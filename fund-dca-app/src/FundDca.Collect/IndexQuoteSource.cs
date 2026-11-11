using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FundDca.Collect;

/// <summary>一条指数实时行情（盘中）。ChangePercent 为百分数，如 -0.85 表示 -0.85%。</summary>
public sealed record IndexQuote(
    string IndexCode,
    string Name,
    decimal ChangePercent,
    DateTimeOffset QuotedAt);

/// <summary>
/// 东方财富 push2 指数实时行情（公开接口，无需鉴权，需 Referer）。
/// 一次请求可取多个指数：GET https://push2.eastmoney.com/api/qt/ulist.np/get?secids=1.000300,124.HSTECH
///
/// 为什么用指数行情而不是基金估值：天天基金 2026-07-21 已下线指数基金净值估算，
/// fundgz 接口返回 404、移动端 FundMNFInfo 的 GSZ/GSZZL 恒为 null，
/// 故盘中估算改为「跟踪指数实时涨跌幅 × 最新确认净值」推算（见 IntradayService）。
///
/// 为什么要在请求层做地址轮换：该域名的 GSLB 会把 A/AAAA 解析到不同边缘节点，
/// 且同一节点对 /api/qt/ulist.np/get 会在建连成功后直接掐断连接（.NET 抛
/// HttpIOException: ResponseEnded）。哪个协议族「好使」会在一天内来回翻转
/// （实测 11-10 上午 v6 断、v4 通，11-11 傍晚 v4 断、v6 通），所以不能押注单一协议族：
/// 每次请求把解析结果按 v4/v6 交替排序、逐个 pin 住重试，全部失败才向上抛。
///
/// 实测边界（2026-11-11 晚，本机连续探测）：批量大小不是变量——1 个 secid 与 13 个 secid
/// 同样的连接被重置，故不做「拆小批」兜底（拆批只会让失败时请求数翻倍，加重限流）。
/// 另外存在一种客户端无解、只能靠退避与快照度过的形态：源站按来源 IP 对 /api/qt/*
/// 做限流时，同一主机 /api/qt/foo 仍正常返回 404、TCP/TLS 也正常，唯独真实行情路径
/// 一律 RST——这时换地址、换协议族、换批量都无效。
///
/// 因此实时接口全失败后，再退回同一提供方的日线接口 push2his/stock/kline/get
/// （不同集群、不同路径，实测覆盖全部 11 个指数并直接返回当日涨跌幅）：
/// 盘中是当日最新一根日线，收盘后即当日收盘值，正好满足「收盘后显示的必须是当天收盘估算值」。
/// </summary>
public sealed class EastMoneyIndexQuoteSource(HttpClient http, ILogger<EastMoneyIndexQuoteSource> logger)
{
    private const string Host = "push2.eastmoney.com";
    private const string Endpoint = "https://" + Host + "/api/qt/ulist.np/get";

    /// <summary>日线兜底接口（同提供方、不同集群与路径）。</summary>
    private const string KlineEndpoint = "https://push2his.eastmoney.com/api/qt/stock/kline/get";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    /// <summary>把单次请求固定到某个解析地址，由 SocketsHttpHandler.ConnectCallback 读取。</summary>
    public static readonly HttpRequestOptionsKey<IPAddress> PinnedAddress =
        new("FundDca.IndexQuote.PinnedAddress");

    /// <summary>一次取数的总耗时上限（含地址轮换与日线兜底），防止 60 秒轮询堆叠成风暴。</summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(15);

    /// <summary>单个地址的单次尝试上限；超时即换下一个地址。</summary>
    private static readonly TimeSpan PerAddressTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 单轮最多尝试的地址数。解析结果目前只有 1 个 A + 1 个 AAAA，
    /// 留 4 个余量即可覆盖 GSLB 多返回几条记录的情况；再多只会在被限流时放大请求量。
    /// </summary>
    private const int MaxAddressAttempts = 4;

    /// <summary>上次成功的协议族（4/6，0=未定），下次优先从它开始，避免每轮都先撞坏掉的一族。</summary>
    private static int _preferredFamily;

    /// <summary>
    /// 日线兜底结果的冷却期：该接口只能逐个 secid 请求，13 个指数就是 13 次请求，
    /// 每 60 秒重来一遍会把限流打得更死。日线本身只有收盘才变化，
    /// 盘中 3 分钟的滞后对「估算」可接受，故 3 分钟内直接复用上次结果。
    /// </summary>
    private static readonly TimeSpan KlineCooldown = TimeSpan.FromMinutes(3);

    private static readonly object KlineGate = new();
    private static Dictionary<string, IndexQuote> _klineSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset _klineFreshUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// 系统内指数代码 → 东财 secid（市场码.代码）。无对应行情口径的返回 null。
    /// 市场码：1=上交所指数、0=深交所/国证指数、2=中证指数、124=港股指数。
    /// </summary>
    public static string? ToSecId(string code) => code switch
    {
        // 港股指数（必须在 H 系列之前判定，避免被当作中证 H 系列）
        "HSTECH" => "124.HSTECH",
        "HSCGSI" => "124.HSCGSI",
        "HSI" => "124.HSI",
        // 深交所 / 国证
        _ when code.StartsWith("399", StringComparison.Ordinal) => $"0.{code}",
        // 中证指数：93xxxx 与 H 系列（H30269 等）
        _ when code.StartsWith("93", StringComparison.Ordinal) => $"2.{code}",
        _ when code.StartsWith('H') && code.Length == 6 => $"2.{code}",
        // 上交所指数（沪深300、中证医药100 等均为 000xxx）
        _ when code.Length == 6 && code.StartsWith("000", StringComparison.Ordinal) => $"1.{code}",
        _ => null,
    };

    /// <summary>
    /// 批量取指数行情，按 secid 索引（如 1.000300）。取不到的指数不会出现在结果里。
    /// 逐个地址尝试，任一地址拿到非空行情即返回；全部地址都失败才抛出
    /// （交由调用方记 warn 并进入退避）。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IndexQuote>> FetchAsync(
        IReadOnlyCollection<string> secIds, CancellationToken ct)
    {
        if (secIds.Count == 0)
        {
            return new Dictionary<string, IndexQuote>(StringComparer.OrdinalIgnoreCase);
        }

        var clock = Stopwatch.StartNew();
        var ids = secIds.ToArray();
        var addresses = await ResolveOrderedAsync(ct);
        Exception? last = null;
        var tried = 0;

        foreach (var address in addresses)
        {
            if (tried >= MaxAddressAttempts || clock.Elapsed >= TotalBudget)
            {
                logger.LogInformation("已达地址数/总耗时上限（已试 {Tried} 个地址，{Elapsed:0.0}s），停止本轮尝试",
                    tried, clock.Elapsed.TotalSeconds);
                break;
            }

            tried++;
            try
            {
                var quotes = await FetchFromAsync(ids, address, clock, ct);
                if (quotes.Count > 0)
                {
                    Volatile.Write(ref _preferredFamily,
                        address.AddressFamily == AddressFamily.InterNetwork ? 4 : 6);
                    return quotes;
                }

                last = new HttpRequestException($"{address} 返回空行情数据");
                logger.LogInformation("行情请求经 {Address} 返回空数据，换下一个地址", address);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                last = ex;
                logger.LogInformation("行情请求经 {Address} 失败（{Reason}），换下一个地址", address, ex.Message);
            }
        }

        // 实时接口在所有地址上都没拿到数据：退回日线接口（同提供方、不同集群与路径）。
        // 盘中它给当日最新一根日线（即当前涨跌幅），收盘后给当日收盘涨跌幅，
        // 保证看板不因单一路径被掐而整批归零。
        // 冷却期内（3 分钟）直接复用上次日线结果，避免每轮 13 次请求把限流打得更死。
        var now = DateTimeOffset.UtcNow;
        lock (KlineGate)
        {
            if (now < _klineFreshUntil && _klineSnapshot.Count > 0)
            {
                logger.LogInformation("实时行情接口未取到数据，沿用 {Count} 个指数的上次日线（冷却中）",
                    _klineSnapshot.Count);
                return new Dictionary<string, IndexQuote>(_klineSnapshot, StringComparer.OrdinalIgnoreCase);
            }
        }

        var viaKline = await FetchViaKlineAsync(ids, clock, ct);
        if (viaKline.Count > 0)
        {
            lock (KlineGate)
            {
                _klineSnapshot = viaKline;
                _klineFreshUntil = DateTimeOffset.UtcNow + KlineCooldown;
            }
            logger.LogInformation("实时行情接口未取到数据，日线兜底取到 {Ok}/{Total} 个指数",
                viaKline.Count, ids.Length);
            return viaKline;
        }

        throw last ?? new HttpRequestException("没有可尝试的解析地址");
    }

    /// <summary>
    /// 日线兜底：逐个 secid 取当日最新一根日线（klt=101, lmt=1），成功几个返回几个。
    /// 逐个请求是因为该接口不支持批量；单个失败只跳过它自己，不影响其它指数。
    /// </summary>
    private async Task<Dictionary<string, IndexQuote>> FetchViaKlineAsync(
        string[] secIds, Stopwatch clock, CancellationToken ct)
    {
        var result = new Dictionary<string, IndexQuote>(StringComparer.OrdinalIgnoreCase);

        foreach (var secId in secIds)
        {
            if (clock.Elapsed >= TotalBudget)
            {
                logger.LogInformation("日线兜底已达总耗时上限 {Budget} 秒，停止尝试", TotalBudget.TotalSeconds);
                break;
            }

            try
            {
                if (await FetchKlineOneAsync(secId, ct) is { } quote)
                {
                    result[secId] = quote;
                }
                else
                {
                    logger.LogInformation("日线兜底：{SecId} 无当日行情", secId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                logger.LogInformation("日线兜底：{SecId} 请求失败（{Reason}）", secId, ex.Message);
            }
        }

        return result;
    }

    /// <summary>取单个指数当日最新一根日线，解析出涨跌幅；无当日数据返回 null。</summary>
    private async Task<IndexQuote?> FetchKlineOneAsync(string secId, CancellationToken ct)
    {
        // f51=日期、f53=收盘价、f59=涨跌幅（百分数，如 -1.71 表示 -1.71%）
        var url = $"{KlineEndpoint}?secid={secId}&fields1=f1&fields2=f51,f53,f59&klt=101&fqt=1&end=20500101&lmt=1";
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(PerAddressTimeout);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://quote.eastmoney.com/");
        req.Headers.UserAgent.ParseAdd(UserAgent);

        using var resp = await http.SendAsync(req, attemptCts.Token);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(attemptCts.Token);
        return string.IsNullOrWhiteSpace(body) ? null : ParseKline(secId, body);
    }

    /// <summary>解析日线兜底响应；没有当日数据或涨跌幅越界时返回 null。</summary>
    private static IndexQuote? ParseKline(string secId, string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("klines", out var klines) ||
            klines.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var last = klines.EnumerateArray().LastOrDefault();
        if (last.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var parts = (last.GetString() ?? string.Empty).Split(',');
        if (parts.Length < 3 ||
            !DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", out var date) ||
            !decimal.TryParse(parts[2], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var pct))
        {
            return null;
        }

        // 合理性守卫同实时接口：指数单日涨跌幅不可能超过 ±15%
        // （这里 pct 已经是百分数，实时接口那边是 ×100 的整数，故阈值不同）
        if (Math.Abs(pct) > 15m)
        {
            return null;
        }

        var name = data.TryGetProperty("name", out var n) ? n.GetString() ?? secId : secId;

        // 日线只到“哪一天”，没有盘中时刻；按当日 15:00（收盘）计，
        // 这样 5 分钟未更新判据在收盘后照常生效，与改动前行为一致。
        var quotedAt = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 0)), TimeSpan.FromHours(8));
        return new IndexQuote(secId[(secId.IndexOf('.') + 1)..], name, pct, quotedAt);
    }

    /// <summary>把请求 pin 到指定地址发一次，解析出行情。</summary>
    private async Task<IReadOnlyDictionary<string, IndexQuote>> FetchFromAsync(
        string[] secIds, IPAddress address, Stopwatch clock, CancellationToken ct)
    {
        var remaining = TotalBudget - clock.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("取数总耗时已用尽");
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(remaining < PerAddressTimeout ? remaining : PerAddressTimeout);

        // f12/f13=代码与市场、f14=名称、f3=涨跌幅（×100 整数）、f124=行情时间（Unix 秒）
        var url = $"{Endpoint}?secids={string.Join(',', secIds)}&fields=f12,f13,f14,f3,f124";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Options.Set(PinnedAddress, address);
        req.Headers.Referrer = new Uri("https://quote.eastmoney.com/");
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await http.SendAsync(req, attemptCts.Token);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(attemptCts.Token);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new HttpRequestException($"{address} 返回空响应体");
        }

        return ParseQuotes(body);
    }

    /// <summary>解析 push2 返回；结构不符（如 data 为 null）时返回空字典，由调用方按失败处理。</summary>
    private static Dictionary<string, IndexQuote> ParseQuotes(string body)
    {
        var result = new Dictionary<string, IndexQuote>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("diff", out var diff) ||
            diff.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var it in diff.EnumerateArray())
        {
            var code = it.TryGetProperty("f12", out var c) ? c.GetString() : null;
            var market = it.TryGetProperty("f13", out var m) && m.TryGetInt32(out var mk) ? mk : (int?)null;
            var pct = GetDecimal(it, "f3");
            if (code is null || market is null || pct is null)
            {
                continue;
            }

            var secId = $"{market}.{code}";
            var name = it.TryGetProperty("f14", out var n) ? n.GetString() ?? code : code;
            var quotedAt = it.TryGetProperty("f124", out var t) && t.TryGetInt64(out var secs) && secs > 0
                ? DateTimeOffset.FromUnixTimeSeconds(secs)
                : DateTimeOffset.UtcNow;

            // 合理性守卫：f3 是"百分数 × 100"（32 表示 0.32%）。指数为一篮子证券，
            // A 股/港股在跟指数单日涨跌幅不可能超过 ±15%；东财在集合竞价/开盘初偶发
            // 下发脏值（如 2026-10-08 开盘初 931069 一度返回 36%），超限一律不采信，
            // 按"行情未取到"处理，避免估算净值与当日盈亏被放大百倍。30 秒后下轮自动恢复。
            if (Math.Abs(pct.Value) > 1500m)
            {
                continue;
            }

            result[secId] = new IndexQuote(code, name, pct.Value / 100m, quotedAt);
        }

        return result;
    }

    /// <summary>解析结果按 v4/v6 交替排序，起始协议族取上次成功的那一族。</summary>
    private static async Task<IPAddress[]> ResolveOrderedAsync(CancellationToken ct)
    {
        var all = await Dns.GetHostAddressesAsync(Host, ct);
        var v4 = all.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        var v6 = all.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToArray();

        var preferV6 = Volatile.Read(ref _preferredFamily) == 6;
        var first = preferV6 ? v6 : v4;
        var second = preferV6 ? v4 : v6;

        var ordered = new List<IPAddress>(all.Length);
        for (var i = 0; i < Math.Max(first.Length, second.Length); i++)
        {
            if (i < first.Length) ordered.Add(first[i]);
            if (i < second.Length) ordered.Add(second[i]);
        }

        return ordered.ToArray();
    }

    /// <summary>
    /// 只有传输层问题才换地址重试：连接被重置、响应被掐断（ResponseEnded）、超时。
    /// HTTP 状态码异常不在此列（那是接口语义问题，重试无益）。
    /// </summary>
    private static bool IsRetryable(Exception ex) => ex switch
    {
        OperationCanceledException => true,   // 调用方未取消时，这里是单地址超时
        TimeoutException => true,
        HttpRequestException => true,         // 含 HttpIOException(ResponseEnded)
        IOException => true,
        SocketException => true,
        _ => false,
    };

    private static decimal? GetDecimal(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDecimal()
            : null;
}
