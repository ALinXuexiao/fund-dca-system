using FundDca.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FundDca.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TradesController(TradeService trades) : ControllerBase
{
    /// <summary>登记一笔交易：正数买入，负数卖出；卖出费率 0。登记即预入账，T 日净值到位后自动校正。</summary>
    [HttpPost]
    public async Task<IActionResult> Record([FromBody] RecordTradeDto body, CancellationToken ct)
    {
        try
        {
            var tradeDate = body.TradeDate is not null && DateOnly.TryParse(body.TradeDate, out var d)
                ? d
                : (DateOnly?)null;

            var result = await trades.RecordTradeAsync(body.FundCode, body.Amount, tradeDate, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>待确认交易流水列表（前端"待确认 ×N"角标与明细）。</summary>
    [HttpGet("pending")]
    public async Task<IReadOnlyList<PendingTradeInfo>> Pending(CancellationToken ct) =>
        await trades.ListPendingAsync(ct);

    /// <summary>把待确认交易按 T 日净值校正入账（净值刷新后调用；通常自动）。</summary>
    [HttpPost("confirm-pending")]
    public async Task<IActionResult> ConfirmPending(CancellationToken ct)
    {
        var count = await trades.ConfirmPendingAsync(ct);
        return Ok(new { confirmed = count });
    }

    /// <summary>撤销一笔待确认交易：反恢预入账，置 Cancelled（误记时用）。</summary>
    [HttpPost("{id:long}/cancel")]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        try
        {
            await trades.CancelPendingAsync(id, ct);
            return Ok(new { cancelled = true, id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}

public record RecordTradeDto
{
    public required string FundCode { get; init; }
    /// <summary>正数买入、负数卖出（金额绝对值）。</summary>
    public decimal Amount { get; init; }
    /// <summary>交易日 T（缺省为今天）；string 便于前端直接传 yyyy-MM-dd。</summary>
    public string? TradeDate { get; init; }
}