using System.Text.Json;

namespace FundDca.Collect;

/// <summary>
/// 天天基金（东方财富）历史净值公开接口。
/// GET https://api.fund.eastmoney.com/f10/lsjz?fundCode=xxxx&pageIndex=1&pageSize=1
/// 需要 Referer: https://fundf10.eastmoney.com/，否则返回 403。
/// 返回最新一个交易日的单位净值、累计净值、日涨跌幅；QDII 最新日期自然晚一天。
/// </summary>
public sealed class EastMoneyNavSource(HttpClient http) : INavQuoteSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<NavQuote?> GetLatestAsync(string fundCode, CancellationToken ct)
    {
        var url = $"https://api.fund.eastmoney.com/f10/lsjz?fundCode={fundCode}&pageIndex=1&pageSize=1&startDate=&endDate=";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://fundf10.eastmoney.com/");
        req.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (!root.TryGetProperty("Data", out var data) ||
            !data.TryGetProperty("LSJZList", out var list) ||
            list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() == 0)
        {
            return null; // 新基金可能暂无净值
        }

        var row = list[0];
        var date = DateOnly.Parse(row.GetProperty("FSRQ").GetString()!);
        var unitNav = ParseDecimal(row, "DWJZ")
            ?? throw new InvalidDataException($"基金 {fundCode} 单位净值字段为空");
        var accNav = ParseDecimal(row, "LJJZ");
        var change = ParseDecimal(row, "JZZZL");

        // 合理性守卫：场外基金单日确认涨幅超 ±25% 只可能源于份额折算、巨额赎回费计入
        // 或数据源脏值（如开盘初占位数据），此时涨幅口径不可信——净值本身仍采信，
        // 仅把涨幅置空，避免看板"日涨跌/当日盈亏"被污染。
        if (change is { } c && Math.Abs(c) > 25m)
        {
            change = null;
        }

        return new NavQuote(date, unitNav, accNav, change);
    }

    private static decimal? ParseDecimal(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var s = el.GetString();
        return decimal.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

public interface INavQuoteSource
{
    Task<NavQuote?> GetLatestAsync(string fundCode, CancellationToken ct);
}
