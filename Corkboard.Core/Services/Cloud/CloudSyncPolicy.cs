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

    /// <summary>
    ///     要不要把云端的作业**并**到本机来（并集）：云端有备份，而本机自己也有没同步过的改动。
    ///     <para>
    ///         两边都动过的时候，谁覆盖谁都会丢东西：拉云端会冲掉用户刚写的作业，推本机又会冲掉
    ///         另一台设备刚布置的作业。所以这种时候取并集——本机的作业一条不动，云端多出来的补进来、
    ///         同一条取更新的那份。融合完再把结果传回去，两边就一样了。
    ///     </para>
    /// </summary>
    public static bool ShouldMerge(DateTimeOffset? remoteUpdatedAt, DateTimeOffset? lastSyncedAt,
        DateTimeOffset? localModifiedAt)
    {
        if (remoteUpdatedAt is null || localModifiedAt is null)
            return false;

        // 从没同步过（新设备、或者云端刚被别的设备清空过）：两边都有数据就直接融合。
        if (lastSyncedAt is null)
            return true;

        return remoteUpdatedAt.Value > lastSyncedAt.Value + Tolerance
               && localModifiedAt.Value > lastSyncedAt.Value + Tolerance;
    }

    /// <summary>
    ///     「只同步最近 N 天」的窗口起点；<paramref name="days" /> 为 0 或负数表示不限（返回 <c>null</c>）。
    /// </summary>
    /// <remarks>
    ///     按作业的创建时间算，是个滚动窗口：今天往前数 N×24 小时。
    ///     窗口只管作业的内容；科目表与手动排序在 <c>board.json</c> 里，永远跟着备份走。
    /// </remarks>
    public static DateTimeOffset? UploadCutoff(int days, DateTimeOffset now)
        => days <= 0 ? null : now.AddDays(-days);

    /// <summary>本机需不需要上传：本地数据比上次同步新（或者从来没同步过但本机有数据）。</summary>
    public static bool NeedsUpload(DateTimeOffset? localModifiedAt, DateTimeOffset? lastSyncedAt)
    {
        if (localModifiedAt is null)
            return false;

        return lastSyncedAt is null || localModifiedAt.Value > lastSyncedAt.Value + Tolerance;
    }

    /// <summary>
    ///     云端那份备份是不是在别处被清掉了：本机记着自己同步成功过，云端却一份备份都没有了。
    /// </summary>
    /// <remarks>
    ///     命中的时候本机要跟着把簿记清掉：否则界面还挂着一个早就不存在的「上次同步」时间，
    ///     而且本机会以为自己不用再传——用户改完作业也不会自动备份，等真要用的时候才发现云端是空的。
    /// </remarks>
    public static bool WasClearedRemotely(DateTimeOffset? lastSyncedAt, bool hasCloudBackup)
        => lastSyncedAt is not null && !hasCloudBackup;

    /// <summary>
    ///     防抖自动上传的截止时间：已经排着一个更早的截止时间就保持不动，只把后来的提前。
    ///     <para>
    ///         作业板改动等得短（一次编辑就落盘，等几秒没人再动就传）、设置改动等得长，
    ///         而作业落盘常常紧跟着一次配置落盘；谁后到就用谁的话，先排的短等待会被长等待顶掉，
    ///         用户改完作业就要多等几十秒。
    ///     </para>
    ///     <para>
    ///         过点了的截止时间也照样保留（返回它本身）：那次上传本来就要发生了，
    ///         真上传时看的是当时的改动，新改动会一起传上去，再往后推只会让用户白等。
    ///     </para>
    /// </summary>
    public static DateTimeOffset NextDeadline(DateTimeOffset? pendingDeadline, DateTimeOffset now, TimeSpan delay)
    {
        var candidate = now + delay;

        return pendingDeadline is { } pending && pending <= candidate ? pending : candidate;
    }
}
