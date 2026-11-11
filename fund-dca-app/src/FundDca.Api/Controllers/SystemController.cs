using FundDca.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FundDca.Api.Controllers;

/// <summary>
/// 进程生命周期相关接口：浏览器页面心跳 + 诊断状态。
/// 两者都不查库、不访问外部行情，避免给数据源增加任何压力。
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemController(
    HostHeartbeatMonitor monitor,
    IOptions<HostLifetimeOptions> options) : ControllerBase
{
    /// <summary>页面心跳：只更新时间戳。页面可见时每 30 秒调用一次。</summary>
    [HttpPost("host/heartbeat")]
    public IActionResult Heartbeat()
    {
        monitor.Record();
        return Ok(new { ok = true });
    }

    /// <summary>心跳诊断：确认开关是否生效、心跳是否在发、已静默多久。</summary>
    [HttpGet("host/state")]
    public IActionResult State()
    {
        var o = options.Value;
        return Ok(new
        {
            exitWithoutHeartbeat = o.ExitWithoutHeartbeat,
            timeoutSeconds = o.HeartbeatTimeoutSeconds,
            beatCount = monitor.BeatCount,
            idleSeconds = Math.Round(monitor.IdleFor.TotalSeconds, 1),
            lastBeatUtc = monitor.LastBeatUtc,
        });
    }
}
