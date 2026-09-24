using FundDca.Core.Domain;
using FundDca.Core.Rules;
using FundDca.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FundDca.Api.Services;

/// <summary>
/// 买入流水：登记即预入账，T 日收盘净值到位后校正份额。
/// 规则（PRD 3.x，用户确认）：
///  1) 登记买入时立即预入账 —— 累加该基金累计本金、扣减当月预算"已投"；
///     份额先按"最近可用净值"预占（今晚净值未到，先记 EstimatedShares）。
///  2) T 日（买入日）收盘净值盘后到位后，按 T 日净值折算真实份额并校正：
///     持有份额 += (ConfirmedShares - EstimatedShares)，流水转 Confirmed。
///  3) T+0/T+1 净值日期统一按">= 买入日的最近一个净值日"确认，兼容 QDII T+1。
///  货币基金/手工市值标的不走净值换算，买入按钮不出现（此处防御性拒绝）。
/// </summary>
public class TradeService(FundDcaDbContext db, ILogger<TradeService> logger)
{
    /// <summary>
    /// 登记一笔买入。返回登记结果：status 为 Confirmed=立即按已有 T 日净值确认；Pending=等今晚净值。
    /// </summary>
    public async Task<TradeResult> RecordBuyAsync(
        string fundCode, decimal amount, DateOnly? tradeDate, CancellationToken ct)
    {
        if (amount <= 0m)
        {
            throw new InvalidOperationException("买入金额必须大于 0");
        }

        var fund = await db.Funds
            .AsNoTracking()
            .Include(f => f.Sector)
            .FirstOrDefaultAsync(f => f.Code == fundCode, ct)
            ?? throw new InvalidOperationException($"基金 {fundCode} 不存在");

        if (fund.UseManualValue || fund.Type == FundType.Money)
        {
            throw new InvalidOperationException("货币基金/手工市值标的不支持按净值买入，请直接维护市值");
        }

        var buyDate = tradeDate ?? DateOnly.FromDateTime(DateTime.Now);

        // 最近可用净值（用于预占份额）
        var lastNav = await db.FundNavs
            .AsNoTracking()
            .Where(n => n.FundCode == fundCode)
            .OrderByDescending(n => n.TradeDate)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("该基金暂无净值数据，请先刷新净值再买入");

        return await RecordAsync(fund, amount, buyDate, lastNav, ct);
    }

    /// <summary>校验并落库一笔买入（登记即预入账）。</summary>
    private async Task<TradeResult> RecordAsync(
        Fund fund, decimal amount, DateOnly buyDate, FundNav lastNav, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // 已有的 T 日（或最近已于盘中落库的净值日）净值 → 立即确认
        var confirmNav = await db.FundNavs
            .AsNoTracking()
            .Where(n => n.FundCode == fund.Code && n.TradeDate >= buyDate)
            .OrderBy(n => n.TradeDate)
            .FirstOrDefaultAsync(ct);

        var estimatedShares = TradeMath.EstimateShares(amount, lastNav.UnitNav);
        decimal? confirmedShares = null;
        decimal? confirmedNav = null;
        TradeStatus status;

        if (confirmNav is not null)
        {
            confirmedNav = confirmNav.UnitNav;
            confirmedShares = TradeMath.ConfirmShares(amount, confirmNav.UnitNav);
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

        // 当月预算"已投"累加
        var yearMonth = DateTime.Now.ToString("yyyy-MM");
        var budget = await db.BudgetMonths.FirstOrDefaultAsync(b => b.YearMonth == yearMonth, ct);
        if (budget is null)
        {
            budget = new BudgetMonth { YearMonth = yearMonth, BudgetAmount = 0m };
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
            trade.Id, fund.Code, fund.Name, buyDate, amount,
            confirmedShares ?? estimatedShares, confirmedNav, trade.EstimatedShares,
            status == TradeStatus.Confirmed, "按最新净值预约入账，T 日收盘净值到位后自动校正",
            status == TradeStatus.Confirmed ? "当日净值已到位，按 T 日净值确认" : "当日净值尚未到位，今晚自动确认");
    }

    /// <summary>
    /// 将待确认买入按 T 日净值校正：持有份额 += (真实份额 - 预估份额)，流水转 Confirmed。
    /// 应在净值刷新完成后调用（/api/collect/refresh 成功之后）。
    /// </summary>
    public async Task<int> ConfirmPendingAsync(CancellationToken ct)
    {
        var pendings = await db.Trades
            .Where(t => t.Status == TradeStatus.Pending)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;

        var confirmedCount = 0;

        // 分组取各基金全部净值（避免逐笔查询）
        var codes = pendings.Select(t => t.FundCode).Distinct().ToList();
        var navsByCode = (await db.FundNavs.AsNoTracking()
                .Where(n => codes.Contains(n.FundCode))
                .OrderBy(n => n.TradeDate)
                .ToListAsync(ct))
            .GroupBy(n => n.FundCode)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.TradeDate).ToList());

        foreach (var t in pendings)
        {
            if (!navsByCode.TryGetValue(t.FundCode, out var navs))
            {
                continue;
            }

            // >= 买入日的最近一个净值（T 日；兼容 QDII T+1）
            var nav = navs.FirstOrDefault(n => n.TradeDate >= t.TradeDate);
            if (nav is null)
            {
                continue;
            }

            var confirmed = TradeMath.ConfirmShares(t.Amount, nav.UnitNav);
            var delta = TradeMath.ShareCorrection(t.Amount, nav.UnitNav, t.EstimatedShares);

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
            holding.Shares = TradeMath.AddShares(holding.Shares, delta);
            holding.UpdatedAt = now;

            t.ConfirmedShares = confirmed;
            t.ConfirmedNav = nav.UnitNav;
            t.Status = TradeStatus.Confirmed;
            t.ConfirmedAt = now;

            confirmedCount++;
            logger.LogInformation("买入确认 {Code} 金额={Amount} 净值={Nav} 份额 {Est}→{Real}",
                t.FundCode, t.Amount, nav.UnitNav, t.EstimatedShares, confirmed);
        }

        if (confirmedCount > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return confirmedCount;
    }

    /// <summary>待确认买入流水（含基金名，前端用于"待确认 ×N"角标）。</summary>
    public async Task<IReadOnlyList<PendingTradeInfo>> ListPendingAsync(CancellationToken ct)
    {
        return await db.Trades
            .AsNoTracking()
            .Where(t => t.Status == TradeStatus.Pending)
            .Include(t => t.Fund)
            .OrderBy(t => t.TradeDate).ThenBy(t => t.Id)
            .Select(t => new PendingTradeInfo(
                t.Id, t.FundCode, t.Fund!.Name, t.TradeDate,
                t.Amount, t.EstimatedShares))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 撤销一笔待确认买入：反恢预入账（持有份额 -= 预估份额、本金 -= 金额、当月预算已投 -= 金额）并置 Cancelled。
    /// 仅允许撤销 Pending；已确认的买入不可撤销（走卖出闭环）。
    /// </summary>
    public async Task CancelPendingAsync(long tradeId, CancellationToken ct)
    {
        var trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == tradeId, ct)
            ?? throw new InvalidOperationException("该买入流水不存在");

        if (trade.Status != TradeStatus.Pending)
        {
            throw new InvalidOperationException(
                trade.Status == TradeStatus.Confirmed
                    ? "该买入已按 T 日净值确认入账，请在卖出闭环中处理"
                    : "该买入已撤销，请勿重复操作");
        }

        var now = DateTimeOffset.UtcNow;

        // 反恢持仓（仅累加预估份额的预占部分；本金、预算全额反恢）
        var holding = await db.Holdings.FirstOrDefaultAsync(h => h.FundCode == trade.FundCode, ct);
        if (holding is not null)
        {
            holding.Shares = TradeMath.AddShares(holding.Shares, -trade.EstimatedShares);
            holding.CostAmount = holding.CostAmount is { } c
                ? Math.Round(Math.Max(0m, c - trade.Amount), 4)
                : null;
            holding.UpdatedAt = now;
        }

        // 反恢当月预算"已投"
        var yearMonth = trade.TradeDate.ToString("yyyy-MM");
        var budget = await db.BudgetMonths.FirstOrDefaultAsync(b => b.YearMonth == yearMonth, ct);
        if (budget is not null)
        {
            budget.InvestedAmount = Math.Round(Math.Max(0m, budget.InvestedAmount - trade.Amount), 4);
        }

        trade.Status = TradeStatus.Cancelled;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("撤销买入 {Id} {Code} 金额={Amount}，预入账已反恢", tradeId, trade.FundCode, trade.Amount);
    }
}

public record TradeResult(
    long Id, string FundCode, string FundName, DateOnly TradeDate, decimal Amount,
    decimal Shares, decimal? Nav, decimal EstimatedShares, bool Confirmed, string Summary, string Note);

public record PendingTradeInfo(
    long Id, string FundCode, string FundName, DateOnly TradeDate,
    decimal Amount, decimal EstimatedShares);