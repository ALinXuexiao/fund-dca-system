using FundDca.Core.Rules;

namespace FundDca.Core.Tests;

public class IntradayEstimateTests
{
    private static readonly TimeSpan Cn = TimeSpan.FromHours(8);

    // 2026-11-13 是周五，2026-11-14 是周六，2026-11-15 是周日，2026-11-16 是周一
    [Theory]
    [InlineData("2026-11-14", true)]  // 周六
    [InlineData("2026-11-15", true)]  // 周日
    [InlineData("2026-11-13", false)] // 周五
    [InlineData("2026-11-16", false)] // 周一
    public void IsWeekend_按北京时区自然日判断(string date, bool expected)
    {
        var now = new DateTimeOffset(DateTime.Parse($"{date} 10:00:00"), Cn);
        Assert.Equal(expected, IntradayEstimate.IsWeekend(now));
    }

    [Fact]
    public void IsWeekend_UTC时间落在北京周六凌晨时按周六处理()
    {
        // UTC 2026-11-13 17:30 = 北京 2026-11-14 01:30（周六）
        var now = new DateTimeOffset(2026, 11, 13, 17, 30, 0, TimeSpan.Zero);
        Assert.True(IntradayEstimate.IsWeekend(now));
    }

    [Fact]
    public void QuoteTradeDate_按北京时区取自然日()
    {
        // 东财 f124 是 Unix 秒；周五 16:29:53+08 的快照必须仍算作 2026-11-13
        var quotedAt = new DateTimeOffset(2026, 11, 13, 16, 29, 53, Cn);
        Assert.Equal(new DateOnly(2026, 11, 13), IntradayEstimate.QuoteTradeDate(quotedAt));

        // UTC 23:30（北京次日 07:30）应归到次日
        var utcLate = new DateTimeOffset(2026, 11, 13, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 14), IntradayEstimate.QuoteTradeDate(utcLate));
    }

    [Fact]
    public void NavCoversQuote_净值日期等于或晚于行情日期时视为已确认()
    {
        // 修复前周六的场景：净值 11-13 与行情 11-13 同日 → 涨跌已计入净值
        Assert.True(IntradayEstimate.NavCoversQuote(
            new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 13)));

        Assert.True(IntradayEstimate.NavCoversQuote(
            new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 13)));
    }

    [Fact]
    public void NavCoversQuote_净值日期落后于行情日期时视为未确认()
    {
        // 交易日盘中：lastNav 是 T-1，行情是 T → 正常估算
        Assert.False(IntradayEstimate.NavCoversQuote(
            new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 13)));
    }

    [Fact]
    public void 估算口径与修复前一致()
    {
        // 手工算例：净值 1.4739、涨跌幅 -1.06%、份额 33.7919
        const decimal lastNav = 1.4739m;
        const decimal pct = -1.06m;
        const decimal shares = 33.7919m;

        var estNav = IntradayEstimate.EstimatedNav(lastNav, pct);
        var estValue = IntradayEstimate.EstimatedMarketValue(shares, estNav);
        var dayPnl = IntradayEstimate.EstimatedDayPnl(shares, estNav, lastNav);

        // 与修复前 Math.Round(lastNav * (1 + pct/100), 4) 等公式逐位对齐
        Assert.Equal(1.4583m, estNav);
        Assert.Equal(Math.Round(shares * 1.4583m, 2), estValue);
        Assert.Equal(Math.Round(shares * (1.4583m - lastNav), 2), dayPnl);
    }

    [Fact]
    public void 已确认行的真实当日盈亏与看板dayPnl口径一致()
    {
        // 份额 × 净值 = 市值，再用基金自身涨跌幅反推：与 PortfolioMath.DayPnl 完全同一口径
        const decimal shares = 33.7919m;
        const decimal unitNav = 1.4739m;
        const decimal navPct = -1.06m;

        var marketValue = Math.Round(shares * unitNav, 2);
        var confirmed = IntradayEstimate.ConfirmedDayPnl(shares, unitNav, navPct);

        Assert.Equal(PortfolioMath.DayPnl(marketValue, navPct), confirmed);
    }

    [Fact]
    public void 混合态合计等于已确认真实加未确认估算()
    {
        // 6 只已确认（真实值）、5 只未确认（估算值）
        var rows = new (decimal? Confirmed, decimal? Estimated)[]
        {
            (-17.61m, null), (-5.30m, null), (7.94m, null), (-6.44m, null), (-5.54m, null), (-1.43m, null),
            (null, 7.05m), (null, -1.73m), (null, -2.90m), (null, 0.88m), (null, -0.53m),
        };

        var total = IntradayEstimate.TotalDayPnl(rows);

        // 已确认部分 -28.38 + 未确认部分 2.77
        Assert.Equal(-28.38m + 2.77m, total);
        // 与修复前（只加估算行）不同：旧的 2.77 会漏掉已确认部分并造成符号反转
        Assert.NotEqual(2.77m, total);
    }

    [Fact]
    public void 纯盘中合计等于全部估算之和()
    {
        var rows = new (decimal? Confirmed, decimal? Estimated)[]
        {
            (null, 7.05m), (null, -1.73m), (null, -2.90m), (null, 0.88m), (null, -0.53m),
        };

        Assert.Equal(2.77m, IntradayEstimate.TotalDayPnl(rows));
    }

    [Fact]
    public void 全确认合计等于全部真实值之和()
    {
        var rows = new (decimal? Confirmed, decimal? Estimated)[]
        {
            (-17.61m, null), (-5.30m, null), (7.94m, null), (-6.44m, null), (-5.54m, null), (-1.43m, null),
        };

        Assert.Equal(-28.38m, IntradayEstimate.TotalDayPnl(rows));
    }

    [Fact]
    public void 同一行不会同时计入真实与估算()
    {
        // 已确认优先：即使两个字段都有值，也只取真实值，避免重复计算
        var rows = new (decimal? Confirmed, decimal? Estimated)[]
        {
            (-5.00m, 99.00m),
        };

        Assert.Equal(-5.00m, IntradayEstimate.TotalDayPnl(rows));
        Assert.Equal(-5.00m, IntradayEstimate.DayPnlFor(-5.00m, 99.00m));
        Assert.Null(IntradayEstimate.DayPnlFor(null, null));
    }
}
