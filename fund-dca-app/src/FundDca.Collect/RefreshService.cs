using FundDca.Core.Domain;
using FundDca.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FundDca.Collect;

/// <summary>
/// 盘后刷新编排：
/// 1) 取全部启用的非货币基金（货币基金不采集净值）；
/// 2) 并行 HTTP 采集（DbContext 不参与并发，写库在采集完成后顺序进行）；
/// 3) 净值 upsert 到 fund_navs；
/// 4) 按当前份额 × 单位净值写当日 daily_snapshots。
/// 单只失败不影响其他基金，失败项进入报告，由前端弹窗提醒。
/// </summary>
public sealed class RefreshService(
    FundDcaDbContext db,
    NavCollector collector,
    ILogger<RefreshService> logger)
{
    public async Task<RefreshReport> RefreshAllAsync(CancellationToken ct)
    {
        var funds = await db.Funds.AsNoTracking()
            .Where(f => f.IsActive && f.Type != FundType.Money && !f.UseManualValue)
            .OrderBy(f => f.Code)
            .Select(f => new { f.Code, f.Name })
            .ToListAsync(ct);

        logger.LogInformation("开始采集 {Count} 只基金净值", funds.Count);

        // 写库所需数据一次性预载（主库在云端，按行查询的往返延迟代价极高）：
        // 持仓（无跟踪）、每基金最新净值/快照（跟踪，用于同行更新）。共 3 次 RTT，与基金数量无关。
        var holdingByCode = await db.Holdings.AsNoTracking()
            .ToDictionaryAsync(h => h.FundCode, ct);
        var navByCode = (await db.LatestNavsTrackedAsync(ct))
            .ToDictionary(n => n.FundCode);
        var snapByCode = (await db.LatestSnapshotsTrackedAsync(ct))
            .ToDictionary(s => s.FundCode);

        // 并行采集（纯 HTTP），单只失败隔离
        var fetched = await Task.WhenAll(funds.Select(async f =>
        {
            try
            {
                var quote = await collector.FetchWithRetryAsync(f.Code, ct);
                return new { f.Code, Quote = quote, Error = (string?)null };
            }
            catch (Exception ex)
            {
                return new { f.Code, Quote = (NavQuote?)null, Error = (string?)ex.Message };
            }
        }));

        var results = new List<FundRefreshResult>();
        var now = DateTimeOffset.UtcNow;

        foreach (var item in fetched)
        {
            if (item.Quote is null)
            {
                results.Add(new FundRefreshResult(item.Code, false, null, false, "EASTMONEY",
                    item.Error ?? "无净值数据"));
                continue;
            }

            var q = item.Quote;
            bool newRow;

            // 采集到的日期只会等于或晚于库内最新日期：相等则更新最新行，否则新增
            if (navByCode.TryGetValue(item.Code, out var existing) && existing.TradeDate == q.TradeDate)
            {
                existing.UnitNav = q.UnitNav;
                existing.AccNav = q.AccNav;
                existing.DayChangePercent = q.DayChangePercent;
                existing.Source = "EASTMONEY";
                existing.FetchedAt = now;
                newRow = false;
            }
            else
            {
                var nav = new FundNav
                {
                    FundCode = item.Code,
                    TradeDate = q.TradeDate,
                    UnitNav = q.UnitNav,
                    AccNav = q.AccNav,
                    DayChangePercent = q.DayChangePercent,
                    Source = "EASTMONEY",
                    FetchedAt = now,
                };
                db.FundNavs.Add(nav);
                navByCode[item.Code] = nav;
                newRow = true;
            }

            // 当日快照：份额 × 单位净值
            if (holdingByCode.TryGetValue(item.Code, out var holding))
            {
                var marketValue = Math.Round(holding.Shares * q.UnitNav, 2);

                if (snapByCode.TryGetValue(item.Code, out var snap) && snap.TradeDate == q.TradeDate)
                {
                    snap.Shares = holding.Shares;
                    snap.UnitNav = q.UnitNav;
                    snap.MarketValue = marketValue;
                    snap.DayChangePercent = q.DayChangePercent;
                    snap.CreatedAt = now;
                }
                else
                {
                    var created = new DailySnapshot
                    {
                        FundCode = item.Code,
                        TradeDate = q.TradeDate,
                        Shares = holding.Shares,
                        UnitNav = q.UnitNav,
                        MarketValue = marketValue,
                        DayChangePercent = q.DayChangePercent,
                        CreatedAt = now,
                    };
                    db.DailySnapshots.Add(created);
                    snapByCode[item.Code] = created;
                }
            }

            results.Add(new FundRefreshResult(item.Code, true, q.TradeDate, newRow, "EASTMONEY", null));
        }

        // 全部 upsert 一次性提交（Npgsql 自动批量打包），把 N 次写往返收敛为 1 次；
        // 镜像同步拦截器也只触发一次，避免采集期间对本地镜像的无效排队
        await db.SaveChangesAsync(ct);

        var report = new RefreshReport(results, now);
        logger.LogInformation("采集完成：成功 {Ok}，失败 {Fail}", report.SuccessCount, report.FailedCount);
        return report;
    }
}
