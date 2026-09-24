namespace FundDca.Core.Domain;

/// <summary>交易流水方向（M3 先实现买入；卖出在 M4 卖出闭环时扩展）。</summary>
public enum TradeSide
{
    Buy = 1,
}

/// <summary>交易入账状态。</summary>
public enum TradeStatus
{
    /// <summary>已登记、已预入账（预算/本金先占），待当天收盘净值到位后校正份额。</summary>
    Pending = 1,

    /// <summary>当天收盘净值已到位，份额已按 T 日净值确认。</summary>
    Confirmed = 2,

    /// <summary>用户撤销的待确认买入：预入账已全额反恢，仅留审计记录。</summary>
    Cancelled = 3,
}

/// <summary>
/// 交易流水（买入记录）。
/// 登记即预入账：立即累加累计本金与当月预算"已投"，份额先用最近可用净值预占；
/// 待 T 日（买入日）收盘净值盘后到位后，按 T 日净值校正份额并转 Confirmed。
/// </summary>
public class Trade
{
    public long Id { get; set; }

    public string FundCode { get; set; } = string.Empty;

    public Fund? Fund { get; set; }

    public TradeSide Side { get; set; } = TradeSide.Buy;

    /// <summary>买入日（T 日）：份额按该日收盘净值确认。</summary>
    public DateOnly TradeDate { get; set; }

    /// <summary>买入金额（成交本金）。</summary>
    public decimal Amount { get; set; }

    /// <summary>登记时按最近可用净值预占的份额，用于 T 日净值到位后计算校正差。</summary>
    public decimal EstimatedShares { get; set; }

    /// <summary>确认份额（T 日净值折算；Pending 时为空）。</summary>
    public decimal? ConfirmedShares { get; set; }

    /// <summary>确认净值（T 日收盘净值；Pending 时为空）。</summary>
    public decimal? ConfirmedNav { get; set; }

    public TradeStatus Status { get; set; } = TradeStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }
}