using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Corkboard.Core.Helpers;
using Corkboard.Core.Services.Config;
using Corkboard.Services.Auth;
using Microsoft.Extensions.Logging;
using CR = Corkboard.Core.Langs.Common.Resources;

namespace Corkboard.ViewModels.SettingsPages;

/// <summary>
///     账号设置页：展示当前登录的 SECTL 组织账号，并提供登录 / 退出。
/// </summary>
/// <remarks>
///     必须订阅 <see cref="ISectlAuthService.StateChanged" />、不能只在构造时读一次：登录要跳浏览器、
///     资料与头像是登录后异步补齐的、凭据失效也是服务端在后台发现的，只读一次的话这些变化都到不了界面上
///     （表现就是「登录回来仍然显示未登录」，只能重启应用）。
/// </remarks>
public sealed partial class AccountSettingsPageViewModel : ViewModelBase
{
    private readonly ISectlAuthService _auth;
    private readonly ILogger<AccountSettingsPageViewModel>? _logger;
    private Bitmap? _avatar;

    /// <param name="configHandler">配置句柄，交给 <see cref="ViewModelBase" /> 持有。</param>
    /// <param name="auth">SECTL 账号服务（单例）。</param>
    /// <param name="logger">日志，可选（测试与设计器场景可能没有）。</param>
    public AccountSettingsPageViewModel(MainConfigHandler configHandler,
        ISectlAuthService auth,
        ILogger<AccountSettingsPageViewModel>? logger = null) : base(configHandler)
    {
        _auth = auth;
        _logger = logger;

        Apply();
        _auth.StateChanged += OnAuthStateChanged;
    }

    /// <summary>本机是否已有可用会话。</summary>
    [ObservableProperty] private bool _isSignedIn;

    /// <summary>正在登录或退出：两个按钮都按不动，避免重复发起授权。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>登录已发起、正等浏览器里的 loopback 回调（这一步可能要几十秒）。</summary>
    [ObservableProperty] private bool _isWaitingForBrowser;

    /// <summary>账号资料的显示名（昵称，昵称为空时退回账号 ID）。</summary>
    [ObservableProperty] private string _displayName = string.Empty;

    /// <summary>账号 ID（SECTL 用户名 / 学号）。</summary>
    [ObservableProperty] private string _accountId = string.Empty;

    /// <summary>邮箱，服务端可能不给。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmail))]
    private string _email = string.Empty;

    /// <summary>没有头像时显示在圆圈里的首字。</summary>
    [ObservableProperty] private string _initial = string.Empty;

    /// <summary>凭据已失效，需要重新登录才继续可用。</summary>
    [ObservableProperty] private bool _requiresReauthorization;

    /// <summary>失败原因；为空表示当前没有要提示的错误。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>账号头像；服务端不给或解码失败时为 <c>null</c>。</summary>
    public Bitmap? Avatar => _avatar;

    /// <summary>是否有头像可显示。</summary>
    public bool HasAvatar => _avatar is not null;

    /// <summary>已登录但拿不到头像：用首字占位。</summary>
    public bool ShowInitial => IsSignedIn && _avatar is null;

    /// <summary>未登录：圆圈里放人形轮廓，不留一个空圆。</summary>
    public bool ShowPlaceholder => !IsSignedIn && _avatar is null;

    /// <summary>服务端给了邮箱才显示邮箱那一行。</summary>
    public bool HasEmail => !string.IsNullOrEmpty(Email);

    /// <summary>有错误才显示错误那一行。</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    ///     登录：打开系统浏览器走 OAuth 2.0 PKCE，回调由 loopback 监听器接住。
    /// </summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy) return;

        ErrorMessage = null;
        IsBusy = true;
        IsWaitingForBrowser = true;
        try
        {
            await _auth.SignInAsync();
        }
        catch (OperationCanceledException)
        {
            // 用户关掉了浏览器或页面已经离开：不是错误，界面回到原来的登录态即可。
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "SECTL 账号登录失败。");
            // 服务层抛出的消息本身就是给用户看的（「无法打开浏览器。」「登录超时，请重试。」），
            // 其余异常一律用通用文案兜住，免得把英文技术细节糊到界面上。
            ErrorMessage = exception is InvalidOperationException
                ? exception.Message
                : CR.Settings_Account_ErrorDescription;
        }
        finally
        {
            IsWaitingForBrowser = false;
            IsBusy = false;
            Apply();
        }
    }

    /// <summary>
    ///     退出登录：以本机为准（服务端不可达也照样退出），并清掉本机保存的凭据。
    /// </summary>
    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (IsBusy) return;

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _auth.SignOutAsync();
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "SECTL 账号退出失败。");
            ErrorMessage = CR.Settings_Account_ErrorDescription;
        }
        finally
        {
            IsBusy = false;
            Apply();
        }
    }

    /// <summary>
    ///     页面离开可视树时断开单例事件。
    /// </summary>
    /// <remarks>
    ///     页面与 ViewModel 都是 transient，而 <see cref="ISectlAuthService" /> 是单例：
    ///     不断开的话每开一次账号页都会在单例上多挂一个处理器，页面也就回收不掉。
    /// </remarks>
    public void Detach()
    {
        _auth.StateChanged -= OnAuthStateChanged;
    }

    /// <summary>把服务里的当前状态搬到界面属性上。</summary>
    private void Apply()
    {
        IsSignedIn = _auth.IsSignedIn;
        RequiresReauthorization = _auth.RequiresReauthorization;
        DisplayName = ResolveDisplayName(_auth.User, _auth.Token?.UserId);
        AccountId = ResolveAccountId(_auth.User, _auth.Token?.UserId);
        Email = _auth.User?.ResolvedEmail ?? string.Empty;
        // 显示名已经做过「昵称 → 账号 ID」的回退，所以这里只把显示名交给首字解析（与上游一致）。
        Initial = AvatarInitialResolver.Resolve(DisplayName, null);
        UpdateAvatar();
    }

    /// <summary>按当前状态重新解码头像；没有可用图片（或解码失败）时退回首字 / 人形轮廓。</summary>
    private void UpdateAvatar()
    {
        Bitmap? avatar = null;
        if (IsSignedIn && _auth.AvatarBytes is { Length: > 0 } bytes)
            try
            {
                using var stream = new MemoryStream(bytes);
                avatar = new Bitmap(stream);
            }
            catch (Exception exception)
            {
                // 头像解码失败只是少一张图，绝不能连累账号资料显示。
                _logger?.LogDebug(exception, "账号头像解码失败。");
                avatar = null;
            }

        // 旧位图不主动 Dispose：绑定可能还在渲染它。头像很小，一次会话里也换不了几次。
        _avatar = avatar;
        OnPropertyChanged(nameof(Avatar));
        OnPropertyChanged(nameof(HasAvatar));
        OnPropertyChanged(nameof(ShowInitial));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    /// <summary>
    ///     账号状态变了（登录完成、资料补齐、凭据失效、退出）。
    /// </summary>
    /// <remarks>
    ///     事件可能从后台线程发出（令牌刷新、启动期会话恢复），而这里要改的是位图与绑定属性，
    ///     所以一律先回到 UI 线程。
    /// </remarks>
    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>登录态变化要顺带刷新「首字 / 人形轮廓」这两个由它派生的开关。</summary>
    partial void OnIsSignedInChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInitial));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    /// <summary>显示名：昵称优先，其次账号 ID。</summary>
    internal static string ResolveDisplayName(SectlUser? user, string? tokenUserId)
    {
        return FirstNonBlank(user?.ResolvedUserName, user?.ResolvedUserId, tokenUserId) ?? string.Empty;
    }

    /// <summary>账号 ID：只认服务端给的用户 ID 与令牌里的用户 ID。</summary>
    internal static string ResolveAccountId(SectlUser? user, string? tokenUserId)
    {
        return FirstNonBlank(user?.ResolvedUserId, tokenUserId) ?? string.Empty;
    }

    /// <summary>第一个有内容的串（去掉两端空白），全为空时返回 <c>null</c>。</summary>
    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();

        return null;
    }
}
