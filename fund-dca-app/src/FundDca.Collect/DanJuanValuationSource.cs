using System.Text.Json;
using System.Text.Json.Serialization;

namespace FundDca.Collect;

/// <summary>一条指数估值报价（百分位统一为 0-100 百分数）。</summary>
public sealed record ValuationQuote(
    string ExternalCode,
    DateOnly TradeDate,
    decimal? PeTtm,
    decimal? PePercentile,
    decimal? Pb,
    decimal? PbPercentile);

/// <summary>
/// 蛋卷基金指数估值数据源（公开 JSON，无需鉴权）。
/// 一次请求返回全部指数（约 60+），按外部代码索引；代码带交易所前缀（SH/SZ/CSI/HK）。
/// </summary>
public sealed class DanJuanValuationSource(HttpClient http)
{
    private const string Url = "https://danjuanfunds.com/djapi/index_eva/dj";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>系统内裸代码 → 蛋卷外部代码。未列出的代码若带前缀可直接透传。</summary>
    public static string ToExternalCode(string code) => code switch
    {
        // 上交所
        _ when code.StartsWith("000", StringComparison.Ordinal) && code.Length == 6 => $"SH{code}",
        // 深交所/国证
        _ when code.StartsWith("399", StringComparison.Ordinal) => $"SZ{code}",
        // 中证 H 系列（H30269 等）
        _ when code.StartsWith('H') && code != "HSTECH" => $"CSI{code}",
        // 港股
        "HSTECH" => "HKHSTECH",
        "HSI" => "HKHSI",
        _ => code,
    };

    public async Task<IReadOnlyDictionary<string, ValuationQuote>> FetchAllAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
        req.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var items = doc.RootElement.GetProperty("data").GetProperty("items");

        var map = new Dictionary<string, ValuationQuote>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items.EnumerateArray())
        {
            var ext = it.GetProperty("index_code").GetString()!;
            var ts = it.GetProperty("ts").GetInt64();
            var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ts)
                .ToOffset(TimeSpan.FromHours(8)).Date);

            map[ext] = new ValuationQuote(
                ext,
                date,
                GetDecimal(it, "pe") is { } pe && pe > 0 ? pe : null,
                ToPercent(GetDecimal(it, "pe_percentile")),
                GetDecimal(it, "pb") is { } pb && pb > 0 ? pb : null,
                ToPercent(GetDecimal(it, "pb_percentile")));
        }

        return map;
    }

    private static decimal? GetDecimal(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDecimal()
            : null;

    /// <summary>蛋卷给 0-1 小数，统一转 0-100（保留 2 位）；空值透传。</summary>
    private static decimal? ToPercent(decimal? v) => v is null ? null : Math.Round(v.Value * 100m, 2);
}
