using System.Text;
using FundDca.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FundDca.Api.Controllers;

/// <summary>
/// 进程生命周期相关接口：页面心跳 + 页面存活长连接 + 诊断状态。
/// 都不查库、不访问外部行情，避免给数据源增加任何压力。
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

    /// <summary>
    /// 页面存活长连接（Server-Sent Events）：页面整个生命周期（含切到后台）保持一条连接，
    /// 服务端每 10 秒推一帧注释。浏览器的后台定时器节流只影响 setInterval，不影响这条连接，
    /// 因此「标签页在后台待多久」都不会被误判成关闭；连接断开（关标签/关浏览器）后
    /// 不再续命，由看门狗的 90 秒阈值回收进程。
    /// </summary>
    [HttpGet("host/watch")]
    public async Task Watch(CancellationToken ct)
    {
        var ping = TimeSpan.FromSeconds(Math.Max(3, options.Value.WatchPingSeconds));

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        monitor.StreamOpened();
        monitor.Record();
        try
        {
            await WriteFrame(": connected\n\n", ct);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ping, ct);
                await WriteFrame(": ping\n\n", ct);
                // 只有推送成功才证明页面这条连接还在，把「最后见到页面」推进到现在。
                // 后台标签页的 setInterval 会被节流，但这条连接不受影响，因此能一直续命。
                monitor.Record();
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开（关标签/关浏览器/刷新）：正常路径，不算错误。
        }
        catch (IOException)
        {
            // 连接已被对端重置。
        }
        finally
        {
            monitor.StreamClosed();
            // 断开这一刻页面才真正没人看，作为最后一次信号，之后交给 90 秒阈值判定。
            monitor.Record();
        }
    }

    /// <summary>心跳诊断：确认开关是否生效、信号是否在来、已静默多久。</summary>
    [HttpGet("host/state")]
    public IActionResult State()
    {
        var o = options.Value;
        return Ok(new
        {
            exitWithoutHeartbeat = o.ExitWithoutHeartbeat,
            timeoutSeconds = o.HeartbeatTimeoutSeconds,
            watchPingSeconds = o.WatchPingSeconds,
            beatCount = monitor.BeatCount,
            activeStreams = monitor.ActiveStreams,
            streamCount = monitor.StreamCount,
            idleSeconds = Math.Round(monitor.IdleFor.TotalSeconds, 1),
            lastBeatUtc = monitor.LastBeatUtc,
        });
    }

    private async Task WriteFrame(string frame, CancellationToken ct)
    {
        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), ct);
        await Response.Body.FlushAsync(ct);
    }
}
