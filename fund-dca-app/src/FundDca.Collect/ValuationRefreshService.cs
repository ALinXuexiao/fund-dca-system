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

        foreach (var idx in indexes)
        {
            if (idx.Metric == ValuationMetric.None)
            {
                // 无估值口径（REITs 等）：不采集、不计失败
                continue;
            }

            // 手工维护估值的指数（蛋卷未覆盖，如恒生消费）：保留手工值，不采集、不计失败
            var latestManual = await db.IndexValuations.AsNoTracking()
                .Where(v => v.IndexCode == idx.Code)
                .OrderByDescending(v => v.TradeDate)
                .FirstOrDefaultAsync(ct);
            if (latestManual?.Source == "MANUAL")
            {
                continue;
            }

            var ownCode = DanJuanValuationSource.ToExternalCode(idx.Code);
            var viaProxy = false;
            var resolvedCode = ownCode;

            if (!quotes.ContainsKey(ownCode))
            {
                if (!string.IsNullOrEmpty(idx.ProxyCode))
                {
                    resolvedCode = DanJuanValuationSource.ToExternalCode(idx.ProxyCode);
                    viaProxy = true;
                }
            }

            if (!quotes.TryGetValue(resolvedCode, out var q))
            {
                // 蛋卷（含代理）未覆盖：PE 口径取中证官网该指数自有 PE 历史，按配置窗口现算百分位。
                // 不用"代理估值"记账——这是本指数自己的估值水平。
                if (idx.Metric == ValuationMetric.PeTtm)
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

                        var existingCsi = await db.IndexValuations
                            .FirstOrDefaultAsync(v => v.IndexCode == idx.Code && v.TradeDate == latest.TradeDate, ct);

                        if (existingCsi is null)
                        {
                            db.IndexValuations.Add(new IndexValuation
                            {
                                IndexCode = idx.Code,
                                TradeDate = latest.TradeDate,
                                PeTtm = latest.Pe,
                                PePercentile = pePercentile,
                                Source = "CSI",
                                FetchedAt = now,
                            });
                        }
                        else
                        {
                            existingCsi.PeTtm = latest.Pe;
                            existingCsi.PePercentile = pePercentile;
                            existingCsi.Pb = null;
                            existingCsi.PbPercentile = null;
                            existingCsi.ResolvedCode = null;
                            existingCsi.Source = "CSI";
                            existingCsi.FetchedAt = now;
                        }

                        await db.SaveChangesAsync(ct);
                        results.Add(new ValuationRefreshResult(idx.Code, idx.Name, true,
                            latest.TradeDate.ToString("yyyy-MM-dd"), false, null, pePercentile, null, null));
                        continue;
                    }
                }

                results.Add(new ValuationRefreshResult(idx.Code, idx.Name, false, null, viaProxy, resolvedCode,
                    null, null, "数据源未覆盖该指数（可在档案中配置代理指数）"));
                continue;
            }

            var existing = await db.IndexValuations
                .FirstOrDefaultAsync(v => v.IndexCode == idx.Code && v.TradeDate == q.TradeDate, ct);

            if (existing is null)
            {
                db.IndexValuations.Add(new IndexValuation
                {
                    IndexCode = idx.Code,
                    TradeDate = q.TradeDate,
                    PeTtm = q.PeTtm,
                    PePercentile = q.PePercentile,
                    Pb = q.Pb,
                    PbPercentile = q.PbPercentile,
                    ResolvedCode = viaProxy ? resolvedCode : null,
                    Source = "DANJUAN",
                    FetchedAt = now,
                });
            }
            else
            {
                existing.PeTtm = q.PeTtm;
                existing.PePercentile = q.PePercentile;
                existing.Pb = q.Pb;
                existing.PbPercentile = q.PbPercentile;
                existing.ResolvedCode = viaProxy ? resolvedCode : null;
                existing.Source = "DANJUAN";
                existing.FetchedAt = now;
            }

            await db.SaveChangesAsync(ct);
            results.Add(new ValuationRefreshResult(idx.Code, idx.Name, true, q.TradeDate.ToString("yyyy-MM-dd"),
                viaProxy, viaProxy ? resolvedCode : null, q.PePercentile, q.PbPercentile, null));
        }

        var report = new ValuationRefreshReport(results, now);
        logger.LogInformation("估值采集完成：成功 {Ok}，失败 {Fail}（代理 {Proxy}）",
            report.SuccessCount, report.FailedCount, results.Count(r => r.ViaProxy));
        return report;
    }
}
