using FundDca.Api.Models;
using FundDca.Core.Domain;
using FundDca.Core.Rules;
using FundDca.Data;
using Microsoft.EntityFrameworkCore;

namespace FundDca.Api.Services;

/// <summary>
/// 今日定投决策聚合：档案 + 持仓成本/市值 + 最新指数估值 + 去重组 + 当月预算 + 定投设置 →
/// Core 纯引擎 DcaDecisionEngine 输出红绿灯与固定额建议（四条件同时满足才投）。
/// </summary>
public class DecisionService(FundDcaDbContext db)
{
    /// <summary>单行设置不存在时的兜底（正常由播种保证存在）。</summary>
    private async Task<DcaSetting> GetSettingAsync(CancellationToken ct)
    {
        var s = await db.DcaSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        return s ?? new DcaSetting { Id = 1, FixedInvestAmount = 50m };
    }

    public async Task<DecisionDto> BuildTodayAsync(CancellationToken ct = default)
    {
        var setting = await GetSettingAsync(ct);
        var options = new DecisionOptions(
            setting.FixedInvestAmount,
            PositionCalculator.SingleFundLimitPercent,
            PositionCalculator.SectorLimitPercent);

        var funds = await db.Funds.AsNoTracking()
            .Where(f => f.IsActive)
            .Include(f => f.Sector)
            .Include(f => f.TrackedIndex)
            .OrderBy(f => f.Sector!.SortOrder).ThenBy(f => f.Code)
            .ToListAsync(ct);

        var holdings = (await db.Holdings.AsNoTracking().ToListAsync(ct))
            .ToDictionary(h => h.FundCode);
        var manual = (await db.ManualValues.AsNoTracking().ToListAsync(ct))
            .ToDictionary(m => m.FundCode);
        var latestNavs = (await db.FundNavs.AsNoTracking().ToListAsync(ct))
            .GroupBy(n => n.FundCode)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(n => n.TradeDate).First());

        // 每只基金当前市值（与看板同一口径）
        decimal MarketValue(Fund f)
        {
            if (f.UseManualValue || f.Type == FundType.Money)
            {
                return Math.Round(manual.GetValueOrDefault(f.Code)?.MarketValue ?? 0m, 2);
            }
            if (holdings.TryGetValue(f.Code, out var h) && latestNavs.TryGetValue(f.Code, out var n))
            {
                return Math.Round(h.Shares * n.UnitNav, 2);
            }
            return 0m;
        }

        var mvByCode = funds.ToDictionary(f => f.Code, MarketValue);
        var denominator = mvByCode.Values.Sum();

        // 赛道市值
        var sectorMv = funds
            .Where(f => f.Sector is not null)
            .GroupBy(f => f.Sector!.Name)
            .ToDictionary(g => g.Key, g => g.Sum(f => mvByCode[f.Code]));

        // 最新估值（每指数取最新一条）
        var latestVal = (await db.IndexValuations.AsNoTracking().ToListAsync(ct))
            .GroupBy(v => v.IndexCode)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.TradeDate).First());

        // B5 去重：非主基金 → 同组主基金代码
        var groups = await db.OverlapGroups.AsNoTracking()
            .Include(g => g.Members).ToListAsync(ct);
        var coveredBy = new Dictionary<string, string>();
        foreach (var g in groups)
        {
            var primary = g.Members.FirstOrDefault(m => m.IsPrimary)?.FundCode;
            if (primary is null)
            {
                continue;
            }
            foreach (var m in g.Members.Where(m => !m.IsPrimary))
            {
                coveredBy[m.FundCode] = primary;
            }
        }

        var yearMonth = DateTime.Now.ToString("yyyy-MM");
        var budget = await db.BudgetMonths.AsNoTracking()
            .FirstOrDefaultAsync(b => b.YearMonth == yearMonth, ct);
        var remaining = budget is null ? 0m : Math.Max(0m, budget.BudgetAmount - budget.InvestedAmount);

        var inputs = new List<DecisionInput>();
        foreach (var f in funds)
        {
            var metricKind = f.TrackedIndex?.Metric ?? ValuationMetric.None;
            decimal? percentile = null;
            if (f.TrackedIndex is { Metric: not ValuationMetric.None } idx &&
                latestVal.TryGetValue(idx.Code, out var v))
            {
                percentile = idx.Metric switch
                {
                    // 盈利收益率 E/P = 1/PE-TTM × 100%，越高越便宜；PE 缺失/非正视为无数据
                    ValuationMetric.EarningsYield => v.PeTtm is > 0m ? Math.Round(100m / v.PeTtm.Value, 2) : null,
                    ValuationMetric.Pb => v.PbPercentile,
                    _ => v.PePercentile
                };
            }

            inputs.Add(new DecisionInput(
                f.Code, f.Name, f.Type, f.Sector?.Name,
                mvByCode[f.Code],
                holdings.GetValueOrDefault(f.Code)?.CostAmount,
                f.Sector is null ? 0m : sectorMv.GetValueOrDefault(f.Sector.Name),
                percentile,
                f.TrackedIndex?.LowThresholdPercent ?? 30m,
                f.TrackedIndex?.HighThresholdPercent ?? 70m,
                coveredBy.GetValueOrDefault(f.Code),
                metricKind));
        }

        var decisions = DcaDecisionEngine.Build(inputs, denominator, remaining, options);

        var valuationDate = latestVal.Values
            .Select(v => (DateOnly?)v.TradeDate)
            .DefaultIfEmpty(null).Max()?.ToString("yyyy-MM-dd");

        var dtos = new List<FundDecisionDto>();
        foreach (var d in decisions)
        {
            var f = funds.First(x => x.Code == d.FundCode);
            var idx = f.TrackedIndex;
            IndexValuation? val = null;
            if (idx is not null)
            {
                latestVal.TryGetValue(idx.Code, out val);
            }

            var metric = idx?.Metric.ToString();
            decimal? metricValue = null;
            if (idx is not null && val is not null && idx.Metric != ValuationMetric.None)
            {
                metricValue = idx.Metric switch
                {
                    ValuationMetric.Pb => val.Pb,
                    // 盈利收益率以百分数展示（11.69 表示 11.69%）
                    ValuationMetric.EarningsYield => val.PeTtm is > 0m ? Math.Round(100m / val.PeTtm.Value, 2) : null,
                    _ => val.PeTtm
                };
            }

            dtos.Add(new FundDecisionDto
            {
                Code = d.FundCode,
                Name = d.FundName,
                Type = f.Type.ToString(),
                SectorName = f.Sector?.Name,
                MarketValue = mvByCode[d.FundCode],
                WeightPercent = d.WeightPercent,
                SectorWeightPercent = d.SectorWeightPercent,
                Signal = d.Signal.ToString(),
                // 即使无估值口径（REITs）也展示跟踪指数名称，不再显示"无跟踪指数"
                IndexName = idx?.Name,
                IndexCode = idx?.Code,
                Metric = idx is null || idx.Metric == ValuationMetric.None ? null : metric,
                MetricValue = metricValue,
                // 盈利收益率口径展示绝对值 E/P（metricValue），不展示历史百分位
                Percentile = idx?.Metric == ValuationMetric.EarningsYield ? null : d.Percentile,
                ValuationDate = val?.TradeDate.ToString("yyyy-MM-dd"),
                TotalReturnPercent = d.TotalReturnPercent,
                SuggestedAmount = d.SuggestedAmount,
                Blockers = d.Blockers.ToList(),
                Notes = d.Notes.ToList(),
            });
        }

        var totalSuggested = decisions.Sum(d => d.SuggestedAmount);

        return new DecisionDto
        {
            ValuationDate = valuationDate,
            Denominator = denominator,
            Budget = budget is null ? null : new BudgetDto
            {
                YearMonth = budget.YearMonth,
                BudgetAmount = budget.BudgetAmount,
                InvestedAmount = budget.InvestedAmount,
                RemainingAmount = Math.Round(remaining, 2),
            },
            TotalSuggested = totalSuggested,
            RemainingAfter = Math.Round(remaining - totalSuggested, 2),
            Options = new DecisionOptionsDto
            {
                FixedInvestAmount = options.FixedInvestAmount,
                SingleFundLimitPercent = options.SingleFundLimitPercent,
                SectorLimitPercent = options.SectorLimitPercent,
            },
            Funds = dtos,
        };
    }

    /// <summary>更新单次定投固定金额。</summary>
    public async Task<DcaSetting> UpdateFixedAmountAsync(decimal amount, CancellationToken ct)
    {
        var setting = await db.DcaSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (setting is null)
        {
            setting = new DcaSetting { Id = 1 };
            db.DcaSettings.Add(setting);
        }
        setting.FixedInvestAmount = amount;
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return setting;
    }
}
