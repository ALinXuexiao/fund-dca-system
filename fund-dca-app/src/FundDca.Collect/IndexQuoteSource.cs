using System.Text.Json;

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
/// </summary>
public sealed class EastMoneyIndexQuoteSource(HttpClient http)
{
    private const string Endpoint = "https://push2.eastmoney.com/api/qt/ulist.np/get";

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

    /// <summary>批量取指数行情，按 secid 索引（如 1.000300）。取不到的指数不会出现在结果里。</summary>
    public async Task<IReadOnlyDictionary<string, IndexQuote>> FetchAsync(
        IReadOnlyCollection<string> secIds, CancellationToken ct)
    {
        var result = new Dictionary<string, IndexQuote>(StringComparer.OrdinalIgnoreCase);
        if (secIds.Count == 0)
        {
            return result;
        }

        // f12/f13=代码与市场、f14=名称、f3=涨跌幅（×100 整数）、f124=行情时间（Unix 秒）
        var url = $"{Endpoint}?secids={string.Join(',', secIds)}&fields=f12,f13,f14,f3,f124";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://quote.eastmoney.com/");
        req.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("diff", out var diff) ||
            diff.ValueKind != JsonValueKind.Array)
        {
            return result; // 全部未取到（如指数在东财无行情口径）
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

            result[secId] = new IndexQuote(code, name, pct.Value / 100m, quotedAt);
        }

        return result;
    }

    private static decimal? GetDecimal(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDecimal()
            : null;
}