namespace FundDca.Core.Domain;

/// <summary>
/// 标的去重组：同一组内只有"主投标的"能进入买入判定（G0 闸门）。
/// 内置七组：红利、消费、大盘、中盘、小盘、成长风格、价值风格，用户可自行增删。
/// </summary>
public class OverlapGroup
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>是否系统内置（内置组可改名/删成员，删除时给出二次确认）</summary>
    public bool IsBuiltIn { get; set; }

    public List<FundOverlap> Members { get; set; } = new();
}

/// <summary>基金与去重组的多对多关联；IsPrimary = 组内主投标的</summary>
public class FundOverlap
{
    public int OverlapGroupId { get; set; }

    public OverlapGroup? Group { get; set; }

    public string FundCode { get; set; } = string.Empty;

    public Fund? Fund { get; set; }

    public bool IsPrimary { get; set; }
}
