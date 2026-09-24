namespace FundDca.Core.Domain;

/// <summary>
/// 手工维护市值（货币基金专用）：不采集净值、不模拟收益，
/// 由用户定期填写当前市值，系统直接计入分母 D。
/// </summary>
public class ManualValue
{
    public string FundCode { get; set; } = string.Empty;

    public Fund? Fund { get; set; }

    public decimal MarketValue { get; set; }

    /// <summary>用户填写的市值日期（用于"超过 7 天未更新"提示）</summary>
    public DateOnly ValueDate { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
