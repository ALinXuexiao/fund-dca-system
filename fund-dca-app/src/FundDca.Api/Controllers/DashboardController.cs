using FundDca.Api.Models;
using FundDca.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FundDca.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController(DashboardService dashboard, IntradayService intraday) : ControllerBase
{
    /// <summary>盘后看板：分母 D、今日盈亏、权益累计收益、逐行持仓占比与赛道聚合。</summary>
    [HttpGet]
    public async Task<DashboardDto> Get(CancellationToken ct) => await dashboard.BuildAsync(ct);

    /// <summary>
    /// 盘中估算：跟踪指数实时涨跌幅 × 最新确认净值，得到估算净值/市值/今日盈亏。
    /// 不落库；指数行情后端缓存 30 秒，供前端 60 秒自动刷新。
    /// </summary>
    [HttpGet("intraday")]
    public async Task<IntradayDto> Intraday(CancellationToken ct) => await intraday.BuildAsync(ct);
}
