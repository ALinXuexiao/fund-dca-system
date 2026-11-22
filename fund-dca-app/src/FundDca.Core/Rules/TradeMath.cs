namespace FundDca.Core.Rules;

/// <summary>
/// 买入/卖出流水的纯算术（便于单元测试；EF 层只负责读写库）。
/// 买入口径（外扣法：申购费从买入金额内扣，与场外基金申购一致）：
///  净申购金额 = round(金额 / (1 + 申购费率), 2)
///  预计额份额 = round(净申购金额 / 最近可用净值, 4)
///  T 日确认份额 = round(净申购金额 / T 日收盘净值, 4)
///  持有份额校正差 = 确认份额 - 预计额份额
/// 买入金额是投资者实付总额（含申购费），只有"净申购金额"买到份额，
/// 故买入后市值恒小于本金，差额即申购费，持有收益率初始为 -申购费率。
/// 卖出口径（赎回费率为 0）：
///  份额 = round(卖出金额 / 净值, 4)
///  结转成本 = 当前周期本金 × 卖出份额 / 原持仓份额（保持剩余持仓的单位成本不变）
/// </summary>
public static class TradeMath
{
    public const int SharesScale = 4;

    /// <summary>
    /// 净申购金额（外扣法）：金额 / (1 + 费率)，四舍五入到分；费率为负按 0 处理。
    /// 申购费率是必传参数（不设默认值），确保任何买入路径都不可能漏掉申购费
    /// —— 2026-10-16 修复：此前份额直接按"金额 / 净值"折算，漏扣了申购费。
    /// </summary>
    public static decimal NetBuyAmount(decimal amount, decimal buyFeeRate) =>
        Math.Round(amount / (1m + Math.Max(0m, buyFeeRate)), 2);

    /// <summary>登记预占份额 = 净申购金额 / 最近可用净值。</summary>
    public static decimal EstimateShares(decimal amount, decimal latestNav, decimal buyFeeRate) =>
        Math.Round(NetBuyAmount(amount, buyFeeRate) / latestNav, SharesScale);

    /// <summary>T 日确认份额 = 净申购金额 / T 日收盘净值。</summary>
    public static decimal ConfirmShares(decimal amount, decimal confirmedNav, decimal buyFeeRate) =>
        Math.Round(NetBuyAmount(amount, buyFeeRate) / confirmedNav, SharesScale);

    /// <summary>净值到位后的份额校正：确认份额与预计额份额之差（加到持有份额上）。</summary>
    public static decimal ShareCorrection(
        decimal amount, decimal confirmedNav, decimal estimatedShares, decimal buyFeeRate) =>
        Math.Round(ConfirmShares(amount, confirmedNav, buyFeeRate) - estimatedShares, SharesScale);

    public static decimal AddShares(decimal current, decimal delta) =>
        Math.Round(current + delta, SharesScale);

    public static decimal AddCost(decimal current, decimal amount) =>
        Math.Round(current + amount, SharesScale);

    /// <summary>卖出预减份额 = 卖出金额 / 最近可用净值（赎回费率 0）。</summary>
    public static decimal EstimateSellShares(decimal amount, decimal latestNav) =>
        Math.Round(amount / latestNav, SharesScale);

    /// <summary>卖出 T 日确认份额 = 卖出金额 / T 日收盘净值（赎回费率 0）。</summary>
    public static decimal ConfirmSellShares(decimal amount, decimal confirmedNav) =>
        Math.Round(amount / confirmedNav, SharesScale);

    /// <summary>
    /// 卖出待确认的份额校正：登记时已按预估份额预减，确认时返回应回补的份额
    /// （预估 - 确认，正值回补、负值继续扣减），直接加到持有份额上。
    /// </summary>
    public static decimal SellShareCorrection(
        decimal amount, decimal confirmedNav, decimal estimatedShares) =>
        Math.Round(estimatedShares - ConfirmSellShares(amount, confirmedNav), SharesScale);

    /// <summary>
    /// 卖出结转成本：按卖出份额占原持仓份额的比例扣减当前周期本金，
    /// 使剩余持仓的单位成本保持不变。成本未知（资产证明不含成本）时返回 0。
    /// </summary>
    public static decimal ProportionalCost(decimal? costAmount, decimal totalShares, decimal soldShares)
    {
        if (costAmount is not { } cost || cost <= 0m || totalShares <= 0m)
        {
            return 0m;
        }

        var ratio = Math.Min(1m, Math.Max(0m, soldShares / totalShares));
        return Math.Round(cost * ratio, SharesScale);
    }
}