using FundDca.Core.Domain;
using FundDca.Core.Rules;

namespace FundDca.Core.Tests;

/// <summary>
/// 实盘定投规则：四条件同时满足（绿灯低估 + 总收益为负 + 单只&lt;10% + 赛道&lt;15%）
/// 才投固定额 50 元，任一不满足即不操作；预算不足不投。
/// </summary>
public class DcaDecisionEngineTests
{
    private const decimal D = 10000m;

    /// <summary>构造一只权益基金输入；默认绿灯 10% 分位、亏损 -5%、占比远离红线。</summary>
    private static DecisionInput Eq(
        string code,
        decimal marketValue = 100m,
        decimal cost = 200m,
        decimal? percentile = 10m,
        decimal sectorMv = 100m,
        FundType type = FundType.Stock,
        string? coveredBy = null) =>
        new(code, code, type, "测试赛道", marketValue, cost, sectorMv,
            percentile, 30m, 70m, coveredBy);

    private static DecisionInput Stable(string code = "BOND", decimal mv = 100m) =>
        new(code, code, FundType.Bond, "债券", mv, mv, mv, null, 30m, 70m, null);

    [Fact]
    public void AllFourConditionsMet_SuggestsFixedAmount()
    {
        var decisions = DcaDecisionEngine.Build(new[] { Eq("BUY") }, D, 2000m);
        var d = decisions.Single();
        Assert.Equal(SignalLevel.Green, d.Signal);
        Assert.Equal(50m, d.SuggestedAmount);
        Assert.Empty(d.Blockers);
        Assert.Contains(d.Notes, n => n.Contains("四条件全部满足"));
    }

    [Fact]
    public void FixedAmount_IsConfigurable()
    {
        var decisions = DcaDecisionEngine.Build(new[] { Eq("BUY") }, D, 2000m,
            new DecisionOptions(100m));
        Assert.Equal(100m, decisions.Single().SuggestedAmount);
    }

    [Fact]
    public void Yellow_NotUndervalued_BlocksEvenIfLosing()
    {
        // 百分位 50% → 黄灯；收益 -50%、占比 1%，条件①不满足即不买
        var d = DcaDecisionEngine.Build(new[] { Eq("Y", percentile: 50m) }, D, 2000m).Single();
        Assert.Equal(0m, d.SuggestedAmount);
        Assert.Contains(d.Blockers, b => b.Contains("条件①"));
    }

    [Fact]
    public void Red_Blocks()
    {
        var d = DcaDecisionEngine.Build(new[] { Eq("R", percentile: 80m) }, D, 2000m).Single();
        Assert.Equal(SignalLevel.Red, d.Signal);
        Assert.Equal(0m, d.SuggestedAmount);
        Assert.Contains(d.Blockers, b => b.Contains("高估线"));
    }

    [Fact]
    public void NoValuation_BlocksCondition1()
    {
        var d = DcaDecisionEngine.Build(new[] { Eq("NODATA", percentile: null) }, D, 2000m).Single();
        Assert.Equal(SignalLevel.NoData, d.Signal);
        Assert.Contains(d.Blockers, b => b.Contains("条件①") && b.Contains("暂无指数估值"));
        Assert.Equal(0m, d.SuggestedAmount);
    }

    [Fact]
    public void PositiveReturn_BlocksCondition2_EvenIfGreen()
    {
        // 绿灯但市值 > 本金（+100%），不补仓
        var d = DcaDecisionEngine.Build(new[] { Eq("PROFIT", marketValue: 200m, cost: 100m) }, D, 2000m).Single();
        Assert.Equal(0m, d.SuggestedAmount);
        Assert.Contains(d.Blockers, b => b.Contains("条件②"));
        Assert.Contains(d.Blockers, b => b.Contains("为正"));
    }

    [Fact]
    public void MissingCost_BlocksCondition2()
    {
        var input = Eq("NOCOST") with { CostAmount = null };
        var d = DcaDecisionEngine.Build(new[] { input }, D, 2000m).Single();
        Assert.Equal(0m, d.SuggestedAmount);
        Assert.Contains(d.Blockers, b => b.Contains("条件②") && b.Contains("本金"));
    }

    [Fact]
    public void FundWeightAt10Percent_BlocksCondition3()
    {
        // D=10000，市值 1000 → 占比正好 10%，严格小于不通过
        var d = DcaDecisionEngine.Build(new[] { Eq("FAT", marketValue: 1000m, cost: 2000m, sectorMv: 1000m) },
            D, 2000m).Single();
        Assert.Equal(0m, d.SuggestedAmount);
        Assert.Contains(d.Blockers, b => b.Contains("条件③"));
    }

    [Fact]
    public void FundWeightJustUnder10Percent_Passes()
    {
        // 当前 9% < 10%，投后 950/10050 = 9.45% 仍 < 10%
        var d = DcaDecisionEngine.Build(
            new[] { Eq("OK", marketValue: 900m, cost: 2000m, sectorMv: 900m) }, D, 2000m).Single();
        Assert.Equal(50m, d.SuggestedAmount);
    }

    [Fact]
    public void SectorWeightAt15Percent_BlocksCondition4()
    {
        // 单只仅 1%，但赛道 1500 → 15%，严格小于不通过
        var d = DcaDecisionEngine.Build(
            new[] { Eq("SECTOR", marketValue: 100m, cost: 200m, sectorMv: 1500m) }, D, 2000m).Single();
        Assert.Equal(0m, d.SuggestedAmount);
        Assert.Contains(d.Blockers, b => b.Contains("条件④"));
    }

    [Fact]
    public void PostInvestmentCap_BindsAtBoundary()
    {
        // D=1000，单只 98 元 → 当前 9.8% 满足；投 50 后 148/1050=14.1%？
        // 用更紧的边界：D=1000，mv=98 → 投后 (98+50)/1050=14.1% 仍过；改用 mv=95 → 145/1050=13.8% 过。
        // 真正边界：cap=(0.1D-mv)/0.9；mv=95.56 时 cap≈4.93 < 50 → 投后越线，拦截
        var d = DcaDecisionEngine.Build(
            new[] { Eq("EDGE", marketValue: 95.56m, cost: 200m, sectorMv: 95.56m) }, 1000m, 2000m).Single();
        Assert.Equal(0m, d.SuggestedAmount);
        Assert.Contains(d.Blockers, b => b.Contains("条件③"));
    }

    [Fact]
    public void OverlapCovered_Blocks()
    {
        var d = DcaDecisionEngine.Build(new[] { Eq("DUP", coveredBy: "MAIN") }, D, 2000m).Single();
        Assert.Equal(0m, d.SuggestedAmount);
        Assert.Contains(d.Blockers, b => b.Contains("去重"));
    }

    [Fact]
    public void StableFunds_NeverGetSuggestion()
    {
        var d = DcaDecisionEngine.Build(new[] { Stable() }, D, 2000m).Single();
        Assert.Equal(SignalLevel.Stable, d.Signal);
        Assert.Equal(0m, d.SuggestedAmount);
        Assert.Empty(d.Blockers);
    }

    [Fact]
    public void Budget_LowestPercentileFirst_AndExhausts()
    {
        // 两只均达标：A 百分位 5%，B 百分位 20%；预算仅 50 → 更低估的 A 中标
        var inputs = new[]
        {
            Eq("B", percentile: 20m),
            Eq("A", percentile: 5m),
        };
        var decisions = DcaDecisionEngine.Build(inputs, D, 50m);
        Assert.Equal(50m, decisions.Single(x => x.FundCode == "A").SuggestedAmount);
        var b = decisions.Single(x => x.FundCode == "B");
        Assert.Equal(0m, b.SuggestedAmount);
        Assert.Contains(b.Blockers, x => x.Contains("预算"));
    }

    [Fact]
    public void BudgetZero_NoneBought()
    {
        var decisions = DcaDecisionEngine.Build(new[] { Eq("A"), Eq("B", percentile: 20m) }, D, 0m);
        Assert.All(decisions, d => Assert.Equal(0m, d.SuggestedAmount));
        Assert.All(decisions, d => Assert.Contains(d.Blockers, b => b.Contains("预算")));
    }

    [Fact]
    public void MultipleFailures_AllReported()
    {
        // 黄灯 + 正收益 + 单只超线 + 赛道超线：四条原因都应列出
        var input = Eq("BAD", marketValue: 1600m, cost: 100m, percentile: 50m, sectorMv: 2000m);
        var d = DcaDecisionEngine.Build(new[] { input }, D, 2000m).Single();
        Assert.Equal(0m, d.SuggestedAmount);
        Assert.Contains(d.Blockers, b => b.Contains("条件①"));
        Assert.Contains(d.Blockers, b => b.Contains("条件②"));
        Assert.Contains(d.Blockers, b => b.Contains("条件③"));
        Assert.Contains(d.Blockers, b => b.Contains("条件④"));
    }
}
