using FundDca.Core.Domain;
using FundDca.Core.Rules;
using Xunit;

namespace FundDca.Core.Tests;

/// <summary>
/// M3 导入对账规则（PRD 5.3）：先对账后覆盖，差异不静默；
/// 份额增加默认红利再投（不加本金），新基金按名称归类，减份额不静默处理。
/// </summary>
public class ImportReconcilerTests
{
    private const int BondSector = 9;
    private const int MoneySector = 10;

    private static SystemPosition Sys(
        string code = "110020", string name = "沪深300ETF联接A", FundType type = FundType.Stock,
        int? sector = 1, bool manual = false, decimal shares = 3200m,
        decimal? cost = 5000m, decimal mv = 5478.40m, decimal? nav = 1.7120m) =>
        new(code, name, type, sector, sector == 9 ? "债券" : sector == 10 ? "货币" : "大盘",
            manual, shares, cost, mv, nav);

    private static ParsedPosition File(
        string? code, string name = "沪深300ETF联接A", decimal? shares = 3200m, decimal? mv = 5478.40m) =>
        new(code, name, shares, mv);

    [Fact]
    public void 份额一致_判定Matched_无需动作()
    {
        var items = ImportReconciler.Build([File("110020")], [Sys()], BondSector, MoneySector);
        var one = Assert.Single(items);
        Assert.Equal(ReconcileKind.Matched, one.Kind);
        Assert.Equal(ReconcileAction.None, one.Action);
        Assert.Equal(0m, one.SharesDelta);
    }

    [Fact]
    public void 份额增加_默认红利再投_只加份额_建议成本为空()
    {
        // 系统 2000 份、净值 1.05；文件 2050 份
        var sys = Sys(shares: 2000m, nav: 1.05m, mv: 2100m);
        var items = ImportReconciler.Build([File("110020", shares: 2050m, mv: 2152.50m)], [sys], BondSector, MoneySector);
        var one = Assert.Single(items);
        Assert.Equal(ReconcileKind.ShareIncrease, one.Kind);
        Assert.Equal(ReconcileAction.DividendReinvest, one.Action);
        Assert.Equal(50m, one.SharesDelta);
        // 红利再投不增加本金（SuggestedAddedCost 仅在改判手动加仓时参考）
        Assert.Equal(52.50m, one.SuggestedAddedCost);
        Assert.Contains(ReconcileAction.ManualAdd, one.AllowedActions);
        Assert.Contains(ReconcileAction.KeepSystem, one.AllowedActions);
    }

    [Fact]
    public void 份额减少_默认保留系统值_且只能保留()
    {
        var sys = Sys(shares: 2000m, mv: 2100m);
        var items = ImportReconciler.Build([File("110020", shares: 1900m, mv: 1995m)], [sys], BondSector, MoneySector);
        var one = Assert.Single(items);
        Assert.Equal(ReconcileKind.ShareDecrease, one.Kind);
        Assert.Equal(ReconcileAction.KeepSystem, one.Action);
        Assert.Equal([ReconcileAction.KeepSystem], one.AllowedActions);
        Assert.Equal(-100m, one.SharesDelta);
    }

    [Fact]
    public void 新债券基金_默认归入稳健类_建仓()
    {
        var items = ImportReconciler.Build(
            [new ParsedPosition("007403", "安信短债债券C", 900m, 900.50m)],
            [], BondSector, MoneySector);
        var one = Assert.Single(items);
        Assert.Equal(ReconcileKind.NewFund, one.Kind);
        Assert.Equal(FundType.Bond, one.ProposedType);
        Assert.Equal(BondSector, one.SectorId);
        Assert.False(one.IsManual);
        Assert.Equal(ReconcileAction.CreateFund, one.Action);
    }

    [Fact]
    public void 新货币基金_手工市值_货币赛道()
    {
        var items = ImportReconciler.Build(
            [new ParsedPosition("018093", "某某现金添利货币", null, 5000m)],
            [], BondSector, MoneySector);
        var one = Assert.Single(items);
        Assert.Equal(FundType.Money, one.ProposedType);
        Assert.True(one.IsManual);
        Assert.Equal(MoneySector, one.SectorId);
    }

    [Fact]
    public void 新权益基金_不预选赛道_等待用户选择()
    {
        var items = ImportReconciler.Build(
            [new ParsedPosition("999999", "某某行业股票A", 100m, 100m)],
            [], BondSector, MoneySector);
        var one = Assert.Single(items);
        Assert.Equal(FundType.Stock, one.ProposedType);
        Assert.Null(one.SectorId);
    }

    [Fact]
    public void 货币市值变化_默认接受文件值()
    {
        var sys = Sys(code: "018092", name: "兴银现金添利货币C", type: FundType.Money,
            sector: MoneySector, manual: true, shares: 0m, cost: null, mv: 2030.41m, nav: null);
        var items = ImportReconciler.Build(
            [new ParsedPosition("018092", "兴银现金添利货币C", null, 2100.00m)],
            [sys], BondSector, MoneySector);
        var one = Assert.Single(items);
        Assert.Equal(ReconcileKind.ManualValueChange, one.Kind);
        Assert.Equal(ReconcileAction.AcceptFileValue, one.Action);
        Assert.Equal(2100.00m, one.FileMarketValue);
    }

    [Fact]
    public void 系统有文件无_保留系统持仓_不删除()
    {
        var items = ImportReconciler.Build([], [Sys()], BondSector, MoneySector);
        var one = Assert.Single(items);
        Assert.Equal(ReconcileKind.MissingInFile, one.Kind);
        Assert.Equal(ReconcileAction.KeepSystem, one.Action);
        Assert.Null(one.FileShares);
    }

    [Fact]
    public void 无法识别代码_UnknownRow_默认忽略()
    {
        var items = ImportReconciler.Build([File(null, "某无法识别的行", 10m, 10m)], [Sys()], BondSector, MoneySector);
        Assert.Contains(items, i => i.Kind == ReconcileKind.UnknownRow && i.Action == ReconcileAction.KeepSystem);
        Assert.Contains(items, i => i.Kind == ReconcileKind.MissingInFile);
    }

    [Fact]
    public void 名称推断类型_覆盖债券货币QDII与股票()
    {
        Assert.Equal(FundType.Bond, ImportReconciler.InferType("华夏短债债券A"));
        Assert.Equal(FundType.Money, ImportReconciler.InferType("天弘余额宝货币"));
        Assert.Equal(FundType.Qdii, ImportReconciler.InferType("华安恒生科技ETF联接(QDII)A"));
        Assert.Equal(FundType.Stock, ImportReconciler.InferType("创业板ETF联接A"));
    }
}
