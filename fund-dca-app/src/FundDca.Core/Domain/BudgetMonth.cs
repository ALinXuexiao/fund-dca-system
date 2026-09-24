namespace FundDca.Core.Domain;

/// <summary>
/// 月度预算资金池：当月定投预算与已投金额。
/// 资金留在银行卡、不形成持仓、不计入分母 D；
/// 月底剩余额度（Budget - Invested）一次性买入债券基金。
/// </summary>
public class BudgetMonth
{
    /// <summary>年月，主键，格式 "2026-09"</summary>
    public string YearMonth { get; set; } = string.Empty;

    /// <summary>当月定投预算</summary>
    public decimal BudgetAmount { get; set; }

    /// <summary>当月已投股票/混合/QDII 金额（成交回填后累加）</summary>
    public decimal InvestedAmount { get; set; }

    /// <summary>月底是否已把剩余额度转入债券基金</summary>
    public bool BondSweepDone { get; set; }
}
