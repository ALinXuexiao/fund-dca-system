using FundDca.Api.Models;
using FundDca.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FundDca.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController(DashboardService dashboard) : ControllerBase
{
    /// <summary>盘后看板：分母 D、今日盈亏、权益累计收益、逐行持仓占比与赛道聚合。</summary>
    [HttpGet]
    public async Task<DashboardDto> Get(CancellationToken ct) => await dashboard.BuildAsync(ct);
}
