using FundDca.Core.Domain;
using FundDca.Core.Rules;
using FundDca.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FundDca.Api.Services;

/// <summary>
/// 交易流水：正数买入、负数卖出；登记即预入账，T 日收盘净值到位后校正份额。
/// 买入规则（PRD 3.x，用户确认）：
///  0) 申购费外扣：份额按"净申购金额 = 金额 / (1 + 申购费率)"折算（见 TradeMath），
///     账户累计本金记实付金额（含费），故买入后市值恒小于本金，差额即申购费。
///  1) 登记买入时立即预入账 —— 累加该基金累计本金、扣减当月预算"已投"；
///     份额先按"最近可用净值"预占（今晚净值未到，先记 EstimatedShares）。
///  2) T 日（买入日）收盘净值盘后到位后，按 T 日净值折算真实份额并校正：
///     持有份额 += (ConfirmedShares - EstimatedShares)，流水转 Confirmed。
///  3) T+0/T+1 净值日期统一按">= 买入日的最近一个净值日"确认，兼容 QDII T+1。
/// 卖出规则（赎回费率 0）：
///  - 卖出金额 / 净值 = 份额，登记即预减持仓份额，并按份额比例结转持有成本；
///  - 卖出不占用当月预算；
///  - T 日净值到位后按确认份额校正份额与结转成本。
/// 货币基金/手工市值标的不走净值换算，交易按钮不出现（此处防御性拒绝）。
/// </summary>
public class TradeService(FundDcaDbContext db, ILogger<TradeService> logger)
{
    /// <summary>
    /// 登记一笔交易：正数买入，负数卖出。返回登记结果：
    /// status 为 Confirmed=立即按已有 T 日净值确认；Pending=等今晚净值。
    /// </summary>
    public async Task<TradeResult> RecordTradeAsync(
        string fundCode, decimal signedAmount, DateOnly? tradeDate, CancellationToken ct)
    {
        if (signedAmount == 0m)
        {
            throw new InvalidOperationException("交易金额不能为 0：正数为买入，负数为卖出");
        }

        var fund = await db.Funds
            .AsNoTracking()
            .Include(f => f.Sector)
            .FirstOrDefaultAsync(f => f.Code == fundCode, ct)
            ?? throw new InvalidOperationException($"基金 {fundCode} 不存在");

        if (fund.UseManualValue || fund.Type == FundType.Money)
        {
            throw new InvalidOperationException("货币基金/手工市值标的不支持按净值交易，请直接维护市值");
        }

        var tradeDateValue = tradeDate ?? DateOnly.FromDateTime(DateTime.Now);

        // 最近可用净值（用于预占/预减份额）
        var lastNav = await db.FundNavs
            .AsNoTracking()
            .Where(n => n.FundCode == fundCode)
            .OrderByDescending(n => n.TradeDate)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("该基金暂无净值数据，请先刷新净值再交易");

        return signedAmount > 0m
            ? await RecordBuyAsync(fund, signedAmount, tradeDateValue, lastNav, ct)
            : await RecordSellAsync(fund, Math.Abs(signedAmount), tradeDateValue, lastNav, ct);
    }

    /// <summary>校验并落库一笔买入（登记即预入账）。</summary>
    private async Task<TradeResult> RecordBuyAsync(
        Fund fund, decimal amount, DateOnly buyDate, FundNav lastNav, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // 申购费率（档案缺失视为无费品种）；份额一律按净申购金额折算
        var feeRate = fund.BuyFeeRate ?? 0m;

        // 已有的 T 日（或最近已于盘中落库的净值日）净值 → 立即确认
        var confirmNav = await db.FundNavs
            .AsNoTracking()
            .Where(n => n.FundCode == fund.Code && n.TradeDate >= buyDate)
            .OrderBy(n => n.TradeDate)
            .FirstOrDefaultAsync(ct);

        var estimatedShares = TradeMath.EstimateShares(amount, lastNav.UnitNav, feeRate);
        decimal? confirmedShares = null;
        decimal? confirmedNav = null;
        TradeStatus status;

        if (confirmNav is not null)
        {
            confirmedNav = confirmNav.UnitNav;
            confirmedShares = TradeMath.ConfirmShares(amount, confirmNav.UnitNav, feeRate);
            status = TradeStatus.Confirmed;
        }
        else
        {
            confirmedShares = null;
            status = TradeStatus.Pending;
        }

        // ---------- 预入账：持仓 + 预算 ----------
        var holding = await db.Holdings.FirstOrDefaultAsync(h => h.FundCode == fund.Code, ct);
        if (holding is null)
        {
            holding = new Holding
            {
                FundCode = fund.Code,
                Shares = 0m,
                CostAmount = 0m,
                OpenedAt = buyDate,
                UpdatedAt = now,
            };
            db.Holdings.Add(holding);
        }
        holding.Shares = TradeMath.AddShares(holding.Shares, confirmedShares ?? estimatedShares);
        holding.CostAmount = TradeMath.AddCost(holding.CostAmount ?? 0m, amount);
        holding.UpdatedAt = now;

        // 当月预算"已投"累加。
        // 当月尚无记录时沿用上月预算额建档——不能像原先那样直接按 0 建档，
        // 否则当月首笔买入会把 0 固化，并被后续月份逐月继承下去。
        var budget = await db.CurrentMonthAsync(ct);
        if (budget is null)
        {
            // 全新库从未设置过预算：维持 0，由页面引导用户首次录入
            budget = new BudgetMonth { YearMonth = DateTime.Now.ToString("yyyy-MM") };
            db.BudgetMonths.Add(budget);
        }
        budget.InvestedAmount = Math.Round(budget.InvestedAmount + amount, 4);

        // ---------- 流水 ----------
        var trade = new Trade
        {
            FundCode = fund.Code,
            Side = TradeSide.Buy,
            TradeDate = buyDate,
            Amount = amount,
            EstimatedShares = estimatedShares,
            ConfirmedShares = confirmedShares,
            ConfirmedNav = confirmedNav,
            Status = status,
            CreatedAt = now,
            ConfirmedAt = status == TradeStatus.Confirmed ? now : null,
        };
        db.Trades.Add(trade);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("买入登记 {Code} 金额={Amount} 状态={Status} 预估份额={Est} 确认净值={Nav}",
            fund.Code, amount, status, estimatedShares, confirmedNav);

        return new TradeResult(
            trade.Id, TradeSide.Buy, fund.Code, fund.Name, buyDate, amount,
            confirmedShares ?? estimatedShares, confirmedNav, estimatedShares,
            status == TradeStatus.Confirmed, "按最新净值预约入账，T 日收盘净值到位后自动校正",
            status == TradeStatus.Confirmed ? "当日净值已到位，按 T 日净值确认" : "当日净值尚未到位，今晚自动确认");
    }

    /// <summary>校验并落库一笔卖出（登记即预减份额与结转成本，赎回费率 0）。</summary>
    private async Task<TradeResult> RecordSellAsync(
        Fund fund, decimal amount, DateOnly sellDate, FundNav lastNav, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var holding = await db.Holdings.FirstOrDefaultAsync(h => h.FundCode == fund.Code, ct);
        if (holding is null || holding.Shares <= 0m)
        {
            throw new InvalidOperationException("该基金当前无持仓，不能卖出");
        }

        // 卖出按金额输入、赎回费率 0：份额 = 金额 / 净值
        var estimatedShares = TradeMath.EstimateSellShares(amount, lastNav.UnitNav);

        var confirmNav = await db.FundNavs
            .AsNoTracking()
            .Where(n => n.FundCode == fund.Code && n.TradeDate >= sellDate)
            .OrderBy(n => n.TradeDate)
            .FirstOrDefaultAsync(ct);

        decimal? confirmedShares = null;
        decimal? confirmedNav = null;
        TradeStatus status;

        if (confirmNav is not null)
        {
            confirmedNav = confirmNav.UnitNav;
            confirmedShares = TradeMath.ConfirmSellShares(amount, confirmedNav.Value);
            status = TradeStatus.Confirmed;
        }
        else
        {
            status = TradeStatus.Pending;
        }

        var sharesToDeduct = confirmedShares ?? estimatedShares;
        if (sharesToDeduct > holding.Shares)
        {
            var approxAmount = Math.Round(holding.Shares * lastNav.UnitNav, 2);
            throw new InvalidOperationException(
                $"卖出金额超出可用持仓：当前持有 {holding.Shares:0.####} 份，按最近净值约 ¥{approxAmount:0.00}");
        }

        // 按份额比例结转成本，保持剩余持仓单位成本不变
        var costBasis = TradeMath.ProportionalCost(holding.CostAmount, holding.Shares, sharesToDeduct);
        holding.Shares = TradeMath.AddShares(holding.Shares, -sharesToDeduct);
        if (holding.CostAmount is { } cost)
        {
            holding.CostAmount = Math.Round(Math.Max(0m, cost - costBasis), 4);
        }
        holding.UpdatedAt = now;

        // 卖出不占用、也不反还当月预算"已投"：预算约束的是买入投入。

        var trade = new Trade
        {
            FundCode = fund.Code,
            Side = TradeSide.Sell,
            TradeDate = sellDate,
            Amount = amount,
            EstimatedShares = estimatedShares,
            CostAmount = costBasis,
            ConfirmedShares = confirmedShares,
            ConfirmedNav = confirmedNav,
            Status = status,
            CreatedAt = now,
            ConfirmedAt = status == TradeStatus.Confirmed ? now : null,
        };
        db.Trades.Add(trade);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("卖出登记 {Code} 金额={Amount} 状态={Status} 预估份额={Est} 结转成本={Cost} 确认净值={Nav}",
            fund.Code, amount, status, estimatedShares, costBasis, confirmedNav);

        return new TradeResult(
            trade.Id, TradeSide.Sell, fund.Code, fund.Name, sellDate, amount,
            confirmedShares ?? estimatedShares, confirmedNav, estimatedShares,
            status == TradeStatus.Confirmed, "按最近净值预减份额并结转成本，T 日收盘净值到位后自动校正",
            status == TradeStatus.Confirmed ? "当日净值已到位，按 T 日净值确认卖出份额" : "当日净值尚未到位，今晚自动确认卖出份额");
    }

    /// <summary>
    /// 将待确认交易按 T 日净值校正并转 Confirmed。
    /// 买入：持有份额 += (真实份额 - 预估份额)，份额按净申购金额折算；
    /// 卖出：按确认份额回补/续减份额，并按登记时的单份成本同步调整结转成本。
    /// 应在净值刷新完成后调用（/api/collect/refresh 成功之后）。
    /// </summary>
    public async Task<int> ConfirmPendingAsync(CancellationToken ct)
    {
        var pendings = await db.Trades
            .Where(t => t.Status == TradeStatus.Pending)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;

        var confirmedCount = 0;

        // 分组取各基金全部净值与申购费率（避免逐笔查询）
        var codes = pendings.Select(t => t.FundCode).Distinct().ToList();
        var navsByCode = (await db.FundNavs.AsNoTracking()
                .Where(n => codes.Contains(n.FundCode))
                .OrderBy(n => n.TradeDate)
                .ToListAsync(ct))
            .GroupBy(n => n.FundCode)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.TradeDate).ToList());
        var feeByCode = (await db.Funds.AsNoTracking()
                .Where(f => codes.Contains(f.Code))
                .Select(f => new { f.Code, f.BuyFeeRate })
                .ToListAsync(ct))
            .ToDictionary(x => x.Code, x => x.BuyFeeRate ?? 0m);

        foreach (var t in pendings)
        {
            if (!navsByCode.TryGetValue(t.FundCode, out var navs))
            {
                continue;
            }

            // >= 交易日的最近一个净值（T 日；兼容 QDII T+1）
            var nav = navs.FirstOrDefault(n => n.TradeDate >= t.TradeDate);
            if (nav is null)
            {
                continue;
            }

            var holding = await db.Holdings.FirstOrDefaultAsync(h => h.FundCode == t.FundCode, ct);
            if (holding is null)
            {
                holding = new Holding
                {
                    FundCode = t.FundCode,
                    Shares = 0m,
                    CostAmount = 0m,
                    OpenedAt = t.TradeDate,
                    UpdatedAt = now,
                };
                db.Holdings.Add(holding);
            }

            decimal confirmed;
            if (t.Side == TradeSide.Sell)
            {
                // 登记时已按预估份额预减，确认时回补差额；成本按登记时的单份成本同步回补
                confirmed = TradeMath.ConfirmSellShares(t.Amount, nav.UnitNav);
                var backfill = TradeMath.SellShareCorrection(t.Amount, nav.UnitNav, t.EstimatedShares);
                holding.Shares = Math.Max(0m, TradeMath.AddShares(holding.Shares, backfill));

                var unitCost = t.EstimatedShares > 0m ? (t.CostAmount ?? 0m) / t.EstimatedShares : 0m;
                if (holding.CostAmount is { } cost && unitCost > 0m)
                {
                    holding.CostAmount = Math.Max(0m, Math.Round(cost + unitCost * backfill, 4));
                }
                // 最终结转成本 = 单份成本 × 实际确认份额
                t.CostAmount = Math.Round(unitCost * confirmed, 4);
            }
            else
            {
                var feeRate = feeByCode.TryGetValue(t.FundCode, out var rate) ? rate : 0m;
                confirmed = TradeMath.ConfirmShares(t.Amount, nav.UnitNav, feeRate);
                var delta = TradeMath.ShareCorrection(t.Amount, nav.UnitNav, t.EstimatedShares, feeRate);
                holding.Shares = TradeMath.AddShares(holding.Shares, delta);
            }

            holding.UpdatedAt = now;

            t.ConfirmedShares = confirmed;
            t.ConfirmedNav = nav.UnitNav;
            t.Status = TradeStatus.Confirmed;
            t.ConfirmedAt = now;

            confirmedCount++;
            logger.LogInformation("{Side}确认 {Code} 金额={Amount} 净值={Nav} 份额 {Est}→{Real}",
                t.Side, t.FundCode, t.Amount, nav.UnitNav, t.EstimatedShares, confirmed);
        }

        if (confirmedCount > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return confirmedCount;
    }

    /// <summary>待确认交易流水（含方向与基金名，前端用于"待确认 ×N"角标）。</summary>
    public async Task<IReadOnlyList<PendingTradeInfo>> ListPendingAsync(CancellationToken ct)
    {
        return await db.Trades
            .AsNoTracking()
            .Where(t => t.Status == TradeStatus.Pending)
            .Include(t => t.Fund)
            .OrderBy(t => t.TradeDate).ThenBy(t => t.Id)
            .Select(t => new PendingTradeInfo(
                t.Id, t.Side, t.FundCode, t.Fund!.Name, t.TradeDate,
                t.Amount, t.EstimatedShares))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 撤销一笔待确认交易并置 Cancelled：
    /// 买入反恢预入账（份额 -= 预估份额、本金 -= 金额、当月预算已投 -= 金额）；
    /// 卖出原路加回份额与结转成本（不动预算）。仅允许撤销 Pending。
    /// </summary>
    public async Task CancelPendingAsync(long tradeId, CancellationToken ct)
    {
        var trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == tradeId, ct)
            ?? throw new InvalidOperationException("该交易流水不存在");

        if (trade.Status != TradeStatus.Pending)
        {
            throw new InvalidOperationException(
                trade.Status == TradeStatus.Confirmed
                    ? "该交易已按 T 日净值确认入账，不能撤销"
                    : "该交易已撤销，请勿重复操作");
        }

        var now = DateTimeOffset.UtcNow;
        var isSell = trade.Side == TradeSide.Sell;

        var holding = await db.Holdings.FirstOrDefaultAsync(h => h.FundCode == trade.FundCode, ct);
        if (holding is not null)
        {
            if (isSell)
            {
                // 卖出登记时预减了份额与成本，撤销时原路加回
                holding.Shares = TradeMath.AddShares(holding.Shares, trade.EstimatedShares);
                if (holding.CostAmount is { } cost)
                {
                    holding.CostAmount = Math.Round(cost + (trade.CostAmount ?? 0m), 4);
                }
            }
            else
            {
                holding.Shares = TradeMath.AddShares(holding.Shares, -trade.EstimatedShares);
                holding.CostAmount = holding.CostAmount is { } cost
                    ? Math.Round(Math.Max(0m, cost - trade.Amount), 4)
                    : null;
            }
            holding.UpdatedAt = now;
        }

        // 反恢当月预算"已投"（只有买入占用预算）
        if (!isSell)
        {
            var yearMonth = trade.TradeDate.ToString("yyyy-MM");
            var budget = await db.BudgetMonths.FirstOrDefaultAsync(b => b.YearMonth == yearMonth, ct);
            if (budget is not null)
            {
                budget.InvestedAmount = Math.Round(Math.Max(0m, budget.InvestedAmount - trade.Amount), 4);
            }
        }

        trade.Status = TradeStatus.Cancelled;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("撤销{Side} {Id} {Code} 金额={Amount}，预入账已反恢",
            isSell ? "卖出" : "买入", tradeId, trade.FundCode, trade.Amount);
    }
}

public record TradeResult(
    long Id, TradeSide Side, string FundCode, string FundName, DateOnly TradeDate, decimal Amount,
    decimal Shares, decimal? Nav, decimal EstimatedShares, bool Confirmed, string Summary, string Note);

public record PendingTradeInfo(
    long Id, TradeSide Side, string FundCode, string FundName, DateOnly TradeDate,
    decimal Amount, decimal EstimatedShares);