using FundDca.Core.Domain;
using FundDca.Core.Rules;

namespace FundDca.Api.Models;

/// <summary>赛道选项（新基金归类下拉）。</summary>
public record SectorOptionDto(int Id, string Name, bool IsEquity);

/// <summary>对账项（前端差异表格一行）。</summary>
public record ReconcileItemDto(
    string? Code,
    string FileFundName,
    decimal? FileShares,
    decimal? FileMarketValue,
    bool ExistsInSystem,
    string? SystemName,
    FundType? SystemType,
    int? SectorId,
    string? SectorName,
    bool IsManual,
    decimal? SystemShares,
    decimal? SystemCostAmount,
    decimal? SystemMarketValue,
    decimal? UnitNav,
    ReconcileKind Kind,
    ReconcileAction Action,
    decimal SharesDelta,
    decimal? SuggestedAddedCost,
    FundType? ProposedType,
    IReadOnlyList<ReconcileAction> AllowedActions,
    string Message);

public record ImportSummaryDto(
    int Total,
    int Matched,
    int Differences,
    int NewFunds,
    int ManualValueChanges);

/// <summary>解析 + 对账预览（第一步/第二步数据，不落库；ParsedStatement 暂存服务端缓存）。</summary>
public record ImportPreviewDto(
    string Token,
    string Source,
    string? FileName,
    string TemplateVersion,
    string? ProofDate,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ReconcileItemDto> Items,
    IReadOnlyList<SectorOptionDto> Sectors,
    ImportSummaryDto Summary);

/// <summary>用户对单条对账项的处理选择。</summary>
public class CommitDecisionDto
{
    public string? Code { get; set; }
    public string Action { get; set; } = nameof(ReconcileAction.None);
    public int? SectorId { get; set; }
    public string? FundType { get; set; }
    public decimal? AddedCost { get; set; }
}

/// <summary>确认导入请求。</summary>
public class CommitRequestDto
{
    public string Token { get; set; } = string.Empty;
    /// <summary>可覆盖证明日期（yyyy-MM-dd）。</summary>
    public string? ProofDate { get; set; }
    public List<CommitDecisionDto> Decisions { get; set; } = [];
}

/// <summary>导入/回滚结果（第三步）。</summary>
public record CommitResultDto(
    string Version,
    string Source,
    string ProofDate,
    int Applied,
    IReadOnlyList<string> Logs,
    ImportSummaryDto Summary);

/// <summary>持仓版本列表项。</summary>
public record VersionListItemDto(
    long Id,
    string Version,
    string Source,
    string? FileName,
    string ProofDate,
    string CreatedAt,
    bool IsCurrent,
    string? Summary,
    int ItemCount);
