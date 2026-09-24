namespace FundDca.Core.Domain;

/// <summary>
/// 每日持仓快照：每次盘后采集后落盘，份额 × 当日单位净值 = 市值。
/// 版本化留存，支持历史回看与导入回滚（M3 扩展版本号）。
/// </summary>
public class DailySnapshot
{
    public string FundCode { get; set; } = string.Empty;

    public Fund? Fund { get; set; }

    public DateOnly TradeDate { get; set; }

    public decimal Shares { get; set; }

    public decimal UnitNav { get; set; }

    public decimal MarketValue { get; set; }

    public decimal? DayChangePercent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
