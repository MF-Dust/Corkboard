using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Corkboard.Core.Abstraction;
using Corkboard.Core.Attributes;
using Corkboard.Core.Icons;
using Corkboard.Services.Ui;
using Corkboard.ViewModels.SettingsPages;
using FluentAvalonia.UI.Controls;
using CR = Corkboard.Core.Langs.Common.Resources;

namespace Corkboard.Views.SettingsPages.Account;

/// <summary>
///     账号设置页：展示 SECTL 组织账号并处理登录 / 退出。
/// </summary>
[PageInfo("settings.account", FluentIcons.PersonFilled)]
public partial class AccountSettingsPage : UserControl
{
    private readonly AccountSettingsPageViewModel _viewModel;

    public AccountSettingsPage()
    {
        InitializeComponent();

        _viewModel = IAppHost.GetService<AccountSettingsPageViewModel>();
        DataContext = _viewModel;

        // 页面是键控 DI 造出来的 transient，离开可视树必须断开单例上的事件（见 VM 的 Detach）。
        Unloaded += (_, _) => _viewModel.Detach();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    ///     退出登录：先弹一句确认，确认后才真的清凭据。
    /// </summary>
    private async void SignOut_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;

        var dialog = new FAContentDialog
        {
            Title = CR.Settings_Account_SignOutConfirmTitle,
            Content = CR.Settings_Account_SignOutConfirmMessage,
            PrimaryButtonText = CR.Settings_Account_SignOut,
            CloseButtonText = CR.Common_Cancel,
            DefaultButton = FAContentDialogButton.Close
        };

        // 主窗口可能设了不透明度，对话框期间要让位给 1，否则框自己也是半透明的。
        using var modalOpacity = ModalOpacityScope.Begin(this);
        if (await dialog.ShowAsync(topLevel) != FAContentDialogResult.Primary) return;

        await _viewModel.SignOutCommand.ExecuteAsync(null);
    }
}
