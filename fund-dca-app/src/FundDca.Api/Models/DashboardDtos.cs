namespace FundDca.Api.Models;

/// <summary>盘后看板聚合结果。</summary>
public record DashboardDto
{
    /// <summary>主净值日期（在管基金最新净值日期；QDII 可能早一天）</summary>
    public string? AsOfDate { get; init; }

    /// <summary>是否仍包含 SEED 兜底数据（未成功采集过真实净值）</summary>
    public bool ContainsSeedData { get; init; }

    public decimal Denominator { get; init; }
    public decimal EquityMarketValue { get; init; }
    public decimal StableMarketValue { get; init; }

    public decimal DayPnl { get; init; }
    public decimal EquityCost { get; init; }

    /// <summary>权益累计盈亏/收益率；资产证明不含成本时为空</summary>
    public decimal? EquityPnl { get; init; }
    public decimal? EquityPnlPercent { get; init; }

    public BudgetDto? Budget { get; init; }

    public required List<DashboardRowDto> Rows { get; init; }
    public required List<SectorSliceDto> Sectors { get; init; }
}

public record BudgetDto
{
    public required string YearMonth { get; init; }
    public decimal BudgetAmount { get; init; }
    public decimal InvestedAmount { get; init; }
    public decimal RemainingAmount { get; init; }
}

public record DashboardRowDto
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public int? SectorId { get; init; }
    public string? SectorName { get; init; }
    public bool SectorIsEquity { get; init; }
    public int SectorSortOrder { get; init; }

    public decimal? Shares { get; init; }
    public decimal? CostAmount { get; init; }
    public decimal? UnitNav { get; init; }
    public string? NavDate { get; init; }
    public decimal? DayChangePercent { get; init; }

    public decimal MarketValue { get; init; }
    public decimal? TotalReturnPercent { get; init; }
    public decimal WeightPercent { get; init; }

    /// <summary>净值日期早于主日期（QDII 滞后）</summary>
    public bool Stale { get; init; }
    public bool IsMoney { get; init; }
    public string? Source { get; init; }

    /// <summary>手工市值日期（货币基金，用于"超过 7 天未更新"提示）</summary>
    public string? ManualValueDate { get; init; }
}

public record SectorSliceDto
{
    public required string Name { get; init; }
    public decimal MarketValue { get; init; }
    public decimal WeightPercent { get; init; }
    public bool IsEquity { get; init; }
    public int SortOrder { get; init; }
}

public record RefreshReportDto
{
    public required string FinishedAt { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public required List<FundRefreshResultDto> Results { get; init; }
}

public record FundRefreshResultDto
{
    public required string FundCode { get; init; }
    public bool Success { get; init; }
    public string? TradeDate { get; init; }
    public bool NewRow { get; init; }
    public string? Error { get; init; }
}

public record ManualValueUpdateDto
{
    public decimal MarketValue { get; init; }
    public string? ValueDate { get; init; }
}
