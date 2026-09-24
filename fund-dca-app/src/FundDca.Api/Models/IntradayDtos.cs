namespace FundDca.Api.Models;

/// <summary>
/// 盘中估算（盘后看板的实时补充口径，纯内存计算、不落库）。
/// 由「跟踪指数实时涨跌幅 × 最新确认净值」推算，与实际净值存在跟踪误差，仅供参考。
/// </summary>
public record IntradayDto
{
    /// <summary>所取指数行情中最新的一条更新时间；无任何行情时为空</summary>
    public string? QuotedAt { get; init; }

    /// <summary>行情已超过 5 分钟未更新（休市/午休/数据源异常）</summary>
    public bool QuoteStale { get; init; }

    /// <summary>成功估算的基金数</summary>
    public int CoveredCount { get; init; }

    /// <summary>无法估算的基金数（无持仓/无净值/指数无行情）</summary>
    public int UnavailableCount { get; init; }

    public required List<IntradayRowDto> Rows { get; init; }
}

public record IntradayRowDto
{
    public required string FundCode { get; init; }

    /// <summary>实际取数的指数代码（走代理指数时与基金跟踪指数不同）</summary>
    public string? IndexCode { get; init; }
    public string? IndexName { get; init; }

    /// <summary>是否用档案配置的代理指数代为估算</summary>
    public bool ViaProxy { get; init; }

    /// <summary>指数盘中涨跌幅（%）</summary>
    public decimal? IndexChangePercent { get; init; }

    /// <summary>最新确认的单位净值及其日期</summary>
    public decimal? LastNav { get; init; }
    public string? LastNavDate { get; init; }

    /// <summary>估算盘中净值 = 最新净值 × (1 + 指数涨跌幅)</summary>
    public decimal? EstimatedNav { get; init; }

    /// <summary>估算市值 = 份额 × 估算净值</summary>
    public decimal? EstimatedMarketValue { get; init; }

    /// <summary>估算今日盈亏 = 份额 × (估算净值 - 最新净值)</summary>
    public decimal? EstimatedDayPnl { get; init; }

    /// <summary>不可估算原因；可估算时为空</summary>
    public string? Reason { get; init; }
}