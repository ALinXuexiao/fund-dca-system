using FundDca.Core.Domain;
using FundDca.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FundDca.Collect;

public sealed record ValuationRefreshResult(
    string IndexCode,
    string IndexName,
    bool Success,
    string? TradeDate,
    bool ViaProxy,
    string? ResolvedCode,
    decimal? PePercentile,
    decimal? PbPercentile,
    string? Error);

public sealed record ValuationRefreshReport(
    IReadOnlyList<ValuationRefreshResult> Results,
    DateTimeOffset FinishedAt)
{
    public int SuccessCount => Results.Count(r => r.Success);
    public int FailedCount => Results.Count(r => !r.Success);
    public List<ValuationRefreshResult> Failures => Results.Where(r => !r.Success).ToList();
}

/// <summary>
/// 指数估值采集编排：
/// 1) 一次拉取蛋卷全量估值；
/// 2) 对每个被启用基金跟踪的指数解析外部代码，未覆盖时回落到代理指数（ProxyCode）；
/// 3) upsert index_valuations（按本系统指数代码记账，ResolvedCode 记录实际取数代码）。
/// Metric=None 的指数（REITs 等）直接跳过，记为"无估值口径"。
/// </summary>
public sealed class ValuationRefreshService(
    FundDcaDbContext db,
    DanJuanValuationSource source,
    CsiIndexPeSource csiSource,
    ILogger<ValuationRefreshService> logger)
{
    public async Task<ValuationRefreshReport> RefreshAsync(CancellationToken ct)
    {
        // 仅采集启用基金实际跟踪的指数
        var usedCodes = await db.Funds.AsNoTracking()
            .Where(f => f.IsActive && f.TrackedIndexCode != null)
            .Select(f => f.TrackedIndexCode!)
            .Distinct()
            .ToListAsync(ct);

        var indexes = await db.Indexes.AsNoTracking()
            .Where(i => usedCodes.Contains(i.Code))
            .ToListAsync(ct);

        IReadOnlyDictionary<string, ValuationQuote> quotes;
        try
        {
            quotes = await source.FetchAllAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "蛋卷估值列表拉取失败");
            return new ValuationRefreshReport(
                indexes.Select(i => new ValuationRefreshResult(i.Code, i.Name, false, null, false, null, null, null,
                    $"估值列表拉取失败：{ex.Message}")).ToList(),
                DateTimeOffset.UtcNow);
        }

        var now = DateTimeOffset.UtcNow;
        var results = new List<ValuationRefreshResult>();

        // 每指数最新估值一次性预载（跟踪实体）：循环内只做内存判定与 upsert，最后一次提交，
        // 与指数数量无关地把写路径收敛为 1 次云端往返
        var valByCode = (await db.LatestValuationsTrackedAsync(ct))
            .ToDictionary(v => v.IndexCode);

        foreach (var idx in indexes)
        {
            if (idx.Metric == ValuationMetric.None)
            {
                // 无估值口径（REITs 等）：不采集、不计失败
                continue;
            }

            // 手工维护估值的指数（蛋卷未覆盖，如恒生消费）：保留手工值，不采集、不计失败
            valByCode.TryGetValue(idx.Code, out var latestManual);
            if (latestManual?.Source == "MANUAL")
            {
                continue;
            }

            var ownCode = DanJuanValuationSource.ToExternalCode(idx.Code);

            // 蛋卷估值记账（自有命中 viaProxy=false；代理兜底 viaProxy=true）
            void ApplyDanJuan(ValuationQuote q, bool viaProxy, string? resolvedExt)
            {
                if (valByCode.TryGetValue(idx.Code, out var existing) && existing.TradeDate == q.TradeDate)
                {
                    existing.PeTtm = q.PeTtm;
                    existing.PePercentile = q.PePercentile;
                    existing.Pb = q.Pb;
                    existing.PbPercentile = q.PbPercentile;
                    existing.ResolvedCode = viaProxy ? resolvedExt : null;
                    existing.Source = "DANJUAN";
                    existing.FetchedAt = now;
                }
                else
                {
                    var created = new IndexValuation
                    {
                        IndexCode = idx.Code,
                        TradeDate = q.TradeDate,
                        PeTtm = q.PeTtm,
                        PePercentile = q.PePercentile,
                        Pb = q.Pb,
                        PbPercentile = q.PbPercentile,
                        ResolvedCode = viaProxy ? resolvedExt : null,
                        Source = "DANJUAN",
                        FetchedAt = now,
                    };
                    db.IndexValuations.Add(created);
                    valByCode[idx.Code] = created;
                }
            }

            // ① 首选：蛋卷对该指数"自身"的估值
            if (quotes.TryGetValue(ownCode, out var own))
            {
                ApplyDanJuan(own, false, null);
                results.Add(new ValuationRefreshResult(idx.Code, idx.Name, true,
                    own.TradeDate.ToString("yyyy-MM-dd"), false, null, own.PePercentile, own.PbPercentile, null));
                continue;
            }

            // ② 蛋卷未覆盖：PE 口径取中证官网该指数自有 PE 历史，按配置窗口现算百分位。
            //    盈利收益率（E/P）底层同样是 PE-TTM 现算，也走此分支。
            //    不用"代理估值"记账——这是本指数自己的估值水平。
            if (idx.Metric == ValuationMetric.PeTtm
                || idx.Metric == ValuationMetric.EarningsYield)
            {
                IReadOnlyList<CsiPePoint> series;
                try
                {
                    series = await csiSource.FetchPeHistoryAsync(idx.Code, idx.WindowYears, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "中证官网 PE 拉取失败：{Code}", idx.Code);
                    series = [];
                }

                if (series.Count > 0)
                {
                    var latest = series[^1];
                    var pePercentile = Math.Round(
                        100m * series.Count(p => p.Pe <= latest.Pe) / series.Count, 2);

                    if (valByCode.TryGetValue(idx.Code, out var existingCsi)
                        && existingCsi.TradeDate == latest.TradeDate)
                    {
                        existingCsi.PeTtm = latest.Pe;
                        existingCsi.PePercentile = pePercentile;
                        existingCsi.Pb = null;
                        existingCsi.PbPercentile = null;
                        existingCsi.ResolvedCode = null;
                        existingCsi.Source = "CSI";
                        existingCsi.FetchedAt = now;
                    }
                    else
                    {
                        var created = new IndexValuation
                        {
                            IndexCode = idx.Code,
                            TradeDate = latest.TradeDate,
                            PeTtm = latest.Pe,
                            PePercentile = pePercentile,
                            Source = "CSI",
                            FetchedAt = now,
                        };
                        db.IndexValuations.Add(created);
                        valByCode[idx.Code] = created;
                    }

                    results.Add(new ValuationRefreshResult(idx.Code, idx.Name, true,
                        latest.TradeDate.ToString("yyyy-MM-dd"), false, null, pePercentile, null, null));
                    continue;
                }
            }

            // ③ 蛋卷与中证官网都没有：最后才回落代理指数（如实标注 viaProxy，前端橙色提示）
            if (!string.IsNullOrEmpty(idx.ProxyCode))
            {
                var proxyCode = DanJuanValuationSource.ToExternalCode(idx.ProxyCode);
                if (quotes.TryGetValue(proxyCode, out var proxy))
                {
                    ApplyDanJuan(proxy, true, proxyCode);
                    results.Add(new ValuationRefreshResult(idx.Code, idx.Name, true,
                        proxy.TradeDate.ToString("yyyy-MM-dd"), true, proxyCode,
                        proxy.PePercentile, proxy.PbPercentile, null));
                    continue;
                }
            }

            results.Add(new ValuationRefreshResult(idx.Code, idx.Name, false, null, false, null,
                null, null, "蛋卷与中证官网均未覆盖该指数（可在档案中配置代理指数）"));
        }

        // 全部估值 upsert 一次提交（N 次写往返 → 1 次）
        await db.SaveChangesAsync(ct);

        var report = new ValuationRefreshReport(results, now);
        logger.LogInformation("估值采集完成：成功 {Ok}，失败 {Fail}（代理 {Proxy}）",
            report.SuccessCount, report.FailedCount, results.Count(r => r.ViaProxy));
        return report;
    }
}
