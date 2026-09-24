using FundDca.Core.Rules;

namespace FundDca.Import.Parsing;

/// <summary>
/// 内置演示资产证明（不依赖上传文件）：与种子组合对齐，预置两处差异——
/// ① 红利低波份额 +50（默认红利再投）；② 新增一只债券基金（默认归稳健类）。
/// 用于在没有真实样例时走通"解析 → 对账 → 导入 → 回滚"全链路（PRD 5.3 "使用演示文件继续"）。
/// </summary>
public static class DemoStatementProvider
{
    public static readonly DateOnly ProofDate = new(2026, 9, 10);

    public static ParsedStatement Build()
    {
        var positions = new List<ParsedPosition>
        {
            // 净值型：与种子份额一致（市值留空，对账只按份额）
            new("024393", "永赢恒生消费指数(QDII)A", 792.63m, null),
            new("022849", "招商中证A50指数增强A", 217.66m, null),

            // 差异①：份额 514.46 → 564.46（+50），默认红利再投；市值 564.46 × 1.097695
            new("020602", "易方达红利低波ETF联接A", 564.46m, 619.62m),

            new("501060", "中金中证优选300指数(LOF)A", 65.54m, null),
            new("028272", "中金中证REITs全收益指数(FOF)A", 200.87m, null),
            new("160218", "国泰国证房地产行业指数(LOF)A", 183.53m, null),
            new("004672", "华夏短债债券A", 655.74m, null),
            new("008975", "富国中证消费50ETF联接A", 258.76m, null),
            new("015282", "华安恒生科技ETF联接(QDII)A", 178.05m, null),
            new("001550", "天弘中证医药100指数A", 265.35m, null),

            // 差异②：系统中不存在的新债券基金
            new("007403", "安信短债债券C", 900.00m, 900.50m),

            // 手工市值标的（货币/理财）：对账按市值
            new("018092", "兴银现金添利货币C", null, 2030.41m),
            new("7R0001", "7日理财+", null, 1013.13m),
        };

        return new ParsedStatement(
            Source: "demo",
            FileName: "演示资产证明.xlsx",
            TemplateVersion: "demo-v1",
            ProofDate: ProofDate,
            Positions: positions,
            Warnings: []);
    }
}
