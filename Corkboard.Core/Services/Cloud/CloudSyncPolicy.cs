namespace Corkboard.Core.Services.Cloud;

/// <summary>
///     自动同步的判定策略（纯逻辑，有单测）。
///     <para>
///         核心原则：**本机有未同步的改动时绝不自动拉取**——那种情况该先把本机推上去，
///         或者由用户自己决定要不要用云端覆盖本机，不能悄悄把用户刚写的作业冲掉。
///     </para>
/// </summary>
public static class CloudSyncPolicy
{
    /// <summary>时间比较的容差：文件系统与服务器时钟不会完全一致。</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     能不能自动拉取云端备份。
    ///     条件：云端有备份、备份比本机上次同步新，且本机自上次同步以来没有改动过。
    /// </summary>
    public static bool ShouldAutoPull(DateTimeOffset? remoteUpdatedAt, DateTimeOffset? lastSyncedAt, DateTimeOffset? localModifiedAt)
    {
        if (remoteUpdatedAt is null)
            return false;

        if (lastSyncedAt is null)
            // 从没同步过：只有本机压根没有数据时才敢拉（新设备）
            return localModifiedAt is null;

        if (remoteUpdatedAt.Value <= lastSyncedAt.Value + Tolerance)
            return false;

        return localModifiedAt is null || localModifiedAt.Value <= lastSyncedAt.Value + Tolerance;
    }

    /// <summary>本机需不需要上传：本地数据比上次同步新（或者从来没同步过但本机有数据）。</summary>
    public static bool NeedsUpload(DateTimeOffset? localModifiedAt, DateTimeOffset? lastSyncedAt)
    {
        if (localModifiedAt is null)
            return false;

        return lastSyncedAt is null || localModifiedAt.Value > lastSyncedAt.Value + Tolerance;
    }
}
