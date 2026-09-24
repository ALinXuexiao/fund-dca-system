using FundDca.Api.Dtos;
using FundDca.Api.Models;
using FundDca.Core.Domain;
using FundDca.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FundDca.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FundsController(FundDcaDbContext db) : ControllerBase
{
    /// <summary>全部基金档案（按赛道排序，前端分组展示）。</summary>
    [HttpGet]
    public async Task<IReadOnlyList<FundListItem>> List(CancellationToken ct)
    {
        var funds = await db.Funds
            .AsNoTracking()
            .Include(f => f.Sector)
            .Include(f => f.TrackedIndex)
            .OrderBy(f => f.Sector!.SortOrder).ThenBy(f => f.Code)
            .ToListAsync(ct);

        return funds.Select(ToListItem).ToList();
    }

    /// <summary>单只基金档案。</summary>
    [HttpGet("{code}")]
    public async Task<ActionResult<FundListItem>> Get(string code, CancellationToken ct)
    {
        var f = await db.Funds
            .AsNoTracking()
            .Include(x => x.Sector)
            .Include(x => x.TrackedIndex)
            .FirstOrDefaultAsync(x => x.Code == code, ct);

        return f is null ? NotFound(new { message = $"基金 {code} 不存在" }) : ToListItem(f);
    }

    /// <summary>
    /// 货币基金手工维护当前市值（不采集净值、不模拟收益）。
    /// 同时更新"最后更新日期"，超过 7 天前端给出提示。
    /// </summary>
    [HttpPut("{code}/manual-value")]
    public async Task<IActionResult> UpdateManualValue(
        string code, [FromBody] ManualValueUpdateDto body, CancellationToken ct)
    {
        if (body.MarketValue < 0)
        {
            return BadRequest(new { message = "市值不能为负" });
        }

        var fund = await db.Funds.Include(f => f.Sector)
            .FirstOrDefaultAsync(f => f.Code == code, ct);
        if (fund is null)
        {
            return NotFound(new { message = $"基金 {code} 不存在" });
        }
        if (!fund.UseManualValue && fund.Type != FundType.Money)
        {
            return BadRequest(new { message = "仅货币基金/手工市值标的支持手工维护市值" });
        }

        var valueDate = body.ValueDate is not null && DateOnly.TryParse(body.ValueDate, out var d)
            ? d
            : DateOnly.FromDateTime(DateTime.Now);

        var existing = await db.ManualValues.FirstOrDefaultAsync(m => m.FundCode == code, ct);
        if (existing is null)
        {
            db.ManualValues.Add(new ManualValue
            {
                FundCode = code,
                MarketValue = body.MarketValue,
                ValueDate = valueDate,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.MarketValue = body.MarketValue;
            existing.ValueDate = valueDate;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { code, marketValue = body.MarketValue, valueDate = valueDate.ToString("yyyy-MM-dd") });
    }

    private static FundListItem ToListItem(Fund f) => new(
        f.Code,
        f.Name,
        f.Type.ToString(),
        f.Sector?.Name,
        f.Sector?.IsEquity ?? false,
        f.Sector?.SortOrder ?? 999,
        f.TrackedIndexCode,
        f.TrackedIndex?.Name,
        f.TrackedIndex?.Metric.ToString() ?? "—",
        f.BuyFeeRate,
        f.DividendMethod,
        f.IsActive);
}
