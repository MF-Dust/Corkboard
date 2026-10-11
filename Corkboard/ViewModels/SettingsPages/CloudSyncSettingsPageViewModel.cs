using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Corkboard.Core.Models.SubConfigs.Cloud;
using Corkboard.Core.Services.Cloud;
using Corkboard.Core.Services.Config;
using Corkboard.Services.Auth;
using Corkboard.Services.Cloud;
using Microsoft.Extensions.Logging;
using CR = Corkboard.Core.Langs.Common.Resources;

namespace Corkboard.ViewModels.SettingsPages;

/// <summary>
///     云同步设置页：把作业板与设置备份到思拓创联云存储，需要时再从云端恢复。
/// </summary>
/// <remarks>
///     页面只做两件事——把 <see cref="ICloudSyncService" /> 的状态画出来、把用户的动作转给它。
///     什么时候该上传、什么时候敢自动拉取、被排除同步的本机设置怎么保住，全在服务里，
///     免得页面自己判一遍、服务再判一遍，两边不一致。
///     <para>
///         两个依赖都是单例（同步服务、账号服务），页面与 ViewModel 都是 transient，
///         所以离开页面时必须在 <see cref="Detach" /> 里退订。
///     </para>
/// </remarks>
public sealed partial class CloudSyncSettingsPageViewModel : ViewModelBase
{
    private readonly ICloudSyncService _cloud;
    private readonly ISectlAuthService _auth;
    private readonly ILogger<CloudSyncSettingsPageViewModel>? _logger;

    /// <param name="configHandler">配置句柄，交给 <see cref="ViewModelBase" /> 持有。</param>
    /// <param name="cloud">云同步服务（单例）。</param>
    /// <param name="auth">账号服务（单例）：没登录就不让开云同步。</param>
    /// <param name="logger">日志，可选（测试与设计器场景可能没有）。</param>
    public CloudSyncSettingsPageViewModel(MainConfigHandler configHandler,
        ICloudSyncService cloud,
        ISectlAuthService auth,
        ILogger<CloudSyncSettingsPageViewModel>? logger = null) : base(configHandler)
    {
        _cloud = cloud;
        _auth = auth;
        _logger = logger;

        SyncWindowOptions = BuildSyncWindowOptions();
        Apply();
        _cloud.StateChanged += OnCloudStateChanged;
        _auth.StateChanged += OnAuthStateChanged;
    }

    /// <summary>云同步这一块的配置（开关、同步内容、上次同步时间），界面直接改它、改完自动落盘。</summary>
    public CloudSyncSettingsConfig Cloud => Config.Cloud;

    /// <summary>「只同步最近多久的作业」的下拉项：<c>0</c> 表示全部传。</summary>
    public IReadOnlyList<SyncWindowOption> SyncWindowOptions { get; }

    /// <summary>
    ///     预设四档（全部/30 天/7 天/1 天）。配置里若是别的值（比如手工改过配置文件），也把它按天数插进去，
    ///     免得下拉显示的和服务实际用的不是一回事。
    /// </summary>
    private IReadOnlyList<SyncWindowOption> BuildSyncWindowOptions()
    {
        List<SyncWindowOption> options =
        [
            new(0, CR.Settings_Cloud_SyncDays_All),
            new(30, string.Format(CR.Settings_Cloud_SyncDays_Days, 30)),
            new(7, string.Format(CR.Settings_Cloud_SyncDays_Days, 7)),
            new(1, string.Format(CR.Settings_Cloud_SyncDays_Days, 1))
        ];
        if (Cloud.SyncDays > 0 && options.TrueForAll(option => option.Days != Cloud.SyncDays))
        {
            var extra = new SyncWindowOption(Cloud.SyncDays,
                string.Format(CR.Settings_Cloud_SyncDays_Days, Cloud.SyncDays));
            var index = options.FindIndex(option => option.Days < Cloud.SyncDays);
            if (index < 0) options.Add(extra);
            else options.Insert(index, extra);
        }

        return options;
    }

    /// <summary>下拉选中项：写回配置里的天数，配置换了对象（云端恢复过设置）也照样重取。</summary>
    public SyncWindowOption SelectedSyncWindow
    {
        get => SyncWindowOptions.FirstOrDefault(option => option.Days == Cloud.SyncDays)
               ?? SyncWindowOptions[0];
        set
        {
            if (value is not null)
                Cloud.SyncDays = value.Days;
        }
    }

    /// <summary>本机是否已登录思拓创联账号。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSync))]
    private bool _isSignedIn;

    /// <summary>正在上传或下载。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSync))]
    [NotifyPropertyChangedFor(nameof(CanRestore))]
    private bool _isBusy;

    /// <summary>云端有备份可恢复。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestore))]
    [NotifyPropertyChangedFor(nameof(ShowRemoteNewer))]
    private bool _hasCloudBackup;

    /// <summary>云端备份比本机上次同步新。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemoteNewer))]
    private bool _remoteIsNewer;

    /// <summary>云端有本机传上去的备份文件（连中断留下的孤儿分片算），「清空云端」按这个使能。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClearCloud))]
    private bool _hasCloudFiles;

    /// <summary>云端那份备份在别处被清掉了，提示用户重新备份（本机数据没事）。</summary>
    [ObservableProperty] private bool _remoteCleared;

    /// <summary>查到用量了才显示「云端空间」那一行。</summary>
    [ObservableProperty] private bool _hasUsage;

    /// <summary>账号云空间用量，形如「已用 1.5 MB，共 2 GB」。</summary>
    [ObservableProperty] private string _cloudUsageText = string.Empty;

    /// <summary>账号云空间剩余，形如「剩余 1.9 GB」。</summary>
    [ObservableProperty] private string _cloudAvailableText = string.Empty;

    /// <summary>本平台（Corkboard）在云端的占用。</summary>
    [ObservableProperty] private string _platformUsageText = string.Empty;

    /// <summary>账号云空间已用百分比，喂给进度条。</summary>
    [ObservableProperty] private double _usagePercent;

    /// <summary>上次同步时间，从没同步过时是「还没同步过」。</summary>
    [ObservableProperty] private string _lastSyncedText = string.Empty;

    /// <summary>失败原因；为空表示当前没有要提示的错误。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>已登录且不在忙：备份与开关都按这个使能。</summary>
    public bool CanSync => IsSignedIn && !IsBusy;

    /// <summary>云端确实有备份才让点恢复。</summary>
    public bool CanRestore => HasCloudBackup && !IsBusy;

    /// <summary>云端确实有东西可清才让点清空（没东西可删的按钮点不动，别让用户白确认一次）。</summary>
    public bool CanClearCloud => HasCloudFiles && !IsBusy;

    /// <summary>云端有更新的备份时才提示用户去恢复。</summary>
    public bool ShowRemoteNewer => RemoteIsNewer && HasCloudBackup;

    /// <summary>有错误才显示错误那一行。</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    ///     设置版本恢复过了：页面据此提示重启（配置对象已经被换掉，不重启就别扭）。
    /// </summary>
    public bool SettingsRestored { get; private set; }

    /// <summary>立即把范围内的本机数据备份到云端。</summary>
    [RelayCommand]
    private async Task BackupNowAsync()
    {
        await RunAsync(() => _cloud.BackupAsync(_cloud.CurrentScope));
    }

    /// <summary>
    ///     用云端备份覆盖本机数据。
    /// </summary>
    /// <remarks>调用方（页面）负责先弹确认框：这一步会丢掉还没传上去的改动。</remarks>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        var scope = _cloud.CurrentScope;
        await RunAsync(() => _cloud.RestoreAsync(scope));
        SettingsRestored = scope.HasFlag(CloudSyncScope.Settings);
    }

    /// <summary>重新查一遍用量与云端备份。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(() => _cloud.RefreshAsync());
    }

    /// <summary>
    ///     把云端的备份全删掉，本机数据不动。
    /// </summary>
    /// <remarks>调用方（页面）负责先弹确认框：删了就没了，云端不会留副本。</remarks>
    [RelayCommand]
    private async Task ClearCloudAsync()
    {
        await RunAsync(() => _cloud.ClearCloudAsync());
    }

    /// <summary>
    ///     页面离开可视树时断开单例事件。
    /// </summary>
    public void Detach()
    {
        _cloud.StateChanged -= OnCloudStateChanged;
        _auth.StateChanged -= OnAuthStateChanged;
    }

    /// <summary>用户的动作：忙就忽略，异常翻成一句能看的话。</summary>
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
            return;

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // 页面离开或用户取消：不是错误。
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "云同步操作失败。");
            // 云存储层的消息本身就是给用户看的（「云端空间不够了，先清理一些云文件」），
            // 其余异常一律用通用文案兜住，免得把英文技术细节糊到界面上。
            ErrorMessage = exception is CloudStorageException
                ? exception.Message
                : CR.Settings_Cloud_ErrorDescription;
        }
        finally
        {
            IsBusy = false;
            Apply();
        }
    }

    /// <summary>把服务里的当前状态搬到界面属性上。</summary>
    private void Apply()
    {
        IsSignedIn = _auth.IsSignedIn;

        var usage = _cloud.Usage;
        HasUsage = usage.TotalStorage > 0;
        CloudUsageText = HasUsage
            ? string.Format(CR.Settings_Cloud_UsageUsed, usage.UsedText, usage.TotalText)
            : string.Empty;
        CloudAvailableText = HasUsage
            ? string.Format(CR.Settings_Cloud_UsageAvailable, usage.AvailableText)
            : string.Empty;
        PlatformUsageText = usage.PlatformUsedText;
        UsagePercent = Math.Clamp(usage.Percentage, 0, 100);

        HasCloudBackup = _cloud.HasCloudBackup;
        HasCloudFiles = _cloud.HasCloudFiles;
        RemoteIsNewer = _cloud.RemoteIsNewer;
        RemoteCleared = _cloud.RemoteCleared;
        LastSyncedText = _cloud.LastSyncedAt is { } synced
            ? string.Format(CR.Settings_Cloud_LastSynced, synced.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
            : CR.Settings_Cloud_NeverSynced;
    }

    /// <summary>同步状态变了（查完用量、上传完、自动拉了、后台失败了）。</summary>
    /// <remarks>事件可能从后台线程发出（定时器、令牌刷新），改绑定属性前一律回到 UI 线程。</remarks>
    private void OnCloudStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyState();
        else
            Dispatcher.UIThread.Post(ApplyState);
    }

    /// <summary>登录 / 退出后要重新算「能不能开云同步」，用量那块也跟着重画。</summary>
    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyState();
        else
            Dispatcher.UIThread.Post(ApplyState);
    }

    /// <summary>刷新界面，并把服务记下的后台失败照搬过来（用户自己的操作错误不动它）。</summary>
    private void ApplyState()
    {
        Apply();
        if (!string.IsNullOrEmpty(_cloud.LastError))
            ErrorMessage = _cloud.LastError;
    }
}
