namespace FundDca.Core.Domain;

/// <summary>
/// 基金档案：静态基础信息（份额/成本/净值等动态数据在 M1 由快照与持仓周期承载）。
/// </summary>
public class Fund
{
    /// <summary>基金代码（6 位数字/QDII 代码），主键</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public FundType Type { get; set; }

    /// <summary>跟踪指数代码（主动基金可为空；为空则不参与 B1 自动判定）</summary>
    public string? TrackedIndexCode { get; set; }

    public IndexInfo? TrackedIndex { get; set; }

    public int? SectorId { get; set; }

    public Sector? Sector { get; set; }

    /// <summary>是否启用（清仓后可停用档案；历史周期仍留档）</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>申购费率（小数，0.0015 = 0.15%）；资产证明不含此项，待用户提供</summary>
    public decimal? BuyFeeRate { get; set; }

    /// <summary>按持有期分档的赎回费率（jsonb 持久化）；待用户提供</summary>
    public List<RedeemFeeTier> RedeemFeeTiers { get; set; } = new();

    /// <summary>分红方式：REINVEST = 红利再投资（系统默认），CASH = 现金分红</summary>
    public string DividendMethod { get; set; } = "REINVEST";

    /// <summary>
    /// 市值手工维护（货币基金、多产品合并的虚拟标的如"7日理财+"）。
    /// 为 true 时不采集外部净值，市值取 manual_values。
    /// </summary>
    public bool UseManualValue { get; set; }
}
