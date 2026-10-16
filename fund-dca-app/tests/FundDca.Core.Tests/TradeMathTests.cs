using Xunit;
using FundDca.Core.Rules;

namespace FundDca.Core.Tests;

/// <summary>
/// 买入流水纯算术：申购费外扣（净申购金额 = 金额 / (1 + 费率)）、
/// 登记即预入账的预计额份额、T 日净值到位后的份额校正。
/// 覆盖用户确认的规则：T 日收盘净值到位后再计算才是正确的。
/// 2026-10-16 补充：份额必须按净申购金额折算 —— 此前直接按"金额 / 净值"折算，
/// 漏扣申购费导致买入后市值虚高（等于本金）。
/// </summary>
public class TradeMathTests
{
    [Fact]
    public void 无申购费_金额50_按最近净值1元_预计额50份()
    {
        Assert.Equal(50.0000m, TradeMath.EstimateShares(50m, 1m, 0m));
    }

    [Fact]
    public void 无申购费_金额50_按净值1点37_份额保留4位小数()
    {
        // 36.4963...
        Assert.Equal(36.4964m, TradeMath.EstimateShares(50m, 1.37m, 0m));
    }

    [Fact]
    public void 申购费0点12_金额50_净申购金额49点94()
    {
        // 50 / (1 + 0.0012) = 49.94007... → 四舍五入到分
        Assert.Equal(49.94m, TradeMath.NetBuyAmount(50m, 0.0012m));
    }

    [Fact]
    public void 申购费0点12_A500首笔_按净值1点3231_预占37点7447份()
    {
        // 净额 49.94 / 1.3231 = 37.74469...；漏扣旧口径会得 37.7900
        Assert.Equal(37.7447m, TradeMath.EstimateShares(50m, 1.3231m, 0.0012m));
    }

    [Fact]
    public void 申购费0点12_A500首笔_T日净值1点2971_确认38点5013份()
    {
        // 净额 49.94 / 1.2971 = 38.50127...；漏扣旧口径会得 38.5475
        Assert.Equal(38.5013m, TradeMath.ConfirmShares(50m, 1.2971m, 0.0012m));
    }

    [Fact]
    public void 申购费外扣_买入后市值恒小于本金_差额即申购费()
    {
        const decimal amount = 50m;
        const decimal feeRate = 0.0012m;
        const decimal nav = 1.2971m;

        var shares = TradeMath.ConfirmShares(amount, nav, feeRate);
        var marketValue = Math.Round(shares * nav, 2);

        Assert.True(marketValue < amount, $"市值 {marketValue} 应小于本金 {amount}（申购费已内扣）");
        Assert.Equal(49.94m, marketValue);                                        // 差额 0.06 = 申购费
        Assert.Equal(-0.12m, Math.Round((marketValue / amount - 1m) * 100m, 2));  // 初始收益率 = -费率
    }

    [Fact]
    public void 费率为负_按0处理_不放大份额()
    {
        Assert.Equal(50m, TradeMath.NetBuyAmount(50m, -0.01m));
        Assert.Equal(50.0000m, TradeMath.EstimateShares(50m, 1m, -0.01m));
    }

    [Fact]
    public void 无申购费_qdii_金额100_按净值2点5_确认40份()
    {
        Assert.Equal(40.0000m, TradeMath.ConfirmShares(100m, 2.5m, 0m));
    }

    [Fact]
    public void 净值上涨_确认份额少于预计额_校正为负()
    {
        // 预计时净值 1.0 → 预占 50 份；T 日收盘涨到 1.1 → 真实 45.4545 份
        var est = TradeMath.EstimateShares(50m, 1.0m, 0m);
        var delta = TradeMath.ShareCorrection(50m, 1.1m, est, 0m);
        Assert.Equal(50.0000m, est);                               // 预入账持仓 50
        Assert.Equal(45.4545m, TradeMath.ConfirmShares(50m, 1.1m, 0m));
        Assert.Equal(-4.5455m, delta);                             // 回撤 4.5455 份
        Assert.Equal(45.4545m, TradeMath.AddShares(50m, delta));   // 校正后持仓 = 真实份额
    }

    [Fact]
    public void 净值下跌_确认份额多于预计额_校正为正()
    {
        var est = TradeMath.EstimateShares(50m, 1.2m, 0m);         // 41.6667
        var delta = TradeMath.ShareCorrection(50m, 1.0m, est, 0m); // 50 - 41.6667
        Assert.Equal(41.6667m, est);
        Assert.Equal(8.3333m, delta);
        Assert.Equal(50.0000m, TradeMath.AddShares(est, delta));
    }

    [Fact]
    public void 含申购费_净值变动_校正后持仓仍收敛到确认份额()
    {
        var est = TradeMath.EstimateShares(50m, 1.3m, 0.0012m);     // 38.4154
        var delta = TradeMath.ShareCorrection(50m, 1.2m, est, 0.0012m);
        var confirmed = TradeMath.ConfirmShares(50m, 1.2m, 0.0012m); // 41.6167
        Assert.Equal(41.6167m, confirmed);
        Assert.Equal(confirmed, TradeMath.AddShares(est, delta));   // 与费率无关地收敛
    }

    [Fact]
    public void 持仓累加与本金累加_按4位与2位小数()
    {
        Assert.Equal(55.0000m, TradeMath.AddShares(50m, 5m));
        Assert.Equal(150.0000m, TradeMath.AddCost(100m, 50m));
    }
}
