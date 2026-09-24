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

            var existing = await db.FundNavs
                .FirstOrDefaultAsync(n => n.FundCode == item.Code && n.TradeDate == q.TradeDate, ct);

            if (existing is null)
            {
                db.FundNavs.Add(new FundNav
                {
                    FundCode = item.Code,
                    TradeDate = q.TradeDate,
                    UnitNav = q.UnitNav,
                    AccNav = q.AccNav,
                    DayChangePercent = q.DayChangePercent,
                    Source = "EASTMONEY",
                    FetchedAt = now,
                });
                newRow = true;
            }
            else
            {
                existing.UnitNav = q.UnitNav;
                existing.AccNav = q.AccNav;
                existing.DayChangePercent = q.DayChangePercent;
                existing.Source = "EASTMONEY";
                existing.FetchedAt = now;
                newRow = false;
            }

            // 当日快照：份额 × 单位净值
            var holding = await db.Holdings.AsNoTracking()
                .FirstOrDefaultAsync(h => h.FundCode == item.Code, ct);
            if (holding is not null)
            {
                var marketValue = Math.Round(holding.Shares * q.UnitNav, 2);
                var snap = await db.DailySnapshots
                    .FirstOrDefaultAsync(s => s.FundCode == item.Code && s.TradeDate == q.TradeDate, ct);

                if (snap is null)
                {
                    db.DailySnapshots.Add(new DailySnapshot
                    {
                        FundCode = item.Code,
                        TradeDate = q.TradeDate,
                        Shares = holding.Shares,
                        UnitNav = q.UnitNav,
                        MarketValue = marketValue,
                        DayChangePercent = q.DayChangePercent,
                        CreatedAt = now,
                    });
                }
                else
                {
                    snap.Shares = holding.Shares;
                    snap.UnitNav = q.UnitNav;
                    snap.MarketValue = marketValue;
                    snap.DayChangePercent = q.DayChangePercent;
                    snap.CreatedAt = now;
                }
            }

            await db.SaveChangesAsync(ct);
            results.Add(new FundRefreshResult(item.Code, true, q.TradeDate, newRow, "EASTMONEY", null));
        }

        var report = new RefreshReport(results, now);
        logger.LogInformation("采集完成：成功 {Ok}，失败 {Fail}", report.SuccessCount, report.FailedCount);
        return report;
    }
}
