using ClosedXML.Excel;

namespace FundDca.Import.Parsing;

/// <summary>
/// 标准导入 Excel 模板：PDF 版式无法识别时的兜底（PRD 5.3）。
/// 用户按列填写基金代码/名称/份额/市值，解析器按列名映射，列顺序不敏感。
/// </summary>
public static class StandardTemplateBuilder
{
    public const string FileName = "基金资产证明导入模板.xlsx";

    public static byte[] Build()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("持仓");

        ws.Range("A1:F1").Merge();
        ws.Cell(1, 1).Value = "基金资产证明标准导入模板";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;

        ws.Cell(2, 1).Value = "证明日期：";
        ws.Cell(2, 2).Value = DateTime.Today.ToString("yyyy-MM-dd");

        var headers = new[] { "基金代码", "基金名称", "持有份额", "当前市值", "收益率", "备注" };
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(4, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#EAF1FB");
        }

        // 示例行（灰色斜体，导入前删除即可；解析器会把它们当普通行，故明确标注示例）
        var samples = new (string Code, string Name, double Shares, double Value, double? Yield, string Note)[]
        {
            ("110020", "示例：沪深300ETF联接A（净值型填写份额）", 3200, 5478.40, 8.2, "示例行，导入前请删除"),
            ("018092", "示例：货币/理财只填当前市值，份额留空", 0, 2000.00, null, "示例行，导入前请删除"),
        };
        for (var r = 0; r < samples.Length; r++)
        {
            var s = samples[r];
            ws.Cell(5 + r, 1).Value = s.Code;
            ws.Cell(5 + r, 2).Value = s.Name;
            if (s.Shares > 0)
            {
                ws.Cell(5 + r, 3).Value = s.Shares;
            }
            ws.Cell(5 + r, 4).Value = s.Value;
            if (s.Yield is { } y)
            {
                ws.Cell(5 + r, 5).Value = y;
            }
            ws.Cell(5 + r, 6).Value = s.Note;
            for (var c = 1; c <= 6; c++)
            {
                ws.Cell(5 + r, c).Style.Font.Italic = true;
                ws.Cell(5 + r, c).Style.Font.FontColor = XLColor.Gray;
            }
        }

        ws.Cell(8, 1).Value =
            "填写说明：① 基金代码为 6 位数字；② 股票/混合/债券/QDII 填“持有份额”，市值可留空（系统按份额×净值计算）；"
            + "③ 货币基金/银行理财只填“当前市值”；④ 份额增加但没有申购扣款时，导入向导会默认判定为红利再投（不加本金）；"
            + "⑤ “收益率”填该基金持仓累计涨跌幅（8.2 或 8.2% 均表示盈利 8.2%，亏损填负数如 -3.5），新基金建档时按 当前市值÷(1+收益率) 反推初始本金，"
            + "建议与“当前市值”同时填写；已在系统中的基金无需填写，本金以系统流水为准。";
        ws.Range("A8:F8").Merge();
        ws.Cell(8, 1).Style.Alignment.WrapText = true;
        ws.Cell(8, 1).Style.Font.FontColor = XLColor.Gray;
        ws.Row(8).Height = 62;

        ws.Column(1).Width = 12;
        ws.Column(2).Width = 42;
        ws.Column(3).Width = 12;
        ws.Column(4).Width = 12;
        ws.Column(5).Width = 12;
        ws.Column(6).Width = 24;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
