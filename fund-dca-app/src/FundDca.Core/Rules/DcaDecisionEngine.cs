using FundDca.Core.Domain;

namespace FundDca.Core.Rules;

/// <summary>
/// 单只基金的决策输入（由 API 聚合层从档案/估值/持仓装配，引擎不碰数据库）。
/// </summary>
public sealed record DecisionInput(
    string FundCode,
    string FundName,
    FundType Type,
    string? SectorName,
    decimal MarketValue,
    /// <summary>当前持仓周期累计投入本金；null 表示成本未知</summary>
    decimal? CostAmount,
    /// <summary>所属赛道当前总市值</summary>
    decimal SectorMarketValue,
    /// <summary>
    /// 判定值：PE/PB 口径为历史百分位（%，越低越便宜）；
    /// EarningsYield 口径为盈利收益率（%，越高越便宜）；无数据为 null。
    /// </summary>
    decimal? Percentile,
    decimal LowThresholdPercent,
    decimal HighThresholdPercent,
    /// <summary>非 null 表示该基金在去重组中被主基金覆盖（B5），值为主基金代码</summary>
    string? CoveredByPrimaryCode,
    /// <summary>估值口径，决定判定值含义、阈值方向与排序方向</summary>
    ValuationMetric Metric = ValuationMetric.PeTtm);

/// <summary>单只基金的决策输出。</summary>
public sealed record FundDecision(
    string FundCode,
    string FundName,
    string? SectorName,
    SignalLevel Signal,
    decimal? Percentile,
    /// <summary>持有总收益率 %（(市值-本金)/本金）；成本未知为 null</summary>
    decimal? TotalReturnPercent,
    /// <summary>当前单只占比 %</summary>
    decimal WeightPercent,
    /// <summary>当前赛道占比 %</summary>
    decimal SectorWeightPercent,
    /// <summary>最终建议金额：固定额（四条件全满足）或 0</summary>
    decimal SuggestedAmount,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Notes);

/// <summary>引擎参数（单次定投固定额 + 两条红线，页面可改固定额）。</summary>
public sealed record DecisionOptions(
    decimal FixedInvestAmount = 50m,
    decimal SingleFundLimitPercent = 10m,
    decimal SectorLimitPercent = 15m);

/// <summary>
/// 定投决策引擎（用户实盘规则，纯函数、无副作用）。
/// 必须同时满足以下 4 个条件，本周才对该基金定投固定额（默认 50 元），任一不满足即不操作：
/// ① 跟踪指数处于低估区间：PE/PB 看历史百分位 &lt; 低估阈值；盈利收益率（E/P=1/PE）看绝对值 ≥ 低估线（默认 10%）；
/// ② 该基金总收益率为负：当前市值 &lt; 累计投入本金；
/// ③ 该基金当前市值占比 &lt; 10%（严格小于）；
/// ④ 所属赛道当前市值总占比 &lt; 15%（严格小于）。
/// 附加闸门：去重组内非主基金不出建议；全部建议合计不得超过当月剩余预算；
/// 投后占比也必须严格低于红线（固定额下做安全校验，等于红线即不通过）。
/// 预算分配：候选基金按百分位从低到高（越跌越优先）依次分配固定额。
/// </summary>
public static class DcaDecisionEngine
{
    /// <summary>判定值的展示名（条件①文案用）。</summary>
    private static string ValueLabel(ValuationMetric metric) =>
        metric == ValuationMetric.EarningsYield ? "盈利收益率" : "估值百分位";

    public static SignalLevel SignalOf(decimal? value, decimal low, decimal high, FundType type,
        ValuationMetric metric = ValuationMetric.PeTtm)
    {
        if (PositionCalculator.IsStable(type))
        {
            return SignalLevel.Stable;
        }
        if (value is null)
        {
            return SignalLevel.NoData;
        }

        var v = value.Value;
        if (metric == ValuationMetric.EarningsYield)
        {
            // 盈利收益率：越高越便宜。≥ 低估线绿灯；≤ 高估线红灯；之间黄灯
            if (v >= low)
            {
                return SignalLevel.Green;
            }
            return v > high ? SignalLevel.Yellow : SignalLevel.Red;
        }

        // PE/PB 历史百分位：越低越便宜
        if (v < low)
        {
            return SignalLevel.Green;
        }
        return v < high ? SignalLevel.Yellow : SignalLevel.Red;
    }

    /// <summary>B3：投后单只占比仍严格低于红线所允许的追加金额上界（元）。上界为负表示已超限。</summary>
    public static decimal MaxAddByFund(decimal marketValue, decimal denominator, decimal limitPercent)
    {
        var l = limitPercent / 100m;
        return Math.Round((l * denominator - marketValue) / (1m - l), 2);
    }

    /// <summary>B4：投后赛道占比仍严格低于红线所允许的追加金额上界（元）。</summary>
    public static decimal MaxAddBySector(decimal sectorMarketValue, decimal denominator, decimal limitPercent)
    {
        var l = limitPercent / 100m;
        return Math.Round((l * denominator - sectorMarketValue) / (1m - l), 2);
    }

    /// <summary>
    /// 对全部在管基金（含稳健类，标记 Stable、不出建议）做一次完整决策。
    /// </summary>
    public static IReadOnlyList<FundDecision> Build(
        IEnumerable<DecisionInput> inputs,
        decimal denominator,
        decimal remainingBudget,
        DecisionOptions? options = null)
    {
        var opt = options ?? new DecisionOptions();
        var list = inputs as IList<DecisionInput> ?? inputs.ToList();

        var raw = new List<(DecisionInput Input, FundDecision Decision, bool Eligible)>();

        foreach (var f in list)
        {
            var signal = SignalOf(f.Percentile, f.LowThresholdPercent, f.HighThresholdPercent, f.Type, f.Metric);
            var blockers = new List<string>();
            var notes = new List<string>();
            var valueLabel = ValueLabel(f.Metric);
            var isEy = f.Metric == ValuationMetric.EarningsYield;

            if (signal == SignalLevel.Stable)
            {
                raw.Add((f, new FundDecision(f.FundCode, f.FundName, f.SectorName, signal, null, null,
                    PositionCalculator.WeightOf(f.MarketValue, denominator),
                    PositionCalculator.WeightOf(f.SectorMarketValue, denominator),
                    0m, blockers, notes), false));
                continue;
            }

            var weight = PositionCalculator.WeightOf(f.MarketValue, denominator);
            var sectorWeight = PositionCalculator.WeightOf(f.SectorMarketValue, denominator);
            decimal? totalReturn = f.CostAmount is { } cost && cost > 0m
                ? Math.Round((f.MarketValue - cost) / cost * 100m, 2)
                : null;

            // 条件①：低估绿灯
            switch (signal)
            {
                case SignalLevel.NoData:
                    blockers.Add("条件① 不满足：暂无指数估值数据，无法判定低估区间");
                    break;
                case SignalLevel.Red when isEy:
                    blockers.Add($"条件① 不满足：{valueLabel} {f.Percentile:F2}% ≤ 高估线 {f.HighThresholdPercent:F1}%，暂停定投");
                    break;
                case SignalLevel.Red:
                    blockers.Add($"条件① 不满足：{valueLabel} {f.Percentile:F1}% ≥ 高估线 {f.HighThresholdPercent:F0}%，暂停定投");
                    break;
                case SignalLevel.Yellow when isEy:
                    blockers.Add($"条件① 不满足：{valueLabel} {f.Percentile:F2}% 未达到低估线（≥ {f.LowThresholdPercent:F0}% 才买）");
                    break;
                case SignalLevel.Yellow:
                    blockers.Add($"条件① 不满足：{valueLabel} {f.Percentile:F1}% 未进入低估区间（低于 {f.LowThresholdPercent:F0}% 才买）");
                    break;
            }

            // 条件②：总收益率为负
            if (totalReturn is null)
            {
                blockers.Add("条件② 不满足：缺累计投入本金数据，无法判定总收益正负");
            }
            else if (totalReturn >= 0m)
            {
                blockers.Add($"条件② 不满足：总收益率 {totalReturn:F2}% 为正，亏损基金才补仓");
            }

            // 条件③：单只当前占比严格 < 10%
            if (weight >= opt.SingleFundLimitPercent)
            {
                blockers.Add($"条件③ 不满足：当前单只占比 {weight:F2}% ≥ {opt.SingleFundLimitPercent:F0}%");
            }

            // 条件④：赛道当前占比严格 < 15%
            if (sectorWeight >= opt.SectorLimitPercent)
            {
                blockers.Add($"条件④ 不满足：当前赛道（{f.SectorName}）占比 {sectorWeight:F2}% ≥ {opt.SectorLimitPercent:F0}%");
            }

            // B5 去重
            if (!string.IsNullOrEmpty(f.CoveredByPrimaryCode))
            {
                blockers.Add($"去重：同组已由主基金 {f.CoveredByPrimaryCode} 覆盖，本只不出建议");
            }

            // 投后红线安全校验：当前占比虽低于红线，但加上固定额后会越线（边界情况）
            var capFund = MaxAddByFund(f.MarketValue, denominator, opt.SingleFundLimitPercent);
            var capSector = MaxAddBySector(f.SectorMarketValue, denominator, opt.SectorLimitPercent);
            if (!blockers.Any() && capFund < opt.FixedInvestAmount)
            {
                blockers.Add($"条件③ 不满足：投后单只占比将达到/超过 {opt.SingleFundLimitPercent:F0}%（最多可加 {Math.Max(0m, Math.Floor(capFund)):F0} 元）");
            }
            if (!blockers.Any() && capSector < opt.FixedInvestAmount)
            {
                blockers.Add($"条件④ 不满足：投后赛道占比将达到/超过 {opt.SectorLimitPercent:F0}%（最多可加 {Math.Max(0m, Math.Floor(capSector)):F0} 元）");
            }

            var eligible = blockers.Count == 0;
            if (eligible)
            {
                var cheapText = isEy
                    ? $"低估（{valueLabel} {f.Percentile:F2}%）"
                    : $"低估（{valueLabel} {f.Percentile:F1}%）";
                notes.Add($"四条件全部满足：{cheapText}、收益 {totalReturn:F2}%、单只 {weight:F2}%、赛道 {sectorWeight:F2}%");
            }

            raw.Add((f, new FundDecision(f.FundCode, f.FundName, f.SectorName, signal, f.Percentile,
                totalReturn, weight, sectorWeight, 0m, blockers, notes), eligible));
        }

        // 预算闸门：候选基金按"越便宜越优先"分配固定额。
        // 百分位口径从低到高；盈利收益率口径从高到低（取负统一为升序）。
        static decimal CheapnessKey(DecisionInput i) => i.Metric == ValuationMetric.EarningsYield
            ? -(i.Percentile ?? -9999m)
            : i.Percentile ?? 9999m;
        var remaining = Math.Max(0m, remainingBudget);
        foreach (var (input, decision, eligible) in raw
                     .Where(x => x.Eligible)
                     .OrderBy(x => CheapnessKey(x.Input))
                     .ToList())
        {
            var idx = raw.FindIndex(x => x.Input.FundCode == input.FundCode);
            if (remaining < opt.FixedInvestAmount)
            {
                var blockers = decision.Blockers.ToList();
                blockers.Add($"预算：当月剩余预算 {remaining:F0} 元不足单次定投 {opt.FixedInvestAmount:F0} 元");
                raw[idx] = (input, decision with { Blockers = blockers }, false);
                continue;
            }

            var notes = decision.Notes.ToList();
            notes.Add($"建议定投 {opt.FixedInvestAmount:F0} 元");
            raw[idx] = (input, decision with { SuggestedAmount = opt.FixedInvestAmount, Notes = notes }, true);
            remaining -= opt.FixedInvestAmount;
        }

        return raw.Select(x => x.Decision).ToList();
    }
}
