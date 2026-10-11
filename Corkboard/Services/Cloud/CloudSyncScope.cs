namespace Corkboard.Services.Cloud;

/// <summary>云同步要处理的内容范围。</summary>
[Flags]
public enum CloudSyncScope
{
    /// <summary>什么都不做。</summary>
    None = 0,

    /// <summary>作业板数据（<c>data/board/</c>）。</summary>
    Board = 1,

    /// <summary>设置（本机专属项除外）。</summary>
    Settings = 2,

    /// <summary>作业板与设置。</summary>
    All = Board | Settings
}
