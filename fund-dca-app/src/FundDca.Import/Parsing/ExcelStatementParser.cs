using ClosedXML.Excel;
using FundDca.Core.Rules;

namespace FundDca.Import.Parsing;

/// <summary>
/// Excel(.xlsx) 资产证明解析器：扫描工作表定位标准列头，按列名映射代码/名称/份额/市值。
/// 同时兼容用户下载的标准模板与自行整理的表格（列顺序不限、允许额外列）。
/// </summary>
public sealed class ExcelStatementParser : IStatementParser
{
    public string Source => "excel";

    public bool Supports(string fileName) =>
        fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    public Task<ParsedStatement> ParseAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var wb = new XLWorkbook(content);

        List<List<string?>>? bestGrid = null;
        DateOnly? proofDate = null;

        foreach (var ws in wb.Worksheets)
        {
            var grid = ReadGrid(ws);
            var (date, rows) = TabularExtract.FromGrid(grid);
            proofDate ??= date;
            if (rows.Count > 0)
            {
                bestGrid = grid;
                break;
            }
        }

        if (bestGrid is null)
        {
            var emptyWarnings = new List<string>
            {
                "未能从 Excel 中识别到持仓行，请确认包含“基金代码 / 基金名称 / 持有份额 / 当前市值”列头，或下载标准模板填写",
            };
            return Task.FromResult(TabularExtract.ToStatement(
                "excel", fileName, "excel-template-v1", null, [], emptyWarnings));
        }

        var (_, finalRows) = TabularExtract.FromGrid(bestGrid);
        return Task.FromResult(TabularExtract.ToStatement(
            "excel", fileName, "excel-template-v1", proofDate, finalRows, []));
    }

    private static List<List<string?>> ReadGrid(IXLWorksheet ws)
    {
        var grid = new List<List<string?>>();
        var range = ws.RangeUsed();
        if (range is null)
        {
            return grid;
        }

        var maxCol = range.LastColumn().ColumnNumber();
        foreach (var row in ws.RowsUsed())
        {
            var cells = new List<string?>(maxCol);
            for (var c = 1; c <= maxCol; c++)
            {
                var cell = row.Cell(c);
                cells.Add(cell.DataType == XLDataType.Number
                    ? cell.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : cell.GetString());
            }
            grid.Add(cells);
        }
        return grid;
    }
}
