namespace FundDca.Core.Domain;

/// <summary>
/// 赛道（行业/风格/宽基/稳健资产分类），B4 赛道占比红线的口径单元。
/// </summary>
public class Sector
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>赛道占比红线（%），默认 15；等号不通过</summary>
    public decimal LimitPercent { get; set; } = 15m;

    /// <summary>是否为权益赛道（false = 债券/货币等稳健资产，不参与 B4）</summary>
    public bool IsEquity { get; set; } = true;

    /// <summary>看板分组显示顺序（权益在前，债券/货币垫底）</summary>
    public int SortOrder { get; set; }
}
