using Xunit;
using FundDca.Core.Rules;

namespace FundDca.Core.Tests;

/// <summary>
/// 买入流水纯算术：登记即预入账的预计额份额、T 日净值到位后的份额校正。
/// 覆盖用户确认的规则：T 日收盘净值到位后再计算才是正确的。
/// </summary>
public class TradeMathTests
{
    [Fact]
    public void 金额50_按最近净值1元_预计额50份()
    {
        Assert.Equal(50.0000m, TradeMath.EstimateShares(50m, 1m));
    }

    [Fact]
    public void 金额50_按净值1点37_份额保留4位小数()
    {
        // 36.4963...
        Assert.Equal(36.4964m, TradeMath.EstimateShares(50m, 1.37m));
    }

    [Fact]
    public void qdii_金额100_按净值2点5_确认40份()
    {
        Assert.Equal(40.0000m, TradeMath.ConfirmShares(100m, 2.5m));
    }

    [Fact]
    public void 净值上涨_确认份额少于预计额_校正为负()
    {
        // 预计时净值 1.0 → 预占 50 份；T 日收盘涨到 1.1 → 真实 45.4545 份
        var est = TradeMath.EstimateShares(50m, 1.0m);
        var delta = TradeMath.ShareCorrection(50m, 1.1m, est);
        Assert.Equal(50.0000m, est);                               // 预入账持仓 50
        Assert.Equal(45.4545m, TradeMath.ConfirmShares(50m, 1.1m));
        Assert.Equal(-4.5455m, delta);                             // 回撤 4.5455 份
        Assert.Equal(45.4545m, TradeMath.AddShares(50m, delta));   // 校正后持仓 = 真实份额
    }

    [Fact]
    public void 净值下跌_确认份额多于预计额_校正为正()
    {
        var est = TradeMath.EstimateShares(50m, 1.2m);  // 41.6667
        var delta = TradeMath.ShareCorrection(50m, 1.0m, est); // 50 - 41.6667
        Assert.Equal(41.6667m, est);
        Assert.Equal(8.3333m, delta);
        Assert.Equal(50.0000m, TradeMath.AddShares(est, delta));
    }

    [Fact]
    public void 持仓累加与本金累加_按4位与2位小数()
    {
        Assert.Equal(55.0000m, TradeMath.AddShares(50m, 5m));
        Assert.Equal(150.0000m, TradeMath.AddCost(100m, 50m));
    }
}