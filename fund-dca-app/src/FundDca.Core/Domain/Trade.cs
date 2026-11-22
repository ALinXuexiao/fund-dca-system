namespace FundDca.Core.Domain;

/// <summary>交易流水方向。</summary>
public enum TradeSide
{
    Buy = 1,
    Sell = 2,
}

/// <summary>交易入账状态。</summary>
public enum TradeStatus
{
    /// <summary>已登记、已预入账（预算/本金先占），待当天收盘净值到位后校正份额。</summary>
    Pending = 1,

    /// <summary>当天收盘净值已到位，份额已按 T 日净值确认。</summary>
    Confirmed = 2,

    /// <summary>用户撤销的待确认交易：预入账已全额反恢，仅留审计记录。</summary>
    Cancelled = 3,
}

/// <summary>
/// 交易流水（买入/卖出）。
/// 买入登记即预入账：累加累计本金与当月预算"已投"，份额先用最近可用净值预占；
/// 卖出登记即预减：按卖出金额占持仓的比例结转成本并扣减份额，不占用当月预算；
/// 两者都待 T 日收盘净值盘后到位后，按 T 日净值校正份额并转 Confirmed。
/// </summary>
public class Trade
{
    public long Id { get; set; }

    public string FundCode { get; set; } = string.Empty;

    public Fund? Fund { get; set; }

    public TradeSide Side { get; set; } = TradeSide.Buy;

    /// <summary>交易日（T 日）：份额按该日收盘净值确认。</summary>
    public DateOnly TradeDate { get; set; }

    /// <summary>交易金额（买入为实付本金，卖出为赎回金额）。</summary>
    public decimal Amount { get; set; }

    /// <summary>登记时按最近可用净值预占/预减的份额，用于 T 日净值到位后计算校正差。</summary>
    public decimal EstimatedShares { get; set; }

    /// <summary>
    /// 卖出结转的持有成本（买入为空）。登记时按预估份额比例扣减，确认后按实际确认份额更新；
    /// 撤销待确认卖出时用于精确反恢持仓成本。
    /// </summary>
    public decimal? CostAmount { get; set; }

    /// <summary>确认份额（T 日净值折算；Pending 时为空）。</summary>
    public decimal? ConfirmedShares { get; set; }

    /// <summary>确认净值（T 日收盘净值；Pending 时为空）。</summary>
    public decimal? ConfirmedNav { get; set; }

    public TradeStatus Status { get; set; } = TradeStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }
}