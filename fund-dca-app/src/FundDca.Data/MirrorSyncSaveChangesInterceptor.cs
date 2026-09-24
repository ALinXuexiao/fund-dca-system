using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FundDca.Data;

/// <summary>
/// 主库写入成功后触发本地镜像同步（后台执行，不阻塞请求）。
/// 覆盖全部写路径：所有服务（交易、导入、采集、设置）最终都通过 SaveChanges 持久化。
/// </summary>
public sealed class MirrorSyncSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly LocalMirrorSynchronizer _synchronizer;

    public MirrorSyncSaveChangesInterceptor(LocalMirrorSynchronizer synchronizer) => _synchronizer = synchronizer;

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (result > 0)
        {
            _synchronizer.QueueSync();
        }
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (result > 0)
        {
            _synchronizer.QueueSync();
        }
        return ValueTask.FromResult(result);
    }
}
