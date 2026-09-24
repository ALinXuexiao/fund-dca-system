using System.Text;
using FundDca.Core.Rules;

namespace FundDca.Import.Parsing;

/// <summary>CSV 资产证明解析器（标准模板导出的 UTF-8 CSV）。</summary>
public sealed class CsvStatementParser : IStatementParser
{
    public string Source => "csv";

    public bool Supports(string fileName) =>
        fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedStatement> ParseAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);

        var grid = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(SplitCsvLine)
            .ToList();

        var (proofDate, rows) = TabularExtract.FromGrid(grid);
        var warnings = new List<string>();
        if (rows.Count == 0)
        {
            warnings.Add("未能从 CSV 中识别到持仓行，请确认包含“基金代码 / 基金名称 / 持有份额 / 当前市值”列头");
        }

        return TabularExtract.ToStatement("csv", fileName, "csv-template-v1", proofDate, rows, warnings);
    }

    /// <summary>最小 CSV 拆分（支持双引号包裹与转义逗号）。</summary>
    private static List<string?> SplitCsvLine(string line)
    {
        var cells = new List<string?>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        cells.Add(sb.ToString());
        return cells;
    }
}
