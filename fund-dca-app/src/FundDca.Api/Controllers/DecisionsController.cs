using FundDca.Api.Models;
using FundDca.Api.Services;
using FundDca.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FundDca.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DecisionsController(DecisionService decisions, FundDcaDbContext db) : ControllerBase
{
    /// <summary>今日定投决策：红绿灯信号、四条件判定、固定额建议。</summary>
    [HttpGet("today")]
    public async Task<DecisionDto> Today(CancellationToken ct) =>
        await decisions.BuildTodayAsync(ct);

    /// <summary>定投设置：单次定投固定金额。</summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var s = await db.DcaSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        return Ok(new { fixedInvestAmount = s?.FixedInvestAmount ?? 50m });
    }

    /// <summary>更新单次定投固定金额（如 50 元 → 涨工资后改为 100 元）。</summary>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] SettingUpdateDto body, CancellationToken ct)
    {
        if (body.FixedInvestAmount <= 0m || body.FixedInvestAmount > 100000m)
        {
            return BadRequest(new { message = "单次定投金额必须在 0 ~ 100000 元之间" });
        }

        var s = await decisions.UpdateFixedAmountAsync(Math.Round(body.FixedInvestAmount, 2), ct);
        return Ok(new { fixedInvestAmount = s.FixedInvestAmount });
    }

    /// <summary>更新当月预算（预算额/已投额），决策立即按新剩余预算重算。</summary>
    [HttpPut("budget")]
    public async Task<IActionResult> UpdateBudget([FromBody] BudgetUpdateDto body, CancellationToken ct)
    {
        if (body.BudgetAmount < 0 || body.InvestedAmount < 0 || body.InvestedAmount > body.BudgetAmount)
        {
            return BadRequest(new { message = "预算额不能为负，且已投额不能超过预算额" });
        }

        var yearMonth = DateTime.Now.ToString("yyyy-MM");
        var budget = await db.BudgetMonths.FirstOrDefaultAsync(b => b.YearMonth == yearMonth, ct);
        if (budget is null)
        {
            budget = new Core.Domain.BudgetMonth { YearMonth = yearMonth };
            db.BudgetMonths.Add(budget);
        }
        budget.BudgetAmount = body.BudgetAmount;
        budget.InvestedAmount = body.InvestedAmount;
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            yearMonth,
            budgetAmount = budget.BudgetAmount,
            investedAmount = budget.InvestedAmount,
            remainingAmount = budget.BudgetAmount - budget.InvestedAmount,
        });
    }
}
