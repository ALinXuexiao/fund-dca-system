namespace FundDca.Api.Models;

/// <summary>今日定投决策结果。</summary>
public record DecisionDto
{
    public string? ValuationDate { get; init; }
    public decimal Denominator { get; init; }
    public BudgetDto? Budget { get; init; }
    public decimal TotalSuggested { get; init; }
    public decimal RemainingAfter { get; init; }

    /// <summary>引擎参数（页面展示口径）</summary>
    public required DecisionOptionsDto Options { get; init; }

    public required List<FundDecisionDto> Funds { get; init; }
}

public record DecisionOptionsDto
{
    /// <summary>单次定投固定金额（元/只/次）</summary>
    public decimal FixedInvestAmount { get; init; }
    public decimal SingleFundLimitPercent { get; init; }
    public decimal SectorLimitPercent { get; init; }
}

public record FundDecisionDto
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? SectorName { get; init; }
    public decimal MarketValue { get; init; }
    public decimal WeightPercent { get; init; }
    public decimal SectorWeightPercent { get; init; }

    /// <summary>Green/Yellow/Red/NoData/Stable</summary>
    public required string Signal { get; init; }

    public string? IndexName { get; init; }
    public string? IndexCode { get; init; }

    /// <summary>实际采用的估值指标 PE/PB（无口径为空）</summary>
    public string? Metric { get; init; }
    public decimal? MetricValue { get; init; }
    public decimal? Percentile { get; init; }
    public string? ValuationDate { get; init; }

    /// <summary>持有总收益率 %（成本未知为空）</summary>
    public decimal? TotalReturnPercent { get; init; }

    /// <summary>今日建议金额：固定额或 0（暂停）</summary>
    public decimal SuggestedAmount { get; init; }

    public required List<string> Blockers { get; init; }
    public required List<string> Notes { get; init; }
}

public record BudgetUpdateDto
{
    public decimal BudgetAmount { get; init; }
    public decimal InvestedAmount { get; init; }
}

public record SettingUpdateDto
{
    public decimal FixedInvestAmount { get; init; }
}

public record ValuationRefreshResultDto
{
    public required string IndexCode { get; init; }
    public required string IndexName { get; init; }
    public bool Success { get; init; }
    public string? TradeDate { get; init; }
    public bool ViaProxy { get; init; }
    public string? ResolvedCode { get; init; }
    public decimal? PePercentile { get; init; }
    public decimal? PbPercentile { get; init; }
    public string? Error { get; init; }
}

public record ValuationRefreshReportDto
{
    public required string FinishedAt { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public required List<ValuationRefreshResultDto> Results { get; init; }
}
