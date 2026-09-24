namespace FundDca.Core.Domain;

/// <summary>
/// 指数估值日快照（B1 红绿灯依据）。每个指数每个交易日一行，upsert 幂等。
/// 百分位统一存 0-100 百分数；数据源若给 0-1 小数，由采集层 ×100。
/// </summary>
public class IndexValuation
{
    /// <summary>本系统跟踪指数代码（代理取值时也记在本指数名下）</summary>
    public string IndexCode { get; set; } = string.Empty;

    public IndexInfo? Index { get; set; }

    public DateOnly TradeDate { get; set; }

    /// <summary>市盈率 TTM；指数整体亏损时为空（如地产）</summary>
    public decimal? PeTtm { get; set; }

    /// <summary>PE 历史百分位（0-100）</summary>
    public decimal? PePercentile { get; set; }

    /// <summary>市净率</summary>
    public decimal? Pb { get; set; }

    /// <summary>PB 历史百分位（0-100）</summary>
    public decimal? PbPercentile { get; set; }

    /// <summary>实际取数的外部指数代码（与 IndexCode 不同即代理估值），如 SH000300</summary>
    public string? ResolvedCode { get; set; }

    /// <summary>数据源：DANJUAN（蛋卷）/ CSI（中证官网自有PE）/ MANUAL</summary>
    public string Source { get; set; } = "DANJUAN";

    public DateTimeOffset FetchedAt { get; set; }
}
