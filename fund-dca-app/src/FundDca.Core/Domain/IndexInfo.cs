namespace FundDca.Core.Domain;

/// <summary>
/// 指数档案：基金跟踪的指数及其估值口径（B1 判定依据）。
/// PE/PB 历史序列与每日百分位在 M1/M5 落表，M0 先保存口径配置。
/// </summary>
public class IndexInfo
{
    /// <summary>指数代码，如 000300（沪深300）、HSTECH（恒生科技）</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>估值参考指标，系统给默认建议，用户可改</summary>
    public ValuationMetric Metric { get; set; } = ValuationMetric.PeTtm;

    /// <summary>
    /// 低估阈值。PE/PB 口径：历史百分位 %，低于该值绿灯（默认 30）；
    /// EarningsYield 口径：盈利收益率绝对值 %，达到/高于该值绿灯（默认 10，此时数值大于高估线）。
    /// </summary>
    public decimal LowThresholdPercent { get; set; } = 30m;

    /// <summary>
    /// 高估阈值。PE/PB 口径：历史百分位 %，达到/高于该值红灯（默认 70）；
    /// EarningsYield 口径：盈利收益率绝对值 %，低于/等于该值红灯（默认 6.4）。
    /// </summary>
    public decimal HighThresholdPercent { get; set; } = 70m;

    /// <summary>百分位历史窗口（年），默认 10；样本不足时按实际样本计算并标注</summary>
    public int WindowYears { get; set; } = 10;

    /// <summary>
    /// 估值代理指数代码（蛋卷等数据源未覆盖本指数时，用高相关指数的百分位代为判定）。
    /// 例如：中证A50→沪深300、中证消费50→中证主要消费。前端需明示"代理估值"。
    /// </summary>
    public string? ProxyCode { get; set; }
}
