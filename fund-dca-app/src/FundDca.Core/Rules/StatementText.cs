using System.Globalization;
using System.Text.RegularExpressions;

namespace FundDca.Core.Rules;

/// <summary>
/// 资产证明文本清洗：基金代码识别、份额/市值数字解析。
/// 支付宝/天天基金导出的数字常带千分位、货币符号、全角字符与单位，统一在此归一。
/// </summary>
public static partial class StatementText
{
    /// <summary>标准 6 位数字基金代码（000001-999999）。</summary>
    public static readonly Regex SixDigitCode = MyRegex();

    /// <summary>带字母的特殊代码（如内部合并标的 7R0001）。</summary>
    public static readonly Regex AlphaCode = MyRegex2();

    [GeneratedRegex(@"(?<![0-9A-Za-z])(\d{6})(?![0-9A-Za-z])")]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"(?<![0-9A-Za-z])([0-9A-Z]{4,8})(?![0-9A-Za-z])")]
    private static partial Regex MyRegex2();

    /// <summary>从一段文本中提取基金代码：优先 6 位数字，其次含字母的短代码。</summary>
    public static string? ExtractCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = SixDigitCode.Match(text);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        foreach (Match am in AlphaCode.Matches(text))
        {
            var v = am.Groups[1].Value;
            if (v.Any(char.IsLetter))
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>把单元格文本解析为金额/份额；失败返回 null。容忍 ¥￥,、空格、全角数字、"份/元"等单位。</summary>
    public static decimal? ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = text.Trim()
            .Replace("，", ",")
            .Replace("．", ".")
            .Replace("％", "%");

        // 全角数字 → 半角
        var chars = s.Select(c => c >= 0xFF10 && c <= 0xFF19 ? (char)(c - 0xFEE0) : c).ToArray();
        s = new string(chars);

        // 抓取第一个带千分位/小数的数字片段
        var m = Regex.Match(s, @"-?\d{1,3}(?:,\d{3})+(?:\.\d+)?|-?\d+(?:\.\d+)?");
        if (!m.Success)
        {
            return null;
        }

        var num = m.Value.Replace(",", "");
        if (decimal.TryParse(num, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0m)
        {
            return d;
        }
        return null;
    }

    /// <summary>从一行中提取所有非负数（剔除日期与百分比），用于 PDF 启发式定位份额/市值。</summary>
    public static List<decimal> NumbersInLine(string line)
    {
        var result = new List<decimal>();
        if (string.IsNullOrWhiteSpace(line))
        {
            return result;
        }

        // 去掉 yyyy-MM-dd / yyyy/MM/dd 日期与百分数，避免被当成份额
        var cleaned = Regex.Replace(line, @"\d{4}[-/]\d{1,2}[-/]\d{1,2}", " ");
        cleaned = Regex.Replace(cleaned, @"-?\d+(?:\.\d+)?\s*%", " ");

        foreach (Match m in Regex.Matches(cleaned, @"-?\d{1,3}(?:,\d{3})+(?:\.\d+)?|-?\d+(?:\.\d+)?"))
        {
            var v = ParseDecimal(m.Value);
            if (v is { } n and > 0m)
            {
                result.Add(n);
            }
        }
        return result;
    }
}
