using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FundDca.Collect;

/// <summary>
/// 带静默重试的净值采集器：首次失败后按配置退避重试 N 次（PRD：5 次）。
/// 过程不打扰用户；全部失败后向上抛出，由刷新编排记录失败项，前端弹窗提醒。
/// </summary>
public sealed class NavCollector(
    INavQuoteSource source,
    IOptions<CollectorOptions> options,
    ILogger<NavCollector> logger)
{
    public async Task<NavQuote?> FetchWithRetryAsync(string fundCode, CancellationToken ct)
    {
        var delays = options.Value.RetryDelaysSeconds;
        Exception? lastError = null;

        // 首次尝试 + delays.Length 次重试
        for (var attempt = 0; attempt <= delays.Length; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    logger.LogInformation("基金 {Code} 净值第 {Attempt} 次重试（等待 {Seconds}s）",
                        fundCode, attempt, delays[attempt - 1]);
                    await Task.Delay(TimeSpan.FromSeconds(delays[attempt - 1]), ct);
                }

                return await source.GetLatestAsync(fundCode, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning("基金 {Code} 净值采集失败（第 {Attempt} 次尝试）：{Error}",
                    fundCode, attempt + 1, ex.Message);
            }
        }

        throw new InvalidOperationException(
            $"基金 {fundCode} 净值在 {delays.Length + 1} 次尝试后仍失败：{lastError?.Message}", lastError);
    }
}
