namespace FundDca.Core.Domain;

/// <summary>
/// 赎回费率分档（按持有天数）。资产证明不含费率，待用户提供后录入。
/// 例如：持有 0-6 天 1.5%，7-29 天 0.75%，30-364 天 0.5%，≥730 天 0%。
/// </summary>
public class RedeemFeeTier
{
    /// <summary>本档起始持有天数（含）</summary>
    public int MinHoldingDays { get; set; }

    /// <summary>本档结束持有天数（含）；null 表示无上限</summary>
    public int? MaxHoldingDays { get; set; }

    /// <summary>赎回费率（小数，0.005 = 0.5%）</summary>
    public decimal Rate { get; set; }
}
