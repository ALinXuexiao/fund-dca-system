using FundDca.Core.Domain;

namespace FundDca.Core.Rules;

/// <summary>
/// 单只基金的当前市值输入（M1 起由日快照按"份额 × 单位净值"产出；货币基金为手工市值）。
/// </summary>
public sealed record HoldingValue(
    string FundCode,
    string FundName,
    FundType Type,
    decimal MarketValue,
    string? SectorName);

public sealed record FundWeight(
    string FundCode,
    string FundName,
    FundType Type,
    decimal MarketValue,
    decimal WeightPercent);

public sealed record SectorWeight(
    string SectorName,
    decimal MarketValue,
    decimal WeightPercent,
    bool IsEquity);

/// <summary>
/// 持仓结构评估结果。
/// </summary>
public sealed record PortfolioEvaluation(
    decimal Denominator,
    decimal EquityMarketValue,
    decimal StableMarketValue,
    IReadOnlyList<FundWeight> FundWeights,
    IReadOnlyList<SectorWeight> SectorWeights);

/// <summary>
/// 持仓占比口径（PRD 3.1）：
/// 分母 D = 全部在管基金当前市值之和（股票/混合/QDII/债券/货币）；
/// 银行卡上的当月待投预算不计入 D；货币基金市值为手工值。
/// B3/B4 为严格小于，等于红线不通过。
/// </summary>
public static class PositionCalculator
{
    public const decimal SingleFundLimitPercent = 10m;
    public const decimal SectorLimitPercent = 15m;

    /// <summary>债券、货币属于稳健资产：计入 D，但不参与权益定投判定。</summary>
    public static bool IsStable(FundType type) => type is FundType.Bond or FundType.Money;

    /// <summary>持仓分母 D：全部在管基金市值之和。银行卡待投预算不传入本方法。</summary>
    public static decimal Denominator(IEnumerable<HoldingValue> holdings) =>
        holdings.Sum(h => h.MarketValue);

    /// <summary>单只占比（百分数，保留 4 位小数）；D 为 0 时返回 0，不抛异常。</summary>
    public static decimal WeightOf(decimal marketValue, decimal denominator) =>
        denominator <= 0m ? 0m : Math.Round(marketValue / denominator * 100m, 4);

    /// <summary>B3：单只权益基金占比严格小于 10%；等于 10% 不通过。</summary>
    public static bool PassesSingleFundLimit(decimal weightPercent, decimal limit = SingleFundLimitPercent) =>
        weightPercent < limit;

    /// <summary>B4：权益赛道占比严格小于 15%；等于 15% 不通过。</summary>
    public static bool PassesSectorLimit(decimal weightPercent, decimal limit = SectorLimitPercent) =>
        weightPercent < limit;

    /// <summary>一次性算清分母、权益/稳健市值、单只与赛道占比。</summary>
    public static PortfolioEvaluation Evaluate(IEnumerable<HoldingValue> holdings)
    {
        var list = holdings as IList<HoldingValue> ?? holdings.ToList();
        var denominator = Denominator(list);

        var fundWeights = list
            .Select(h => new FundWeight(
                h.FundCode, h.FundName, h.Type,
                h.MarketValue, WeightOf(h.MarketValue, denominator)))
            .ToList();

        var sectorWeights = list
            .Where(h => !string.IsNullOrWhiteSpace(h.SectorName))
            .GroupBy(h => h.SectorName!)
            .Select(g =>
            {
                var marketValue = g.Sum(x => x.MarketValue);
                return new SectorWeight(
                    g.Key,
                    marketValue,
                    WeightOf(marketValue, denominator),
                    g.All(x => !IsStable(x.Type)));
            })
            .OrderByDescending(s => s.MarketValue)
            .ToList();

        return new PortfolioEvaluation(
            denominator,
            list.Where(h => !IsStable(h.Type)).Sum(h => h.MarketValue),
            list.Where(h => IsStable(h.Type)).Sum(h => h.MarketValue),
            fundWeights,
            sectorWeights);
    }
}
