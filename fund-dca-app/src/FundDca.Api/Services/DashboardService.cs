using FundDca.Api.Models;
using FundDca.Core.Domain;
using FundDca.Core.Rules;
using FundDca.Data;
using Microsoft.EntityFrameworkCore;

namespace FundDca.Api.Services;

/// <summary>
/// 盘后看板聚合：档案 + 当前持仓 + 最新净值 + 货币手工市值 + 当月预算 → DashboardDto。
/// 占比/收益率一律调用 Core 纯函数，保证与单元测试同一口径。
/// </summary>
public class DashboardService(FundDcaDbContext db)
{
    public async Task<DashboardDto> BuildAsync(CancellationToken ct = default)
    {
        var funds = await db.Funds.AsNoTracking()
            .Where(f => f.IsActive)
            .Include(f => f.Sector)
            .OrderBy(f => f.Sector!.SortOrder).ThenBy(f => f.Code)
            .ToListAsync(ct);

        var holdings = (await db.Holdings.AsNoTracking().ToListAsync(ct))
            .ToDictionary(h => h.FundCode);

        var manualValues = (await db.ManualValues.AsNoTracking().ToListAsync(ct))
            .ToDictionary(m => m.FundCode);

        // 每只基金取最新一条净值（开发期数据量小，内存分组；后期改为窗口函数 SQL）
        var latestNavs = (await db.FundNavs.AsNoTracking().ToListAsync(ct))
            .GroupBy(n => n.FundCode)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(n => n.TradeDate).First());

        var yearMonth = DateTime.Now.ToString("yyyy-MM");
        var budget = await db.BudgetMonths.AsNoTracking()
            .FirstOrDefaultAsync(b => b.YearMonth == yearMonth, ct);

        var asOfDate = latestNavs.Values
            .Where(n => funds.Any(f => f.Code == n.FundCode && f.Type != FundType.Money && !f.UseManualValue))
            .Select(n => (DateOnly?)n.TradeDate)
            .DefaultIfEmpty(null)
            .Max();

        var rows = new List<DashboardRowDto>();
        var containsSeed = false;

        foreach (var f in funds)
        {
            var useManual = f.UseManualValue || f.Type == FundType.Money;
            holdings.TryGetValue(f.Code, out var holding);
            latestNavs.TryGetValue(f.Code, out var nav);
            manualValues.TryGetValue(f.Code, out var manual);

            decimal marketValue;
            decimal? totalReturn = null;

            if (useManual)
            {
                marketValue = Math.Round(manual?.MarketValue ?? 0m, 2);
            }
            else if (holding is not null && nav is not null)
            {
                marketValue = Math.Round(holding.Shares * nav.UnitNav, 2);
                totalReturn = holding.CostAmount is { } cost && cost > 0m
                    ? PortfolioMath.TotalReturnPercent(marketValue, cost)
                    : null;
            }
            else
            {
                marketValue = 0m; // 观察池/未建仓基金
            }

            if (nav?.Source == "SEED")
            {
                containsSeed = true;
            }

            rows.Add(new DashboardRowDto
            {
                Code = f.Code,
                Name = f.Name,
                Type = f.Type.ToString(),
                SectorId = f.SectorId,
                SectorName = f.Sector?.Name,
                SectorIsEquity = f.Sector?.IsEquity ?? true,
                SectorSortOrder = f.Sector?.SortOrder ?? 999,
                Shares = holding?.Shares,
                CostAmount = holding?.CostAmount,
                UnitNav = nav?.UnitNav,
                NavDate = nav?.TradeDate.ToString("yyyy-MM-dd"),
                DayChangePercent = nav?.DayChangePercent,
                MarketValue = marketValue,
                TotalReturnPercent = totalReturn,
                WeightPercent = 0m, // 分母算出后回填
                Stale = nav is not null && asOfDate is not null && nav.TradeDate < asOfDate,
                IsMoney = useManual,
                Source = nav?.Source ?? (useManual ? "MANUAL" : null),
                ManualValueDate = manual?.ValueDate.ToString("yyyy-MM-dd"),
            });
        }

        var evaluation = PositionCalculator.Evaluate(
            rows.Select(r => new HoldingValue(r.Code, r.Name, ParseType(r.Type), r.MarketValue, r.SectorName)));

        var weightByCode = evaluation.FundWeights.ToDictionary(w => w.FundCode, w => w.WeightPercent);
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i] = rows[i] with { WeightPercent = weightByCode.GetValueOrDefault(rows[i].Code) };
        }

        var equityValue = evaluation.EquityMarketValue;
        var stableValue = evaluation.StableMarketValue;
        var denominator = evaluation.Denominator;

        var equityCost = rows
            .Where(r => !PositionCalculator.IsStable(ParseType(r.Type)))
            .Sum(r => r.CostAmount ?? 0m);
        // 资产证明不含成本：全部权益成本未知时，盈亏指标留空，避免把市值误当盈利
        var hasEquityCost = rows
            .Any(r => !PositionCalculator.IsStable(ParseType(r.Type)) && r.CostAmount is > 0m);
        decimal? equityPnl = hasEquityCost ? PortfolioMath.TotalPnl(equityValue, equityCost) : null;
        decimal? equityPnlPercent = hasEquityCost
            ? PortfolioMath.TotalReturnPercent(equityValue, equityCost)
            : null;

        // 今日盈亏：仅主日期、非滞后、非手工市值且有涨跌幅的行
        var asOf = asOfDate?.ToString("yyyy-MM-dd");
        var dayPnl = rows
            .Where(r => !r.IsMoney && !r.Stale && r.NavDate == asOf && r.DayChangePercent is not null)
            .Sum(r => PortfolioMath.DayPnl(r.MarketValue, r.DayChangePercent!.Value));

        var sectors = rows
            .Where(r => r.SectorName is not null)
            .GroupBy(r => new { r.SectorName, r.SectorIsEquity, r.SectorSortOrder })
            .Select(g => new SectorSliceDto
            {
                Name = g.Key.SectorName!,
                MarketValue = Math.Round(g.Sum(r => r.MarketValue), 2),
                WeightPercent = PositionCalculator.WeightOf(g.Sum(r => r.MarketValue), denominator),
                IsEquity = g.Key.SectorIsEquity,
                SortOrder = g.Key.SectorSortOrder,
            })
            .OrderBy(s => s.SortOrder)
            .ToList();

        return new DashboardDto
        {
            AsOfDate = asOf,
            ContainsSeedData = containsSeed,
            Denominator = denominator,
            EquityMarketValue = equityValue,
            StableMarketValue = stableValue,
            DayPnl = dayPnl,
            EquityCost = equityCost,
            EquityPnl = equityPnl,
            EquityPnlPercent = PortfolioMath.TotalReturnPercent(equityValue, equityCost),
            Budget = budget is null ? null : new BudgetDto
            {
                YearMonth = budget.YearMonth,
                BudgetAmount = budget.BudgetAmount,
                InvestedAmount = budget.InvestedAmount,
                RemainingAmount = Math.Round(budget.BudgetAmount - budget.InvestedAmount, 2),
            },
            Rows = rows,
            Sectors = sectors,
        };
    }

    private static FundType ParseType(string s) => Enum.Parse<FundType>(s);
}
