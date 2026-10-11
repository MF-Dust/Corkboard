namespace Corkboard.ViewModels.SettingsPages;

/// <summary>
///     云同步「只同步最近多久的作业」的下拉项。
/// </summary>
/// <param name="Days">窗口天数，<c>0</c> 表示全部都传。</param>
/// <param name="DisplayName">界面上显示的名字。</param>
public sealed record SyncWindowOption(int Days, string DisplayName);
