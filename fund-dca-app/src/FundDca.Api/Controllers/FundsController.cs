using System.Text.RegularExpressions;
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

    /// <summary>新增基金弹窗的下拉数据（赛道 + 已建档指数）。</summary>
    [HttpGet("options")]
    public async Task<FundOptions> Options(CancellationToken ct)
    {
        var sectors = await db.Sectors.AsNoTracking()
            .OrderBy(s => s.SortOrder)
            .Select(s => new SectorOption(s.Id, s.Name, s.IsEquity, s.SortOrder))
            .ToListAsync(ct);

        var indexRows = await db.Indexes.AsNoTracking()
            .OrderBy(i => i.Code)
            .ToListAsync(ct);
        var indexes = indexRows
            .Select(i => new IndexOption(i.Code, i.Name, i.Metric.ToString()))
            .ToList();

        return new FundOptions(sectors, indexes);
    }

    /// <summary>
    /// 手工新增基金档案。规则与导入建档一致：权益类必须选权益赛道；
    /// 货币类不传赛道时默认「货币」并强制手工维护市值；债券类默认「债券」。
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<FundListItem>> Create(
        [FromBody] CreateFundRequest body, CancellationToken ct)
    {
        var code = (body.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!Regex.IsMatch(code, "^[0-9A-Z]{4,12}$"))
        {
            return BadRequest(new { message = "基金代码需为 4–12 位数字/字母（如 012345、7R0001）" });
        }

        var name = (body.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 64)
        {
            return BadRequest(new { message = "基金名称必填，且不超过 64 字" });
        }

        if (!Enum.TryParse<FundType>(body.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type))
        {
            return BadRequest(new { message = "基金类型必须是 Stock/Mixed/Qdii/Bond/Money" });
        }

        if (await db.Funds.AnyAsync(f => f.Code == code, ct))
        {
            return Conflict(new { message = $"基金 {code} 已存在" });
        }

        var isEquity = type is FundType.Stock or FundType.Mixed or FundType.Qdii;
        Sector? sector;
        if (body.SectorId is { } sectorId)
        {
            sector = await db.Sectors.FirstOrDefaultAsync(s => s.Id == sectorId, ct);
            if (sector is null)
            {
                return BadRequest(new { message = "所选赛道不存在" });
            }
            if (isEquity && !sector.IsEquity)
            {
                return BadRequest(new { message = "权益型基金必须选择权益赛道" });
            }
        }
        else
        {
            var defaultSectorName = type switch
            {
                FundType.Money => "货币",
                FundType.Bond => "债券",
                _ => null,
            };
            if (defaultSectorName is null)
            {
                return BadRequest(new { message = "权益型基金必须选择赛道" });
            }
            sector = await db.Sectors.FirstOrDefaultAsync(s => s.Name == defaultSectorName, ct);
            if (sector is null)
            {
                return BadRequest(new { message = $"缺少「{defaultSectorName}」赛道，请先初始化数据库" });
            }
        }

        var indexCode = string.IsNullOrWhiteSpace(body.TrackedIndexCode)
            ? null
            : body.TrackedIndexCode.Trim().ToUpperInvariant();
        if (indexCode is not null && !await db.Indexes.AnyAsync(i => i.Code == indexCode, ct))
        {
            return BadRequest(new { message = $"跟踪指数 {indexCode} 尚未建档" });
        }

        if (body.BuyFeeRate is { } fee && (fee < 0m || fee > 1m))
        {
            return BadRequest(new { message = "申购费率需在 0–1 之间（0.0015 表示 0.15%）" });
        }

        var dividend = string.IsNullOrWhiteSpace(body.DividendMethod)
            ? "REINVEST"
            : body.DividendMethod.Trim().ToUpperInvariant();
        if (dividend is not ("REINVEST" or "CASH"))
        {
            return BadRequest(new { message = "分红方式只能是 REINVEST 或 CASH" });
        }

        db.Funds.Add(new Fund
        {
            Code = code,
            Name = name,
            Type = type,
            SectorId = sector.Id,
            TrackedIndexCode = indexCode,
            BuyFeeRate = body.BuyFeeRate,
            DividendMethod = dividend,
            UseManualValue = type == FundType.Money || body.UseManualValue == true,
            IsActive = true,
        });
        await db.SaveChangesAsync(ct);

        var created = await db.Funds.AsNoTracking()
            .Include(f => f.Sector)
            .Include(f => f.TrackedIndex)
            .FirstAsync(f => f.Code == code, ct);
        return CreatedAtAction(nameof(Get), new { code }, ToListItem(created));
    }

    /// <summary>
    /// 货币基金手工维护当前市值（不采集净值、不模拟收益）。
    /// 同时更新"最后更新日期"，超过 30 天前端给出提示。
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
