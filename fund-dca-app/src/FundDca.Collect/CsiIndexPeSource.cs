using System.Text.Json;

namespace FundDca.Collect;

/// <summary>指数某交易日的市盈率（PE-TTM）观测。</summary>
public sealed record CsiPePoint(DateOnly TradeDate, decimal Pe);

/// <summary>
/// 中证指数官网「指数估值」数据源（公开 JSON，无需鉴权）。
/// 接口字段名为 <c>peg</c>，实际返回的是当日 PE-TTM（官网估值图表即取此值）。
/// 与蛋卷互补：蛋卷只覆盖精选的 60+ 指数，中证官网覆盖全部中证系列指数。
/// </summary>
public sealed class CsiIndexPeSource(HttpClient http)
{
    private const string UrlTemplate =
        "https://www.csindex.com.cn/csindex-home/perf/indexCsiDsPe" +
        "?indexCode={0}&startDate={1:yyyyMMdd}&endDate={2:yyyyMMdd}";

    /// <summary>
    /// 拉取指定指数近 <paramref name="windowYears"/> 年的每日 PE-TTM 序列（按日期升序、按日去重）。
    /// 指数不存在或接口无数据时返回空列表，由调用方决定降级口径。
    /// </summary>
    public async Task<IReadOnlyList<CsiPePoint>> FetchPeHistoryAsync(
        string indexCode, int windowYears, CancellationToken ct)
    {
        var end = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var start = end.AddYears(-Math.Max(1, windowYears));
        var url = string.Format(UrlTemplate, indexCode, start, end);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Referrer = new Uri("https://www.csindex.com.cn/");

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        // 兼容两种包络：{"data":[...]} 或裸 [...]
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.GetProperty("data");

        if (arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var byDate = new SortedDictionary<DateOnly, decimal>();
        foreach (var it in arr.EnumerateArray())
        {
            if (!it.TryGetProperty("tradeDate", out var dEl) ||
                !it.TryGetProperty("peg", out var peEl) ||
                peEl.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var pe = peEl.GetDecimal();
            if (pe <= 0)
            {
                continue;
            }

            var date = DateOnly.ParseExact(dEl.GetString()!, "yyyyMMdd");
            byDate[date] = pe; // 接口偶有非交易日重复值，按日去重
        }

        return byDate.Select(kv => new CsiPePoint(kv.Key, kv.Value)).ToList();
    }
}
