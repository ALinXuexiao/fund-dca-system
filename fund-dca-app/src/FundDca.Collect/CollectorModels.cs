namespace FundDca.Collect;

/// <summary>外部数据源返回的一条最新净值。</summary>
public sealed record NavQuote(DateOnly TradeDate, decimal UnitNav, decimal? AccNav, decimal? DayChangePercent);

/// <summary>
/// 采集重试配置。PRD 口径：失败静默重试 5 次（退避约 5/15/40/90/180 秒），
/// 5 次后仍失败则交由前端弹窗 + Windows 通知。开发环境在 appsettings 中调短。
/// </summary>
public sealed class CollectorOptions
{
    public const string SectionName = "Collector";

    /// <summary>每次重试前的等待秒数；数组长度 = 重试次数（不含首次）</summary>
    public int[] RetryDelaysSeconds { get; set; } = [5, 15, 40, 90, 180];

    /// <summary>单次 HTTP 超时秒数</summary>
    public int TimeoutSeconds { get; set; } = 12;
}

/// <summary>单只基金采集结果。</summary>
public sealed record FundRefreshResult(
    string FundCode,
    bool Success,
    DateOnly? TradeDate,
    bool NewRow,
    string Source,
    string? Error);

/// <summary>整批刷新报告。</summary>
public sealed class RefreshReport(
    IReadOnlyList<FundRefreshResult> Results,
    DateTimeOffset FinishedAt)
{
    public IReadOnlyList<FundRefreshResult> Results { get; } = Results;
    public DateTimeOffset FinishedAt { get; } = FinishedAt;
    public int SuccessCount => Results.Count(r => r.Success);
    public int FailedCount => Results.Count(r => !r.Success);
    public List<FundRefreshResult> Failures => Results.Where(r => !r.Success).ToList();
}
