namespace FundDca.Core.Domain;

/// <summary>
/// 持仓版本（M3 导入对账）：每次确认导入（或系统初始化）落一版全量持仓镜像，
/// 作为"原子切换"后的落点与一键回滚的目标。当前看板数据仍以 holdings / manual_values 为准，
/// 版本表只负责留存与回滚；任意时刻仅有一个版本 IsCurrent=true。
/// </summary>
public class PositionVersion
{
    public long Id { get; set; }

    /// <summary>展示版本号，如 v20260910-2（v + 证明日期 + 当日序号）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>版本来源：baseline（系统首版）/ import（资产证明导入）/ rollback（回滚生成）。</summary>
    public string Source { get; set; } = "import";

    /// <summary>导入的原始文件名（演示/手工为空）。</summary>
    public string? FileName { get; set; }

    /// <summary>资产证明日期（T 日）。</summary>
    public DateOnly ProofDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>是否为当前看板所依据的版本（全表仅一行为 true）。</summary>
    public bool IsCurrent { get; set; }

    /// <summary>导入结果摘要（识别数、一致数、差异处理明细），用于结果页与版本列表。</summary>
    public string? Summary { get; set; }

    public List<PositionVersionItem> Items { get; set; } = new();
}

/// <summary>持仓版本中的单只基金镜像（全量留存，回滚时据此还原）。</summary>
public class PositionVersionItem
{
    public long Id { get; set; }

    public long VersionId { get; set; }

    public PositionVersion? Version { get; set; }

    public string FundCode { get; set; } = string.Empty;

    public string FundName { get; set; } = string.Empty;

    /// <summary>冗余档案类型，仅用于版本详情展示；回滚不改写档案归类。</summary>
    public FundType FundType { get; set; }

    public int? SectorId { get; set; }

    /// <summary>是否手工市值标的（货币基金/7日理财+）。</summary>
    public bool IsManual { get; set; }

    public decimal Shares { get; set; }

    public decimal? CostAmount { get; set; }

    /// <summary>版本时点市值（手工标的=手工市值；净值标的=份额×最新净值）。</summary>
    public decimal MarketValue { get; set; }

    /// <summary>
    /// 本行在该版本中的由来，用于导入结果日志：
    /// Baseline / Unchanged / DividendReinvest / ManualAdd / NewFund / ManualValue / KeptSystem。
    /// </summary>
    public string ChangeKind { get; set; } = "Unchanged";
}
