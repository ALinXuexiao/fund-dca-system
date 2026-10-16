namespace FundDca.Core.Domain;

/// <summary>
/// 基金单位净值历史（盘后正式净值）。
/// 权益/债券基金由采集任务落盘，越积越厚；货币基金不采集。
/// 不同基金净值披露时点不一：投美股的 QDII 常 T+1/T+2 才公布，投港股或境内的当晚即可，
/// 故 TradeDate 可能早于看板主日期，看板逐行标注日期。
/// </summary>
public class FundNav
{
    public string FundCode { get; set; } = string.Empty;

    public Fund? Fund { get; set; }

    /// <summary>净值所属交易日</summary>
    public DateOnly TradeDate { get; set; }

    /// <summary>单位净值</summary>
    public decimal UnitNav { get; set; }

    /// <summary>累计净值（含历史分红；红利再投资份额另由持仓份额体现）</summary>
    public decimal? AccNav { get; set; }

    /// <summary>当日涨跌幅（%）</summary>
    public decimal? DayChangePercent { get; set; }

    /// <summary>数据来源：EASTMONEY / SEED（离线兜底）</summary>
    public string Source { get; set; } = "EASTMONEY";

    public DateTimeOffset FetchedAt { get; set; }
}
