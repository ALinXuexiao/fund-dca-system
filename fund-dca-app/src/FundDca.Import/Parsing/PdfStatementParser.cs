using System.Text.RegularExpressions;
using FundDca.Core.Rules;
using UglyToad.PdfPig;

namespace FundDca.Import.Parsing;

/// <summary>
/// PDF 资产证明解析器（PdfPig，本地提取，文件不出本机）。
/// 真实支付宝/蚂蚁财富资产证明版式固定，但当前没有真实样例可校准坐标模板，
/// 故 M3 先实现“文本提取 + 基金代码定位 + 份额/净值/市值启发式”的通用解析（pdf-generic-v1）：
/// 命中系统内常见“份额 净值 市值”三列时可还原份额与市值；版式不识别时在差异页提示改用 Excel 模板。
/// 待用户提供真实/脱敏 PDF 后，在此追加坐标模板（PRD 7.4 / 8 章）。
/// </summary>
public sealed partial class PdfStatementParser : IStatementParser
{
    public string Source => "pdf";

    public bool Supports(string fileName) =>
        fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(20\d{2})\s*[-年/.]\s*(\d{1,2})\s*[-月/.]\s*(\d{1,2})")]
    private static partial Regex DateRegex();

    public Task<ParsedStatement> ParseAsync(Stream content, string fileName, CancellationToken ct)
    {
        var lines = new List<string>();
        using (var document = PdfDocument.Open(content))
        {
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var text = page.Text ?? string.Empty;
                lines.AddRange(text.Replace("\r\n", "\n").Split('\n'));
            }
        }

        DateOnly? proofDate = null;
        var positions = new Dictionary<string, ParsedPosition>(StringComparer.OrdinalIgnoreCase);
        var unrecognized = 0;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            proofDate ??= TryExtractDate(line);

            var code = StatementText.ExtractCode(line);
            if (code is null)
            {
                continue;
            }

            // 行内数字（剔除代码本身、日期、百分比）；代码可能含字母（如 7R0001）
            decimal? codeNum = decimal.TryParse(code, out var cn) ? cn : null;
            var nums = StatementText.NumbersInLine(line)
                .Where(n => codeNum is null || Math.Abs(n - codeNum.Value) > 0.0001m)
                .ToList();

            var name = ExtractName(line, code);
            var (shares, marketValue) = InferSharesAndValue(nums, name);

            if (shares is null && marketValue is null)
            {
                unrecognized++;
                continue;
            }

            // 同一代码可能跨页眉重复，保留首个识别到数值的行
            if (!positions.ContainsKey(code) || (positions[code].Shares is null && shares is not null))
            {
                positions[code] = new ParsedPosition(code, name, shares, marketValue);
            }
        }

        var warnings = new List<string>
        {
            "PDF 采用通用启发式解析（pdf-generic-v1），请在差异页核对份额/市值；无法识别的行请改用标准 Excel 模板导入",
        };
        if (positions.Count == 0)
        {
            warnings.Add("未能从 PDF 中识别到任何基金持仓行，建议下载标准 Excel 模板填写后导入");
        }
        if (unrecognized > 0)
        {
            warnings.Add($"有 {unrecognized} 行包含基金代码但未能解析出份额/市值，已忽略");
        }

        return Task.FromResult(new ParsedStatement(
            "pdf", fileName, "pdf-generic-v1", proofDate, positions.Values.ToList(), warnings));
    }

    /// <summary>从代码之后、第一个数字之前的片段提取基金名称。</summary>
    private static string ExtractName(string line, string code)
    {
        var idx = line.IndexOf(code, StringComparison.OrdinalIgnoreCase);
        var rest = idx >= 0 ? line[(idx + code.Length)..] : line;
        var m = Regex.Match(rest, @"^[\s\p{IsCJKUnifiedIdeographs}A-Za-z0-9（）()·\-—_+]+?(?=\s*-?\d|\s*$)");
        var name = m.Success ? m.Value : rest;
        return Regex.Replace(name.Trim(), @"\s+", " ");
    }

    /// <summary>
    /// 份额/市值启发式：若存在三个数 a,b,c 且 b 像单位净值（0.05~20）、a×b≈c，则 shares=a、mv=c；
    /// 否则第一个数为份额、第二个为市值；货币类只有一个数时按市值处理。
    /// </summary>
    private static (decimal? Shares, decimal? MarketValue) InferSharesAndValue(List<decimal> nums, string name)
    {
        if (nums.Count == 0)
        {
            return (null, null);
        }

        if (nums.Count >= 3)
        {
            for (var i = 0; i + 2 < nums.Count; i++)
            {
                var a = nums[i];
                var b = nums[i + 1];
                var c = nums[i + 2];
                if (b is >= 0.05m and <= 20m && c > 0m)
                {
                    var ratio = Math.Abs(a * b - c) / c;
                    if (ratio < 0.02m)
                    {
                        return (Math.Round(a, 4), Math.Round(c, 2));
                    }
                }
            }
        }

        var isMoneyLike = name.Contains("货币") || name.Contains("现金") || name.Contains("理财") || name.Contains("余额");
        if (nums.Count == 1)
        {
            return isMoneyLike ? (null, Math.Round(nums[0], 2)) : (Math.Round(nums[0], 4), null);
        }

        return (Math.Round(nums[0], 4), Math.Round(nums[1], 2));
    }

    private static DateOnly? TryExtractDate(string text)
    {
        var m = DateRegex().Match(text);
        if (!m.Success)
        {
            return null;
        }
        if (int.TryParse(m.Groups[1].Value, out var y)
            && int.TryParse(m.Groups[2].Value, out var mo) && mo is >= 1 and <= 12
            && int.TryParse(m.Groups[3].Value, out var d) && d is >= 1 and <= 31)
        {
            return new DateOnly(y, mo, d);
        }
        return null;
    }
}
