using FundDca.Api.Models;
using FundDca.Collect;
using FundDca.Core.Domain;
using FundDca.Core.Rules;
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
    IndexQuoteFetchState quoteState,
    IMemoryCache cache,
    ILogger<IntradayService> logger)
{
    /// <summary>单指数行情缓存时长：自动刷新 60 秒 + 手动刷新并存时的节流阀</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>行情超过该时长即视为未更新（休市、午休或数据源异常）</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public async Task<IntradayDto> BuildAsync(CancellationToken ct)
    {
        // 周末：行情源不产生新数据，取到的只是上一交易日收盘快照，按「今日」展示会误导
        // （且会把那个涨跌幅重复乘到已确认的净值上）。直接返回非交易日，不连数据库、不请求行情源。
        if (IntradayEstimate.IsWeekend(DateTimeOffset.Now))
        {
            logger.LogInformation("非交易日（周末），跳过盘中估算，不请求行情源");
            return new IntradayDto
            {
                IsTradingDay = false,
                // 非交易日不属于「行情未更新」故障，故不走 5 分钟判据
                QuoteStale = false,
                CoveredCount = 0,
                UnavailableCount = 0,
                Rows = [],
            };
        }

        // 只估算有跟踪指数、非手工市值、非货币的启用基金
        var funds = await db.Funds.AsNoTracking()
            .Where(f => f.IsActive && !f.UseManualValue && f.Type != FundType.Money && f.TrackedIndexCode != null)
            .ToListAsync(ct);

        // 指数档案需要连同代理指数一起解析，表本身极小（十几个指数），整表取回
        var indexes = (await db.Indexes.AsNoTracking().ToListAsync(ct))
            .ToDictionary(i => i.Code, StringComparer.OrdinalIgnoreCase);

        var fundCodes = funds.Select(f => f.Code).ToHashSet();
        var holdings = (await db.Holdings.AsNoTracking()
                .Where(h => fundCodes.Contains(h.FundCode))
                .ToListAsync(ct))
            .ToDictionary(h => h.FundCode);

        // 每只基金取最新一条净值（DISTINCT ON）；本接口每 60 秒轮询一次，
        // 不能把不断增长的净值全表历史反复跨网传输
        var latestNavs = (await db.LatestNavsAsync(ct))
            .ToDictionary(n => n.FundCode);

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
        var confirmed = 0;
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

            // 行情缺失时退化为「今天」：此时若当日净值已入库，仍然可以用真实净值涨跌算当日盈亏
            var quoteDate = quote is null
                ? DateOnly.FromDateTime(DateTimeOffset.Now.ToOffset(IntradayEstimate.MarketOffset).DateTime)
                : IntradayEstimate.QuoteTradeDate(quote.QuotedAt);

            // 净值已覆盖行情交易日 = 当日涨跌已计入净值：不能再乘指数涨跌幅，
            // 改用基金自身净值涨跌幅算真实当日盈亏（口径与看板 dayPnl 一致）。
            var navCovered = nav is not null && IntradayEstimate.NavCoversQuote(nav.TradeDate, quoteDate);

            var reason = holding is null || holding.Shares <= 0m ? "无持仓"
                : nav is null ? "无净值"
                : navCovered
                    ? (nav.DayChangePercent is null ? $"当日净值已确认（{nav.TradeDate:yyyy-MM-dd}），缺少当日涨跌幅" : null)
                    : quote is null ? (ownSecId is null && proxySecId is null ? "该指数无实时行情" : "指数行情未取到")
                    : null;

            // 行情时间与是否估算无关：只要取到了行情就记录下来，
            // 让「指数行情」列展示真实取数时间，5 分钟未更新判据照旧按它计算。
            if (quote is not null && (quotedAt is null || quote.QuotedAt > quotedAt))
            {
                quotedAt = quote.QuotedAt;
            }

            decimal? estNav = null;
            decimal? estMarketValue = null;
            decimal? estDayPnl = null;
            decimal? navChangePercent = null;
            decimal? confirmedDayPnl = null;

            if (reason is null)
            {
                if (navCovered)
                {
                    navChangePercent = nav!.DayChangePercent;
                    confirmedDayPnl = IntradayEstimate.ConfirmedDayPnl(
                        holding!.Shares, nav.UnitNav, nav.DayChangePercent!.Value);
                    confirmed++;
                }
                else
                {
                    estNav = IntradayEstimate.EstimatedNav(nav!.UnitNav, quote!.ChangePercent);
                    estMarketValue = IntradayEstimate.EstimatedMarketValue(holding!.Shares, estNav.Value);
                    estDayPnl = IntradayEstimate.EstimatedDayPnl(holding.Shares, estNav.Value, nav.UnitNav);
                    covered++;
                }
            }

            rows.Add(new IntradayRowDto
            {
                FundCode = f.Code,
                IndexCode = code,
                IndexName = indexName,
                ViaProxy = viaProxy,
                IndexChangePercent = estNav is not null ? quote!.ChangePercent : null,
                NavChangePercent = navChangePercent,
                LastNav = nav?.UnitNav,
                LastNavDate = nav?.TradeDate.ToString("yyyy-MM-dd"),
                EstimatedNav = estNav,
                EstimatedMarketValue = estMarketValue,
                EstimatedDayPnl = estDayPnl,
                ConfirmedDayPnl = confirmedDayPnl,
                Reason = reason,
            });
        }

        var dto = new IntradayDto
        {
            QuotedAt = quotedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss"),
            QuoteStale = quotedAt is null || DateTimeOffset.UtcNow - quotedAt > StaleAfter,
            CoveredCount = covered,
            ConfirmedCount = confirmed,
            DayPnlTotal = IntradayEstimate.TotalDayPnl(
                rows.Select(r => (r.ConfirmedDayPnl, r.EstimatedDayPnl))),
            UnavailableCount = rows.Count - covered - confirmed,
            Rows = rows,
        };

        logger.LogInformation(
            "盘中估算完成：指数估算 {Ok} 只，净值已确认 {Confirmed} 只，不可估算 {Fail} 只（行情 {At}）",
            dto.CoveredCount, dto.ConfirmedCount, dto.UnavailableCount, dto.QuotedAt ?? "无");

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

        var now = DateTimeOffset.UtcNow;

        // 退避期内不再打行情源（东财间歇性拒绝时继续 60 秒重试只会加重拒绝），
        // 沿用上一次成功行情，保证看板不因一次失败整批归零。
        if (!quoteState.CanAttempt(now))
        {
            FillFromLastKnown(misses, quotes);
            logger.LogInformation(
                "行情源退避中，跳过本轮取数（{Count} 个指数沿用上次行情）", misses.Count);
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
            quoteState.OnSuccess(fetched);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var delay = quoteState.OnFailure(now);

            // 行情拉取失败不阻断看板：相关基金沿用上次成功行情，没有历史的才标记为不可估算
            FillFromLastKnown(misses, quotes);
            logger.LogWarning(ex,
                "指数实时行情拉取失败（{Count} 个指数），退避 {Delay} 秒后再试", misses.Count, delay.TotalSeconds);
        }

        return quotes;
    }

    /// <summary>用上一次成功行情补位，避免一次网络失败让整批估算归零。</summary>
    private void FillFromLastKnown(IEnumerable<string> secIds, Dictionary<string, IndexQuote> quotes)
    {
        foreach (var secId in secIds)
        {
            if (quotes.ContainsKey(secId))
            {
                continue;
            }
            if (quoteState.TryGetLast(secId, out var last) && last is not null)
            {
                quotes[secId] = last;
            }
        }
    }

    private static string CacheKey(string secId) => $"index-quote:{secId}";
}

/// <summary>
/// 指数行情取数的退避状态与“上一次成功行情”（单例，跨请求共享）。
/// 失败后按 60/120/240 秒指数退避，成功即复位；退避期间不请求行情源。
/// </summary>
public sealed class IndexQuoteFetchState
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(240),
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, IndexQuote> _lastQuotes = new(StringComparer.OrdinalIgnoreCase);
    private int _failures;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;

    /// <summary>当前是否允许再请求行情源（退避期外）。</summary>
    public bool CanAttempt(DateTimeOffset now)
    {
        lock (_gate)
        {
            return now >= _nextAttemptAt;
        }
    }

    /// <summary>取某个指数上一次成功的行情。</summary>
    public bool TryGetLast(string secId, out IndexQuote? quote)
    {
        lock (_gate)
        {
            return _lastQuotes.TryGetValue(secId, out quote);
        }
    }

    /// <summary>成功：复位退避并刷新最近行情。</summary>
    public void OnSuccess(IReadOnlyDictionary<string, IndexQuote> quotes)
    {
        lock (_gate)
        {
            _failures = 0;
            _nextAttemptAt = DateTimeOffset.MinValue;
            foreach (var (secId, quote) in quotes)
            {
                _lastQuotes[secId] = quote;
            }
        }
    }

    /// <summary>失败：按 60→120→240 秒退避，返回本次退避时长。</summary>
    public TimeSpan OnFailure(DateTimeOffset now)
    {
        lock (_gate)
        {
            var delay = Backoff[Math.Min(_failures, Backoff.Length - 1)];
            _failures++;
            _nextAttemptAt = now + delay;
            return delay;
        }
    }
}