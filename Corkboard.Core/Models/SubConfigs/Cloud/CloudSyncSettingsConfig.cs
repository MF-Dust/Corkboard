using CommunityToolkit.Mvvm.ComponentModel;

namespace Corkboard.Core.Models.SubConfigs.Cloud;

/// <summary>
///     云同步（思拓创联云存储）设置。
///     <para>
///         云同步**默认关闭**：每台设备各自决定，登录后不会自动打开；
///         这一整段也不会跟着同步走（见 <see cref="Services.Cloud.CloudSyncExclusions" />），
///         否则一台设备打开就会替所有设备打开、还没法单独关掉。
///     </para>
/// </summary>
public partial class CloudSyncSettingsConfig : ObservableObject
{
    /// <summary>是否开启云同步。</summary>
    [ObservableProperty] private bool _enabled;

    /// <summary>同步作业板内容（<c>data/board/</c>）。</summary>
    [ObservableProperty] private bool _syncBoard = true;

    /// <summary>同步设置（窗口尺寸位置、开机自启等本机专属项除外）。</summary>
    [ObservableProperty] private bool _syncSettings = true;

    /// <summary>本机没有未同步的改动时，自动拉取云端的更新。</summary>
    [ObservableProperty] private bool _autoPull = true;

    /// <summary>本机上一次成功同步的时间。</summary>
    [ObservableProperty] private DateTimeOffset? _lastSyncedAt;

    /// <summary>
    ///     上一次同步上去的设置内容摘要（排除本机专属项之后的 JSON 的 SHA-256）。
    ///     <para>
    ///         判断设置有没有改动只能靠内容比对：窗口位置这类**被排除**的项一移动就会写配置文件，
    ///         用文件写入时间当依据的话，界面永远显示「本机有改动」、再也不敢自动拉取云端备份。
    ///     </para>
    /// </summary>
    [ObservableProperty] private string? _lastSettingsHash;
}
