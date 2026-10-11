using Corkboard.Core.Services.Cloud;

namespace Corkboard.Services.Cloud;

/// <summary>
///     云同步的门面：把作业板与设置备份到思拓创联云存储，并在安全的时候从云端恢复。
///     <para>
///         刻意**不做双向合并**——作业板是自由文本，自动合并两边只会分叉；宁可少同步，
///         也不能把用户刚写的内容悄悄冲掉。所以：改动后自动上传，本机没改动时才自动拉取，
///         本机有改动时只提示「云端有更新的备份」，由用户自己点恢复。
///     </para>
/// </summary>
public interface ICloudSyncService
{
    /// <summary>用量、云备份、忙闲或错误发生变化时触发（可能在后台线程上）。</summary>
    event EventHandler? StateChanged;

    /// <summary>最近一次查询到的账号云存储用量。</summary>
    CloudStorageUsage Usage { get; }

    /// <summary>按当前配置（同步作业板 / 同步设置）算出来的同步范围。</summary>
    CloudSyncScope CurrentScope { get; }

    /// <summary>云端有任意一份备份。</summary>
    bool HasCloudBackup { get; }

    /// <summary>云端有作业板备份。</summary>
    bool HasBoardBackup { get; }

    /// <summary>云端有设置备份。</summary>
    bool HasSettingsBackup { get; }

    /// <summary>云端有 Corkboard 自己传上去的备份文件（含中断的上传留下的孤儿分片）。</summary>
    bool HasCloudFiles { get; }

    /// <summary>云端备份的最近更新时间。</summary>
    DateTimeOffset? RemoteUpdatedAt { get; }

    /// <summary>云端备份比本机上次同步新（此时才提示用户可以从云端恢复）。</summary>
    bool RemoteIsNewer { get; }

    /// <summary>正在上传或下载。</summary>
    bool IsBusy { get; }

    /// <summary>后台同步失败时的说明；用户主动操作报的错由调用方自己处理。</summary>
    string? LastError { get; }

    /// <summary>启动时调用一次：已开启同步且已登录时查一遍云端，必要时自动拉取。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>查一遍云端用量与备份（不自动拉取），供设置页刷新。</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>把范围内的本机数据备份到云端。</summary>
    Task BackupAsync(CloudSyncScope scope, CancellationToken cancellationToken = default);

    /// <summary>用云端备份覆盖本机数据（调用方负责先让用户确认）。</summary>
    Task RestoreAsync(CloudSyncScope scope, CancellationToken cancellationToken = default);

    /// <summary>把 Corkboard 在云端的备份全部删掉（调用方负责先让用户确认；本机数据不动）。</summary>
    Task ClearCloudAsync(CancellationToken cancellationToken = default);
}
