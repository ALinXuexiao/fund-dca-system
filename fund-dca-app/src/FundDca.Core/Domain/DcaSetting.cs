namespace FundDca.Core.Domain;

/// <summary>
/// 定投全局设置（单行表，Id 恒为 1）。
/// 用户每周对每只达标基金定投相同的固定金额（当前 50 元，涨工资后可改为 100 元）。
/// </summary>
public class DcaSetting
{
    public int Id { get; set; } = 1;

    /// <summary>单次定投金额（元/只/次），默认 50</summary>
    public decimal FixedInvestAmount { get; set; } = 50m;

    public DateTimeOffset UpdatedAt { get; set; }
}
