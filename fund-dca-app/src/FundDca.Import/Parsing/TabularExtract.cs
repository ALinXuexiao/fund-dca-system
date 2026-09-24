using System.Text.RegularExpressions;
using FundDca.Core.Rules;

namespace FundDca.Import.Parsing;

/// <summary>
/// 表格类证明（Excel/CSV）的公共提取：定位表头行 → 按列名映射 → 逐行读取
/// 基金代码 / 名称 / 份额 / 市值。对列顺序与额外列宽容，命中内置列名即可。
/// </summary>
internal static class TabularExtract
{
    internal sealed record Row(string? Code, string Name, decimal? Shares, decimal? MarketValue);

    private static readonly string[] CodeKeys = ["基金代码", "代码"];
    private static readonly string[] NameKeys = ["基金名称", "基金简称", "产品名称", "名称"];
    private static readonly string[] ShareKeys = ["持有份额", "可用份额", "总份额", "份额"];
    private static readonly string[] ValueKeys =
        ["参考市值", "持有市值", "资产小计", "最新市值", "市值", "持仓金额", "金额"];

    private static readonly Regex DateRegex =
        new(@"(20\d{2})\s*[-年/.]\s*(\d{1,2})\s*[-月/.]\s*(\d{1,2})", RegexOptions.Compiled);

    /// <summary>把整张表（行→列文本）提取为持仓行与证明日期。</summary>
    public static (DateOnly? ProofDate, List<Row> Rows) FromGrid(IEnumerable<IEnumerable<string?>> grid)
    {
        var rows = grid
            .Select(r => r.Select(c => (c ?? string.Empty).Trim()).ToList())
            .ToList();

        DateOnly? proofDate = null;
        var headerIdx = -1;
        int codeCol = -1, nameCol = -1, shareCol = -1, valueCol = -1;

        for (var i = 0; i < rows.Count; i++)
        {
            // 证明日期可能在标题区（表头之前）
            proofDate ??= ScanDate(rows[i]);

            var r = rows[i];
            var cc = FindColumn(r, CodeKeys);
            var nc = FindColumn(r, NameKeys);
            var sc = FindColumn(r, ShareKeys);
            var vc = FindColumn(r, ValueKeys);
            if (cc >= 0 && nc >= 0 && (sc >= 0 || vc >= 0))
            {
                headerIdx = i;
                codeCol = cc;
                nameCol = nc;
                shareCol = sc;
                valueCol = vc;
                break;
            }
        }

        var result = new List<Row>();
        if (headerIdx < 0)
        {
            return (proofDate, result);
        }

        for (var i = headerIdx + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var line = string.Join(' ', r);
            if (line.Contains("合计") || line.Contains("总计") || line.Contains("小计"))
            {
                continue;
            }

            var codeCell = Cell(r, codeCol);
            var nameCell = Cell(r, nameCol);
            var code = StatementText.ExtractCode(codeCell) ?? StatementText.ExtractCode(nameCell);
            var name = CleanName(nameCell, code);

            var shares = shareCol >= 0 ? StatementText.ParseDecimal(Cell(r, shareCol)) : null;
            var marketValue = valueCol >= 0 ? StatementText.ParseDecimal(Cell(r, valueCol)) : null;

            // 代码、名称、份额、市值四者全无的行视为表尾
            if (code is null && string.IsNullOrWhiteSpace(name) && shares is null && marketValue is null)
            {
                continue;
            }

            result.Add(new Row(code, name, shares, marketValue));
        }

        return (proofDate, result);
    }

    public static ParsedStatement ToStatement(
        string source, string? fileName, string templateVersion,
        DateOnly? proofDate, IEnumerable<Row> rows, IReadOnlyList<string> warnings)
    {
        var positions = rows
            .Select(r => new ParsedPosition(r.Code, r.Name, r.Shares, r.MarketValue))
            .ToList();
        return new ParsedStatement(source, fileName, templateVersion, proofDate, positions, warnings);
    }

    private static string Cell(IReadOnlyList<string> r, int col) =>
        col >= 0 && col < r.Count ? r[col] : string.Empty;

    private static int FindColumn(IReadOnlyList<string> row, string[] keys)
    {
        for (var i = 0; i < row.Count; i++)
        {
            if (keys.Any(k => row[i].Contains(k, StringComparison.Ordinal)))
            {
                return i;
            }
        }
        return -1;
    }

    private static DateOnly? ScanDate(IEnumerable<string> cells)
    {
        foreach (var cell in cells)
        {
            if (string.IsNullOrWhiteSpace(cell))
            {
                continue;
            }
            var m = DateRegex.Match(cell);
            if (m.Success
                && int.TryParse(m.Groups[2].Value, out var month) && month is >= 1 and <= 12
                && int.TryParse(m.Groups[3].Value, out var day) && day is >= 1 and <= 31
                && int.TryParse(m.Groups[1].Value, out var year))
            {
                return new DateOnly(year, month, day);
            }
        }
        return null;
    }

    private static string CleanName(string raw, string? code)
    {
        var name = raw;
        if (code is not null)
        {
            name = name.Replace(code, string.Empty);
        }
        return name.Trim(' ', '-', ':');
    }
}
