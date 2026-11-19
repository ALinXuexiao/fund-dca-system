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

/// <summary>新增基金档案入参（手工建档；类型/赛道规则与导入建档一致）。</summary>
public record CreateFundRequest(
    string Code,
    string Name,
    string Type,
    int? SectorId,
    string? TrackedIndexCode,
    decimal? BuyFeeRate,
    string? DividendMethod,
    bool? UseManualValue);

public record SectorOption(int Id, string Name, bool IsEquity, int SortOrder);

public record IndexOption(string Code, string Name, string Metric);

/// <summary>新增基金弹窗的下拉数据。</summary>
public record FundOptions(
    IReadOnlyList<SectorOption> Sectors,
    IReadOnlyList<IndexOption> Indexes);
