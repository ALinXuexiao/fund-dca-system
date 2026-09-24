using FundDca.Api.Models;
using FundDca.Collect;
using FundDca.Core.Domain;
using FundDca.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FundDca.Api.Services;

/// <summary>
/// 盘中估算编排（不落库，随请求实时算）：
/// 1) 取启用基金的跟踪指数，指数在东财无行情口径时回落到档案配置的代理指数（ProxyCode）；
/// 2) 批量取指数实时涨跌幅（单指数缓存 30 秒，自动刷新每 60 秒一次不会穿透）；
/// 3) 估算盘中净值 = 最新确认净值 × (1 + 指数涨跌幅)，再乘份额得到估算市值与今日盈亏。
///
/// 口径说明：天天基金 2026-07-21 已下线基金净值估算，故改用跟踪指数推算。
/// 指数基金贴合度高；指数增强/联接有跟踪误差，QDII 还叠加汇率与净值滞后，只作参考。
/// </summary>
public sealed class IntradayService(
    FundDcaDbContext db,
    EastMoneyIndexQuoteSource source,
    IMemoryCache cache,
    ILogger<IntradayService> logger)
{
    /// <summary>单指数行情缓存时长：自动刷新 60 秒 + 手动刷新并存时的节流阀</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>行情超过该时长即视为未更新（休市、午休或数据源异常）</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public async Task<IntradayDto> BuildAsync(CancellationToken ct)
    {
        // 只估算有跟踪指数、非手工市值、非货币的启用基金
        var funds = await db.Funds.AsNoTracking()
            .Where(f => f.IsActive && !f.UseManualValue && f.Type != FundType.Money && f.TrackedIndexCode != null)
            .ToListAsync(ct);

        var indexes = (await db.Indexes.AsNoTracking().ToListAsync(ct))
            .ToDictionary(i => i.Code, StringComparer.OrdinalIgnoreCase);

        var holdings = (await db.Holdings.AsNoTracking().ToListAsync(ct))
            .ToDictionary(h => h.FundCode);

        // 每只基金取最新一条净值（开发期数据量小，内存分组）
        var latestNavs = (await db.FundNavs.AsNoTracking().ToListAsync(ct))
            .GroupBy(n => n.FundCode)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(n => n.TradeDate).First());

        // 解析每只基金的候选指数：自有指数 + 档案代理指数。
        // 代理指数一并取数，是因为「自有指数是否有行情」必须等接口返回才知道
        // （如 931769 的 secId 语法合法但东财无行情），合并成一次请求可避免二次往返。
        var picks = new Dictionary<string, (string OwnCode, string? OwnSecId, string? ProxyCode, string? ProxySecId)>();
        var secIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in funds)
        {
            var own = f.TrackedIndexCode!;
            var ownSecId = EastMoneyIndexQuoteSource.ToSecId(own);
            var proxyCode = indexes.GetValueOrDefault(own)?.ProxyCode;
            var proxySecId = string.IsNullOrEmpty(proxyCode) ? null : EastMoneyIndexQuoteSource.ToSecId(proxyCode!);

            if (ownSecId is not null)
            {
                secIds.Add(ownSecId);
            }
            if (proxySecId is not null && !string.Equals(proxySecId, ownSecId, StringComparison.OrdinalIgnoreCase))
            {
                secIds.Add(proxySecId);
            }

            picks[f.Code] = (own, ownSecId, proxyCode, proxySecId);
        }

        var quotes = await GetQuotesAsync(secIds, ct);

        var rows = new List<IntradayRowDto>();
        var covered = 0;
        DateTimeOffset? quotedAt = null;

        foreach (var f in funds)
        {
            var (ownCode, ownSecId, proxyCode, proxySecId) = picks[f.Code];

            // 优先自有指数行情；自有指数东财无行情时回落档案代理指数，并在结果里标注 ViaProxy
            var quote = ownSecId is not null ? quotes.GetValueOrDefault(ownSecId) : null;
            var code = ownCode;
            var viaProxy = false;
            if (quote is null && proxySecId is not null && quotes.GetValueOrDefault(proxySecId) is { } proxyQuote)
            {
                quote = proxyQuote;
                code = proxyCode!;
                viaProxy = true;
            }

            var indexName = indexes.GetValueOrDefault(code)?.Name ?? quote?.Name;

            holdings.TryGetValue(f.Code, out var holding);
            latestNavs.TryGetValue(f.Code, out var nav);

            var reason = holding is null || holding.Shares <= 0m ? "无持仓"
                : nav is null ? "无净值"
                : quote is null ? (ownSecId is null && proxySecId is null ? "该指数无实时行情" : "指数行情未取到")
                : null;

            decimal? estNav = null;
            decimal? estMarketValue = null;
            decimal? estDayPnl = null;

            if (reason is null)
            {
                var est = nav!.UnitNav * (1m + quote!.ChangePercent / 100m);
                estNav = Math.Round(est, 4);
                estMarketValue = Math.Round(holding!.Shares * estNav.Value, 2);
                estDayPnl = Math.Round(holding.Shares * (estNav.Value - nav.UnitNav), 2);
                covered++;
                if (quotedAt is null || quote.QuotedAt > quotedAt)
                {
                    quotedAt = quote.QuotedAt;
                }
            }

            rows.Add(new IntradayRowDto
            {
                FundCode = f.Code,
                IndexCode = code,
                IndexName = indexName,
                ViaProxy = viaProxy,
                IndexChangePercent = reason is null ? quote!.ChangePercent : null,
                LastNav = nav?.UnitNav,
                LastNavDate = nav?.TradeDate.ToString("yyyy-MM-dd"),
                EstimatedNav = estNav,
                EstimatedMarketValue = estMarketValue,
                EstimatedDayPnl = estDayPnl,
                Reason = reason,
            });
        }

        var dto = new IntradayDto
        {
            QuotedAt = quotedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss"),
            QuoteStale = quotedAt is null || DateTimeOffset.UtcNow - quotedAt > StaleAfter,
            CoveredCount = covered,
            UnavailableCount = rows.Count - covered,
            Rows = rows,
        };

        logger.LogInformation("盘中估算完成：可估算 {Ok} 只，不可估算 {Fail} 只（行情 {At}）",
            dto.CoveredCount, dto.UnavailableCount, dto.QuotedAt ?? "无");

        return dto;
    }

    /// <summary>逐个指数查缓存，未命中的合并成一次批量请求，再逐个写回缓存。</summary>
    private async Task<IReadOnlyDictionary<string, IndexQuote>> GetQuotesAsync(
        IReadOnlyCollection<string> secIds, CancellationToken ct)
    {
        var quotes = new Dictionary<string, IndexQuote>(StringComparer.OrdinalIgnoreCase);
        var misses = new List<string>();

        foreach (var secId in secIds)
        {
            if (cache.TryGetValue(CacheKey(secId), out IndexQuote? cached) && cached is not null)
            {
                quotes[secId] = cached;
            }
            else
            {
                misses.Add(secId);
            }
        }

        if (misses.Count == 0)
        {
            return quotes;
        }

        try
        {
            var fetched = await source.FetchAsync(misses, ct);
            foreach (var (secId, quote) in fetched)
            {
                cache.Set(CacheKey(secId), quote, CacheTtl);
                quotes[secId] = quote;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 行情拉取失败不阻断看板：相关基金标记为不可估算
            logger.LogWarning(ex, "指数实时行情拉取失败（{Count} 个指数）", misses.Count);
        }

        return quotes;
    }

    private static string CacheKey(string secId) => $"index-quote:{secId}";
}