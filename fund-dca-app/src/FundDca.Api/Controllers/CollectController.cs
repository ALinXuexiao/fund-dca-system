using FundDca.Api.Models;
using FundDca.Api.Services;
using FundDca.Collect;
using Microsoft.AspNetCore.Mvc;

namespace FundDca.Api.Controllers;

[ApiController]
[Route("api/collect")]
public class CollectController(RefreshService refresh, ValuationRefreshService valuations, TradeService trades) : ControllerBase
{
    /// <summary>
    /// 立即拉取全部启用基金的最新净值（盘后正式净值）。
    /// 单只基金内部静默重试 5 次；仍失败的基金进入 failures，由前端弹窗提醒，不阻断其他基金。
    /// 净值到位后自动把待确认买入按 T 日净值校正入账。
    /// </summary>
    [HttpPost("refresh")]
    public async Task<RefreshReportDto> Refresh(CancellationToken ct)
    {
        var report = await refresh.RefreshAllAsync(ct);
        await trades.ConfirmPendingAsync(ct);
        return new RefreshReportDto
        {
            FinishedAt = report.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            SuccessCount = report.SuccessCount,
            FailedCount = report.FailedCount,
            Results = report.Results.Select(r => new FundRefreshResultDto
            {
                FundCode = r.FundCode,
                Success = r.Success,
                TradeDate = r.TradeDate?.ToString("yyyy-MM-dd"),
                NewRow = r.NewRow,
                Error = r.Error,
            }).ToList(),
        };
    }

    /// <summary>
    /// 立即拉取全部跟踪指数的最新估值（蛋卷基金，百分位口径）。
    /// 数据源未覆盖的指数自动回落到档案配置的代理指数；失败项进入 failures。
    /// </summary>
    [HttpPost("refresh-valuations")]
    public async Task<ValuationRefreshReportDto> RefreshValuations(CancellationToken ct)
    {
        var report = await valuations.RefreshAsync(ct);
        return new ValuationRefreshReportDto
        {
            FinishedAt = report.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            SuccessCount = report.SuccessCount,
            FailedCount = report.FailedCount,
            Results = report.Results.Select(r => new ValuationRefreshResultDto
            {
                IndexCode = r.IndexCode,
                IndexName = r.IndexName,
                Success = r.Success,
                TradeDate = r.TradeDate,
                ViaProxy = r.ViaProxy,
                ResolvedCode = r.ResolvedCode,
                PePercentile = r.PePercentile,
                PbPercentile = r.PbPercentile,
                Error = r.Error,
            }).ToList(),
        };
    }
}
