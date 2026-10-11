namespace Corkboard.Core.Services.Cloud;

/// <summary>云端备份用的固定文件名（同一个账号 + 同一个平台就是一个独立空间，不需要再加前缀）。</summary>
public static class CloudBackupFiles
{
    /// <summary>作业板数据的压缩包。</summary>
    public const string BoardArchiveName = "corkboard-board.zip";

    /// <summary>设置（已裁掉本机专属项）。</summary>
    public const string SettingsFileName = "corkboard-settings.json";

    /// <summary>压缩包内的清单文件名。</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>是不是作业板备份包。</summary>
    public static bool IsBoardArchive(string? fileName) =>
        string.Equals(fileName, BoardArchiveName, StringComparison.OrdinalIgnoreCase);

    /// <summary>是不是设置备份。</summary>
    public static bool IsSettingsFile(string? fileName) =>
        string.Equals(fileName, SettingsFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>在文件列表里找作业板备份（同名的多个取最后出现的一个）。</summary>
    public static CloudFileInfo? FindBoard(IEnumerable<CloudFileInfo> files) =>
        files.LastOrDefault(file => IsBoardArchive(file.FileName));

    /// <summary>在文件列表里找设置备份。</summary>
    public static CloudFileInfo? FindSettings(IEnumerable<CloudFileInfo> files) =>
        files.LastOrDefault(file => IsSettingsFile(file.FileName));
}
