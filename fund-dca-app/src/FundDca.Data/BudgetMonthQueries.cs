using FundDca.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace FundDca.Data;

/// <summary>
/// 当月预算资金池的解析。
/// 预算按月存储（year_month 为主键），跨月后当月即无记录；
/// 若此时按 0 建档，会把"0 预算"固化，并被后续月份逐月继承。
/// 故统一在此处理：当月无记录时，沿用最近一个月的预算额，已投清零。
/// </summary>
public static class BudgetMonthQueries
{
    /// <summary>
    /// 取当月预算。当月尚无记录时，按最近一个月的预算额新建（已投清零）并挂到上下文。
    /// 本方法<b>不提交</b>：写入路径（首笔买入、手工保存预算）会随各自的 SaveChanges 一并落库，
    /// 读路径（看板、决策）则不产生任何写入。
    /// 返回 null 表示库中从未设置过任何预算，由页面引导用户首次录入。
    /// </summary>
    public static async Task<BudgetMonth?> CurrentMonthAsync(
        this FundDcaDbContext db, CancellationToken ct = default)
    {
        var yearMonth = DateTime.Now.ToString("yyyy-MM");

        var current = await db.BudgetMonths.FirstOrDefaultAsync(b => b.YearMonth == yearMonth, ct);
        if (current is not null)
        {
            return current;
        }

        // 取"早于当月"的最近一个月：budget_months 每月一行、总量极小，
        // 由 SQL 按年月倒序取回后在内存中筛选，避免依赖 provider 对字符串比较的翻译。
        var rows = await db.BudgetMonths.AsNoTracking()
            .OrderByDescending(b => b.YearMonth)
            .Select(b => new { b.YearMonth, b.BudgetAmount })
            .ToListAsync(ct);

        var previousAmount = rows
            .Where(b => string.CompareOrdinal(b.YearMonth, yearMonth) < 0)
            .Select(b => (decimal?)b.BudgetAmount)
            .FirstOrDefault();

        // 从未设置过任何预算（全新库）：不凭空建档，交给页面引导首次录入
        if (previousAmount is null)
        {
            return null;
        }

        // 必须用实体新建：EF 会自动补齐所有列（含 bond_sweep_done 这类 NOT NULL 且无库级默认值的列）。
        // 手写 INSERT 只列部分列，会因漏列而违反非空约束。
        var created = new BudgetMonth
        {
            YearMonth = yearMonth,
            BudgetAmount = previousAmount.Value,
            InvestedAmount = 0m,
        };
        db.BudgetMonths.Add(created);
        return created;
    }
}
