namespace FundDca.Core.Domain;

/// <summary>
/// 当前持仓（对应"持仓周期"）：份额与当前周期累计本金。
/// 清仓后周期关闭、记录归档；再建仓时新建持仓记录，本金与持有收益率从 0 起算。
/// M1 先承载当前周期；周期历史表在 M4 卖出闭环时补全。
/// </summary>
public class Holding
{
    public string FundCode { get; set; } = string.Empty;

    public Fund? Fund { get; set; }

    /// <summary>当前持有份额</summary>
    public decimal Shares { get; set; }

    /// <summary>本持仓周期累计投入本金；资产证明不含成本时为空，收益指标显示“—”</summary>
    public decimal? CostAmount { get; set; }

    /// <summary>当前周期建仓日期</summary>
    public DateOnly OpenedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
