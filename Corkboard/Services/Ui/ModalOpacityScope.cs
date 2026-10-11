using Avalonia;
using Avalonia.Controls;
using Corkboard.Views;

namespace Corkboard.Services.Ui;

/// <summary>
///     内容对话框这类「盖在主窗口上的面」在显示期间要求主窗口**完全不透明**。
///     <para>
///         主窗口不透明度是写进原生分层窗口的 alpha，整窗一起乘算：对话框自己也跟着变透明，
///         底下便签的文字、窗口后面的桌面就透上来了（用户反馈「框的底是透明的」）。
///         窗口 alpha 是整窗生效的，窗内画什么都不可能让对话框局部恢复不透明，
///         所以这里在显示期间把不透明度让位给 1，关闭后由窗口自己还原成配置值。
///     </para>
///     <para>
///         设置窗口不登记窗口不透明度能力，<see cref="Begin" /> 解析到它时是空操作；
///         弹层（<c>Border.sheet</c>）不走这里，窗口自己盯着 <see cref="PageOverlayService.IsOpen" />。
///     </para>
/// </summary>
public sealed class ModalOpacityScope : IDisposable
{
    private readonly MainWindow? _window;
    private bool _isDisposed;

    private ModalOpacityScope(MainWindow? window)
    {
        _window = window;
        _window?.EnterModalSurface();
    }

    /// <summary>从弹层里的可视元素找到它所在的窗口；不是主窗口（或还没挂到窗口上）时什么也不做。</summary>
    public static ModalOpacityScope Begin(Visual? owner)
    {
        var topLevel = owner is null ? null : TopLevel.GetTopLevel(owner);
        return new ModalOpacityScope(topLevel as MainWindow);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _window?.ExitModalSurface();
    }
}
