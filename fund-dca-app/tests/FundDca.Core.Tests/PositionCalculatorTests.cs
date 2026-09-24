using FundDca.Core.Domain;
using FundDca.Core.Rules;
using Xunit;

namespace FundDca.Core.Tests;

/// <summary>
/// 用 PRD 看板演示数据（2026-09-10）锁死分母与占比口径：
/// 权益 31,008.40 + 债券 5,000 + 货币 6,000 = D 42,008.40。
/// </summary>
public class PositionCalculatorTests
{
    private static List<HoldingValue> DemoHoldings() =>
    [
        new("110020", "沪深300ETF联接A", FundType.Stock, 5478.40m, "大盘"),
        new("160119", "中证500ETF联接A", FundType.Stock, 3520.00m, "中盘"),
        new("110026", "创业板ETF联接A", FundType.Stock, 3450.00m, "成长"),
        new("100032", "中证红利指数增强A", FundType.Stock, 5600.00m, "价值"),
        new("007466", "红利低波ETF联接A", FundType.Stock, 2140.00m, "红利"),
        new("000248", "主要消费ETF联接", FundType.Stock, 6400.00m, "消费"),
        new("161725", "中证白酒指数A", FundType.Stock, 2100.00m, "消费"),
        new("013402", "恒生科技ETF联接A(QDII)", FundType.Qdii, 2320.00m, "港股科技"),
        new("110007", "易方达稳健收益债券A", FundType.Bond, 5000.00m, "债券"),
        new("000198", "天弘余额宝货币", FundType.Money, 6000.00m, "货币"),
    ];

    [Fact]
    public void Denominator_包含债券与货币_不包含银行卡预算()
    {
        var d = PositionCalculator.Denominator(DemoHoldings());
        Assert.Equal(42008.40m, d);
    }

    [Fact]
    public void Evaluate_权益与稳健市值拆分正确()
    {
        var r = PositionCalculator.Evaluate(DemoHoldings());
        Assert.Equal(42008.40m, r.Denominator);
        Assert.Equal(31008.40m, r.EquityMarketValue);
        Assert.Equal(11000.00m, r.StableMarketValue);
    }

    [Fact]
    public void 单只占比_与看板展示一致()
    {
        var r = PositionCalculator.Evaluate(DemoHoldings());
        var w = r.FundWeights.ToDictionary(x => x.FundCode);

        Assert.Equal(13.0412m, w["110020"].WeightPercent); // 沪深300 13.0%
        Assert.Equal(8.3793m, w["160119"].WeightPercent);  // 中证500 8.4%
        Assert.Equal(15.2350m, w["000248"].WeightPercent); // 主要消费 15.2%
        Assert.Equal(4.9990m, w["161725"].WeightPercent);  // 白酒 5.0%
        Assert.Equal(11.9024m, w["110007"].WeightPercent); // 债券 11.9%
    }

    [Fact]
    public void 赛道占比_大盘13点04_消费20点23()
    {
        var r = PositionCalculator.Evaluate(DemoHoldings());
        var s = r.SectorWeights.ToDictionary(x => x.SectorName);

        Assert.Equal(5478.40m, s["大盘"].MarketValue);
        Assert.Equal(13.0412m, s["大盘"].WeightPercent);
        Assert.Equal(8500.00m, s["消费"].MarketValue);
        Assert.Equal(20.2340m, s["消费"].WeightPercent);
        Assert.Equal(5.5227m, s["港股科技"].WeightPercent);
        Assert.False(s["债券"].IsEquity);
        Assert.False(s["货币"].IsEquity);
    }

    [Fact]
    public void B3_等号不通过_严格小于10()
    {
        Assert.False(PositionCalculator.PassesSingleFundLimit(10m));
        Assert.True(PositionCalculator.PassesSingleFundLimit(9.9999m));
        Assert.False(PositionCalculator.PassesSingleFundLimit(10.0001m));
        // 看板：沪深300 13.04%、主要消费 15.24% 不通过；中证500 8.38%、白酒 4.999% 通过
        Assert.False(PositionCalculator.PassesSingleFundLimit(13.0412m));
        Assert.False(PositionCalculator.PassesSingleFundLimit(15.2350m));
        Assert.True(PositionCalculator.PassesSingleFundLimit(8.3793m));
        Assert.True(PositionCalculator.PassesSingleFundLimit(4.9990m));
    }

    [Fact]
    public void B4_等号不通过_严格小于15()
    {
        Assert.False(PositionCalculator.PassesSectorLimit(15m));
        Assert.True(PositionCalculator.PassesSectorLimit(14.9999m));
        // 新赛道口径：消费 20.23% 不通过；大盘 13.04%、港股科技 5.52% 通过
        Assert.False(PositionCalculator.PassesSectorLimit(20.2340m));
        Assert.True(PositionCalculator.PassesSectorLimit(13.0412m));
        Assert.True(PositionCalculator.PassesSectorLimit(5.5227m));
    }

    [Fact]
    public void 空持仓_分母为零不抛异常()
    {
        var r = PositionCalculator.Evaluate([]);
        Assert.Equal(0m, r.Denominator);
        Assert.Empty(r.FundWeights);
        Assert.Equal(0m, PositionCalculator.WeightOf(100m, 0m));
    }
}

public class PortfolioMathTests
{
    [Fact]
    public void 累计收益率_看板演示口径()
    {
        // 沪深300：5478.40 / 6000 本金 ≈ -8.69%；白酒 2100 / 3000 = -30%
        Assert.Equal(-8.69m, PortfolioMath.TotalReturnPercent(5478.40m, 6000m));
        Assert.Equal(-30.00m, PortfolioMath.TotalReturnPercent(2100m, 3000m));
        Assert.Equal(15.00m, PortfolioMath.TotalReturnPercent(3450m, 3000m));
        Assert.Equal(0m, PortfolioMath.TotalReturnPercent(1000m, 0m));
    }

    [Fact]
    public void 当日盈亏_由涨跌幅反推()
    {
        // 沪深300 当日 +0.82%，今市值 5478.40 → 前收 ≈ 5433.84，盈亏 ≈ +44.56
        Assert.Equal(44.56m, PortfolioMath.DayPnl(5478.40m, 0.82m));
        // 主要消费 -1.34%，今市值 6400 → 盈亏 ≈ -86.92
        Assert.Equal(-86.92m, PortfolioMath.DayPnl(6400m, -1.34m));
    }

    [Fact]
    public void 累计盈亏额_保留两位小数()
    {
        Assert.Equal(-521.60m, PortfolioMath.TotalPnl(5478.40m, 6000m));
    }
}
