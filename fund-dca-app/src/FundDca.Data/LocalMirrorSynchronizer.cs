using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FundDca.Data;

/// <summary>
/// 本地镜像同步器：主库（Neon 云库）为唯一事实来源，每次写操作成功后，
/// 把全部业务表整表复制到本机 PostgreSQL 镜像库（个人持仓数据量小，整表同步最简单可靠，天然自愈）。
/// 本机没有本地库（未装 PostgreSQL / 库不存在 / 连接失败）时自动跳过，绝不影响主流程；
/// 主库与镜像指向同一数据库时（尚未上云的开发机）自动禁用。
/// </summary>
public sealed class LocalMirrorSynchronizer
{
    /// <summary>镜像连接探测超时（秒）：本机没有 PostgreSQL 时快速失败。</summary>
    private const int ProbeTimeoutSeconds = 3;

    /// <summary>同步失败后的重试间隔：期间跳过触发，避免本地库未启动时反复尝试。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(10);

    private readonly string _primaryConnString;
    private readonly string _mirrorConnString;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _triggerLock = new();
    private bool _running;
    private bool _pending;
    private DateTimeOffset? _lastFailureUtc;

    /// <summary>是否启用镜像同步（已配置镜像串且与主库不同）。</summary>
    public bool IsConfigured { get; }

    public LocalMirrorSynchronizer(string primaryConnString, string? mirrorConnString, ILogger? logger = null)
    {
        _primaryConnString = primaryConnString;
        _logger = logger;

        if (mirrorConnString is null ||
            string.Equals(Fingerprint(primaryConnString), Fingerprint(mirrorConnString), StringComparison.OrdinalIgnoreCase))
        {
            IsConfigured = false;
            _mirrorConnString = "";
            return;
        }

        IsConfigured = true;
        // 强制短超时：本机镜像库不可达时 3 秒内放弃
        var mirrorBuilder = new NpgsqlConnectionStringBuilder(mirrorConnString) { Timeout = ProbeTimeoutSeconds };
        _mirrorConnString = mirrorBuilder.ConnectionString;
    }

    /// <summary>
    /// 写操作后调用：后台触发一次同步。并发触发自动合并；同步进行中产生的写操作会追加一轮。
    /// 永不阻塞请求线程，永不抛异常。
    /// </summary>
    public void QueueSync()
    {
        if (!IsConfigured)
        {
            return;
        }
        if (_lastFailureUtc is { } failure && DateTimeOffset.UtcNow - failure < RetryInterval)
        {
            return;
        }

        lock (_triggerLock)
        {
            if (_running)
            {
                _pending = true; // 正在同步，结束时追加一轮补上本次变更
                return;
            }
            _running = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    lock (_triggerLock)
                    {
                        _pending = false;
                    }
                    await RunSyncOnceAsync();
                    _lastFailureUtc = null;
                }
                while (Volatile.Read(ref _pending));
            }
            catch (Exception ex)
            {
                _lastFailureUtc = DateTimeOffset.UtcNow;
                _logger?.LogWarning(ex,
                    "本地镜像同步已暂停（本机未安装 PostgreSQL 或镜像库不可达），{Minutes} 分钟后自动重试",
                    RetryInterval.TotalMinutes);
            }
            finally
            {
                lock (_triggerLock)
                {
                    _running = false;
                }
            }
        });
    }

    /// <summary>启动时调用：同步等待完成一次镜像同步，便于日志确认；失败仅告警，不阻断启动。</summary>
    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return;
        }
        try
        {
            await _mutex.WaitAsync(ct);
            try
            {
                await SyncCoreAsync(ct);
                _lastFailureUtc = null;
            }
            finally
            {
                _mutex.Release();
            }
        }
        catch (Exception ex)
        {
            _lastFailureUtc = DateTimeOffset.UtcNow;
            _logger?.LogWarning(ex,
                "启动镜像同步失败（本机未安装 PostgreSQL 或镜像库不可达时属正常现象，主库不受影响）");
        }
    }

    private async Task RunSyncOnceAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            await SyncCoreAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task SyncCoreAsync(CancellationToken ct = default)
    {
        // 1) 探测：镜像库必须已存在（不自动创建——本机没有 fund_dca 库即视为"无本地库"，直接跳过）
        await using (var probe = new NpgsqlConnection(_mirrorConnString))
        {
            await probe.OpenAsync(ct);
        }

        await using var primary = CreateContext(_primaryConnString);
        await using var mirror = CreateContext(_mirrorConnString);

        // 2) 镜像结构对齐（幂等：与主库同一套 EF 迁移）
        await mirror.Database.MigrateAsync(ct);

        // 3) 主库读取全部业务表
        var sectors = await primary.Sectors.AsNoTracking().ToListAsync(ct);
        var indexes = await primary.Indexes.AsNoTracking().ToListAsync(ct);
        var funds = await primary.Funds.AsNoTracking().ToListAsync(ct);
        var overlapGroups = await primary.OverlapGroups.AsNoTracking().ToListAsync(ct);
        var fundOverlaps = await primary.FundOverlaps.AsNoTracking().ToListAsync(ct);
        var holdings = await primary.Holdings.AsNoTracking().ToListAsync(ct);
        var manualValues = await primary.ManualValues.AsNoTracking().ToListAsync(ct);
        var fundNavs = await primary.FundNavs.AsNoTracking().ToListAsync(ct);
        var dailySnapshots = await primary.DailySnapshots.AsNoTracking().ToListAsync(ct);
        var budgetMonths = await primary.BudgetMonths.AsNoTracking().ToListAsync(ct);
        var indexValuations = await primary.IndexValuations.AsNoTracking().ToListAsync(ct);
        var dcaSettings = await primary.DcaSettings.AsNoTracking().ToListAsync(ct);
        var trades = await primary.Trades.AsNoTracking().ToListAsync(ct);
        var positionVersions = await primary.PositionVersions.AsNoTracking().ToListAsync(ct);
        var positionVersionItems = await primary.PositionVersionItems.AsNoTracking().ToListAsync(ct);

        // 4) 事务内整表替换（CASCADE 清空顺序无关；回放按外键依赖顺序）
        await using var tx = await mirror.Database.BeginTransactionAsync(ct);
        await mirror.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE sectors, indexes, funds, overlap_groups, fund_overlaps, holdings,
                       manual_values, fund_navs, daily_snapshots, budget_months, index_valuations,
                       dca_settings, trades, position_versions, position_version_items
            RESTART IDENTITY CASCADE
            """, ct);

        mirror.Sectors.AddRange(sectors);
        mirror.Indexes.AddRange(indexes);
        mirror.Funds.AddRange(funds);
        mirror.OverlapGroups.AddRange(overlapGroups);
        mirror.FundOverlaps.AddRange(fundOverlaps);
        mirror.Holdings.AddRange(holdings);
        mirror.ManualValues.AddRange(manualValues);
        mirror.FundNavs.AddRange(fundNavs);
        mirror.DailySnapshots.AddRange(dailySnapshots);
        mirror.BudgetMonths.AddRange(budgetMonths);
        mirror.IndexValuations.AddRange(indexValuations);
        mirror.DcaSettings.AddRange(dcaSettings);
        mirror.Trades.AddRange(trades);
        mirror.PositionVersions.AddRange(positionVersions);
        mirror.PositionVersionItems.AddRange(positionVersionItems);
        await mirror.SaveChangesAsync(ct);

        // 5) 对齐自增序列，保证镜像库可独立使用（psql 手工插入不冲突）
        // 表名/列名均为编译期常量，无注入风险（EF1003 属误报，局部抑制）
#pragma warning disable EF1003
        foreach (var (table, column) in new[]
                 {
                     ("sectors", "id"), ("overlap_groups", "id"), ("dca_settings", "id"),
                     ("trades", "id"), ("position_versions", "id"), ("position_version_items", "id"),
                 })
        {
            await mirror.Database.ExecuteSqlRawAsync(
                $"SELECT setval(pg_get_serial_sequence('{table}', '{column}'), " +
                $"COALESCE((SELECT MAX({column}) FROM {table}), 0) + 1, false)", ct);
        }
#pragma warning restore EF1003

        await tx.CommitAsync(ct);

        _logger?.LogInformation(
            "本地镜像已同步：基金 {Funds} 只，持仓 {Holdings} 条，净值 {Navs} 条，估值 {Valuations} 条，交易 {Trades} 笔",
            funds.Count, holdings.Count, fundNavs.Count, indexValuations.Count, trades.Count);
    }

    private static FundDcaDbContext CreateContext(string connString) =>
        new(new DbContextOptionsBuilder<FundDcaDbContext>()
            .UseNpgsql(connString)
            .Options);

    /// <summary>连接指纹（主机:端口:库:用户）：主库与镜像相同时自动禁用镜像。</summary>
    private static string Fingerprint(string connString)
    {
        var b = new NpgsqlConnectionStringBuilder(connString);
        return $"{b.Host}:{b.Port}:{b.Database}:{b.Username}".ToLowerInvariant();
    }
}
