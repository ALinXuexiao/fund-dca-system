namespace FundDca.Core.Rules;

/// <summary>
/// 盘中估算的判定与计算（纯函数，无数据库/网络依赖，便于单元测试锁口径）。
///
/// 估算口径：估算净值 = 最新确认净值 × (1 + 指数当日涨跌幅)。
/// 该口径的前提是「最新净值尚未覆盖行情所在的交易日」：当日净值一旦披露，
/// 当日涨跌已经计入净值，再乘一次就是重复计算——周末看到的「今日盈亏」翻倍、
/// 每个交易日盘后（净值入库后）到次日开盘前都会踩到。
/// </summary>
public static class IntradayEstimate
{
    /// <summary>市场时区（北京时间）：行情时间戳与净值日期都按它对齐到自然日。</summary>
    public static readonly TimeSpan MarketOffset = TimeSpan.FromHours(8);

    /// <summary>行情时间戳对应的交易日（北京时区自然日）。</summary>
    public static DateOnly QuoteTradeDate(DateTimeOffset quotedAt) =>
        DateOnly.FromDateTime(quotedAt.ToOffset(MarketOffset).DateTime);

    /// <summary>
    /// 是否周末（周六/周日，按北京时区自然日）。
    /// 只做周末判断、不含法定节假日日历——节假日不产生新行情，会被下面的
    /// <see cref="CoveredByNavReason"/> 守卫挡住重复计算，但页面文案仍按交易日展示。
    /// </summary>
    public static bool IsWeekend(DateTimeOffset now) =>
        now.ToOffset(MarketOffset).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>
    /// 净值是否已覆盖行情交易日：成立说明当日涨跌已计入净值，
    /// 该行不能再乘一次指数涨跌幅（否则重复计算），应改用基金自身净值涨跌幅算真实当日盈亏。
    /// </summary>
    public static bool NavCoversQuote(DateOnly navDate, DateOnly quoteDate) => navDate >= quoteDate;

    /// <summary>估算盘中净值。</summary>
    public static decimal EstimatedNav(decimal lastNav, decimal changePercent) =>
        Math.Round(lastNav * (1m + changePercent / 100m), 4);

    /// <summary>估算市值 = 份额 × 估算净值。</summary>
    public static decimal EstimatedMarketValue(decimal shares, decimal estimatedNav) =>
        Math.Round(shares * estimatedNav, 2);

    /// <summary>估算当日盈亏 = 份额 × (估算净值 - 最新确认净值)。</summary>
    public static decimal EstimatedDayPnl(decimal shares, decimal estimatedNav, decimal lastNav) =>
        Math.Round(shares * (estimatedNav - lastNav), 2);

    /// <summary>
    /// 已确认行的真实当日盈亏：按基金自身净值涨跌幅反推（口径与看板 dayPnl 一致）。
    /// </summary>
    public static decimal ConfirmedDayPnl(decimal shares, decimal unitNav, decimal navChangePercent) =>
        PortfolioMath.DayPnl(Math.Round(shares * unitNav, 2), navChangePercent);

    /// <summary>单行当日盈亏：已确认行优先取真实值，未确认行取指数估算值。</summary>
    public static decimal? DayPnlFor(decimal? confirmed, decimal? estimated) =>
        confirmed ?? estimated;

    /// <summary>
    /// 合计当日盈亏（统一口径）：已确认行按真实净值涨跌计入，未确认行按指数行情估算计入。
    /// 两者互斥，每行只取其一，避免重复计算、也避免随净值入库进度跳变。
    /// </summary>
    public static decimal TotalDayPnl(IEnumerable<(decimal? Confirmed, decimal? Estimated)> rows) =>
        rows.Sum(r => DayPnlFor(r.Confirmed, r.Estimated) ?? 0m);
}
