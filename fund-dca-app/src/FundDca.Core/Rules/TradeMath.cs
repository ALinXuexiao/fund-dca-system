namespace FundDca.Core.Rules;

/// <summary>
/// 买入流水的纯算术（便于单元测试；EF 层只负责读写库）：
///  预计额份额 = round(金额 / 最近可用净值, 4)
///  T 日确认份额 = round(金额 / T 日收盘净值, 4)
///  持有份额校正差 = 确认份额 - 预计额份额
/// </summary>
public static class TradeMath
{
    public const int SharesScale = 4;

    public static decimal EstimateShares(decimal amount, decimal latestNav) =>
        Math.Round(amount / latestNav, SharesScale);

    public static decimal ConfirmShares(decimal amount, decimal confirmedNav) =>
        Math.Round(amount / confirmedNav, SharesScale);

    /// <summary>净值到位后的份额校正：确认份额与预计额份额之差（加到持有份额上）。</summary>
    public static decimal ShareCorrection(decimal amount, decimal confirmedNav, decimal estimatedShares) =>
        Math.Round(ConfirmShares(amount, confirmedNav) - estimatedShares, SharesScale);

    public static decimal AddShares(decimal current, decimal delta) =>
        Math.Round(current + delta, SharesScale);

    public static decimal AddCost(decimal current, decimal amount) =>
        Math.Round(current + amount, SharesScale);
}