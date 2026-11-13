using Microsoft.Extensions.Options;

namespace FundDca.Api.Services;

/// <summary>
/// 「无人值守自退出」配置（appsettings 的 HostLifetime 节，或环境变量
/// HostLifetime__ExitWithoutHeartbeat 覆盖）。
/// 默认关闭：手工 dotnet run 调试时不会被误杀；只有启动入口显式开启。
/// </summary>
public sealed class HostLifetimeOptions
{
    public const string SectionName = "HostLifetime";

    /// <summary>收不到前端心跳时是否允许后端自动退出。默认 false。</summary>
    public bool ExitWithoutHeartbeat { get; set; }

    /// <summary>连续多少秒没有心跳就判定页面已关闭。默认 90 秒（心跳 30 秒一次）。</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 90;

    /// <summary>看门狗检查间隔秒数。默认 5 秒，即实际退出时刻最多比阈值晚 5 秒。</summary>
    public int CheckIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// 页面存活长连接（SSE）的推送间隔秒数。默认 10 秒。
    /// 必须显著小于 HeartbeatTimeoutSeconds：后台标签页的定时器会被浏览器节流到约 60 秒，
    /// 只有这条与可见性无关的网络连接能稳定证明「页面还开着」。
    /// </summary>
    public int WatchPingSeconds { get; set; } = 10;
}

/// <summary>
/// 记录浏览器页面最后一次「还活着」的信号。有两种来源，都只写这个内存时间戳，
/// 不查库、不访问外部行情：
///   ① 可见页面的 POST 心跳（30 秒一次）；
///   ② 页面存活长连接（SSE）每 10 秒一次的推送——后台标签页的定时器会被浏览器
///      节流到约 60 秒，靠定时器不足以保证 90 秒阈值，而长连接不受可见性影响。
/// 看门狗只读它判断「还有没有人在看页面」。
/// </summary>
public sealed class HostHeartbeatMonitor
{
    private long _lastBeatTicks = DateTime.UtcNow.Ticks;
    private long _beatCount;
    private long _streamCount;
    private long _activeStreams;

    /// <summary>最后一次心跳（UTC）。</summary>
    public DateTime LastBeatUtc => new(Interlocked.Read(ref _lastBeatTicks), DateTimeKind.Utc);

    /// <summary>距最后一次心跳已经过去多久。</summary>
    public TimeSpan IdleFor => DateTime.UtcNow - LastBeatUtc;

    /// <summary>累计收到的心跳次数，仅用于诊断接口确认「心跳确实在发」。</summary>
    public long BeatCount => Interlocked.Read(ref _beatCount);

    /// <summary>当前打开的页面存活长连接数（诊断用）。</summary>
    public long ActiveStreams => Interlocked.Read(ref _activeStreams);

    /// <summary>累计建立过多少条页面存活长连接（诊断用）。</summary>
    public long StreamCount => Interlocked.Read(ref _streamCount);

    public void Record()
    {
        Interlocked.Exchange(ref _lastBeatTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _beatCount);
    }

    public void StreamOpened()
    {
        Interlocked.Increment(ref _streamCount);
        Interlocked.Increment(ref _activeStreams);
    }

    public void StreamClosed()
    {
        Interlocked.Decrement(ref _activeStreams);
    }
}

/// <summary>
/// 无人值守看门狗：开启后若超过 HeartbeatTimeoutSeconds 没收到任何心跳，
/// 判定页面已关闭，调用 StopApplication 让主机走正常关闭流程
/// （释放 DI 容器、IHttpClientFactory 连接池等），而不是硬杀进程。
/// </summary>
public sealed class HostExitWatchdog(
    HostHeartbeatMonitor monitor,
    IOptions<HostLifetimeOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<HostExitWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.ExitWithoutHeartbeat)
        {
            logger.LogInformation("无人值守自退出未启用（HostLifetime:ExitWithoutHeartbeat=false）");
            return;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(10, o.HeartbeatTimeoutSeconds));
        var check = TimeSpan.FromSeconds(Math.Clamp(o.CheckIntervalSeconds, 1, 30));
        logger.LogInformation("无人值守自退出已启用：{Timeout} 秒无前端心跳即退出（检查间隔 {Check} 秒）",
            timeout.TotalSeconds, check.TotalSeconds);

        // 首次心跳前也按同一超时兜底：启动入口通常几秒内就会打开浏览器。
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(check, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var idle = monitor.IdleFor;
            if (idle < timeout)
            {
                continue;
            }

            logger.LogWarning(
                "已 {Idle:N0} 秒未收到前端心跳（阈值 {Timeout:N0} 秒），判定页面已关闭，开始优雅退出后端",
                idle.TotalSeconds, timeout.TotalSeconds);
            lifetime.StopApplication();
            return;
        }
    }
}
