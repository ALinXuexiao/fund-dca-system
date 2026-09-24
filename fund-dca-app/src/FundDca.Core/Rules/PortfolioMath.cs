namespace FundDca.Core.Rules;

/// <summary>
/// 看板口径的纯计算函数（无副作用、无数据库依赖），便于单元测试锁口径。
/// </summary>
public static class PortfolioMath
{
    /// <summary>当前持仓周期累计收益率（%）：(市值 - 本金) / 本金 × 100；本金为 0 返回 0。</summary>
    public static decimal TotalReturnPercent(decimal marketValue, decimal cost) =>
        cost <= 0m ? 0m : Math.Round((marketValue - cost) / cost * 100m, 2);

    /// <summary>累计盈亏额。</summary>
    public static decimal TotalPnl(decimal marketValue, decimal cost) =>
        Math.Round(marketValue - cost, 2);

    /// <summary>
    /// 当日盈亏额：由当日市值与当日涨跌幅反推（pct 为百分数，如 0.82 表示 +0.82%）。
    /// 前收市值 = 今市值 / (1 + pct/100)。QDII 滞后行与货币基金不调用本方法。
    /// </summary>
    public static decimal DayPnl(decimal marketValue, decimal dayChangePercent)
    {
        var factor = 1m + dayChangePercent / 100m;
        if (factor <= 0m)
        {
            return 0m;
        }
        return Math.Round(marketValue - marketValue / factor, 2);
    }

    /// <summary>当日收益率（%），直接采用数据源涨跌幅（4 位小数）。</summary>
    public static decimal DayReturnPercent(decimal? dayChangePercent) =>
        dayChangePercent ?? 0m;
}
