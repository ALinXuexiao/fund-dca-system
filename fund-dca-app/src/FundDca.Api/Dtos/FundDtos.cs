namespace FundDca.Api.Dtos;

/// <summary>基金档案列表项（M0 纵向切片：验证 数据库 → API → Vue 全链路）</summary>
public record FundListItem(
    string Code,
    string Name,
    string Type,
    string? Sector,
    bool SectorIsEquity,
    int SectorSortOrder,
    string? TrackedIndexCode,
    string? TrackedIndexName,
    string ValuationMetric,
    decimal? BuyFeeRate,
    string DividendMethod,
    bool IsActive);
