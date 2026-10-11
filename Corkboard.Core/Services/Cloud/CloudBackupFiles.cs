namespace Corkboard.Core.Services.Cloud;

/// <summary>云端备份用的固定文件名（同一个账号 + 同一个平台就是一个独立空间，不需要再加前缀）。</summary>
public static class CloudBackupFiles
{
    /// <summary>作业板数据的压缩包。</summary>
    public const string BoardArchiveName = "corkboard-board.zip";

    /// <summary>
    ///     上传到云端时用的作业板备份名：内容是压缩包的 base64 文本。
    ///     云存储会把上传的字节按 UTF-8 解码一遍（无效字节变成 U+FFFD），二进制包传上去就坏了，
    ///     所以压缩包一律先用 base64 变成纯 ASCII 再传。
    /// </summary>
    public const string BoardCloudName = BoardArchiveName + Base64Suffix;

    /// <summary>设置（已裁掉本机专属项）。</summary>
    public const string SettingsFileName = "corkboard-settings.json";

    /// <summary>压缩包内的清单文件名。</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>base64 备份名的后缀。</summary>
    public const string Base64Suffix = ".b64";

    /// <summary>是不是作业板备份包（含 base64 传的那份，兼容之前留下的裸 zip 名）。</summary>
    public static bool IsBoardArchive(string? fileName) =>
        string.Equals(fileName, BoardArchiveName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, BoardCloudName, StringComparison.OrdinalIgnoreCase);

    /// <summary>是不是 base64 传的作业板备份。</summary>
    public static bool IsBase64BoardArchive(string? fileName) =>
        string.Equals(fileName, BoardCloudName, StringComparison.OrdinalIgnoreCase);

    /// <summary>是不是设置备份。</summary>
    public static bool IsSettingsFile(string? fileName) =>
        string.Equals(fileName, SettingsFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>在文件列表里找作业板备份（同名有多份时取最新的一份）。</summary>
    public static CloudFileInfo? FindBoard(IEnumerable<CloudFileInfo> files) =>
        Newest(files.Where(file => IsBoardArchive(file.FileName)));

    /// <summary>在文件列表里找设置备份（同名有多份时取最新的一份）。</summary>
    public static CloudFileInfo? FindSettings(IEnumerable<CloudFileInfo> files) =>
        Newest(files.Where(file => IsSettingsFile(file.FileName)));

    /// <summary>文件列表里所有作业板备份（用来把同名旧件一次删干净）。</summary>
    public static IEnumerable<CloudFileInfo> AllBoardArchives(IEnumerable<CloudFileInfo> files) =>
        files.Where(file => IsBoardArchive(file.FileName));

    /// <summary>文件列表里所有设置备份。</summary>
    public static IEnumerable<CloudFileInfo> AllSettingsFiles(IEnumerable<CloudFileInfo> files) =>
        files.Where(file => IsSettingsFile(file.FileName));

    /// <summary>
    ///     取最新的一份。<paramref name="files" /> 是服务端返回的列表（实测按时间倒序），
    ///     先用 <see cref="CloudFileInfo.UpdatedAt" /> 比，时间缺失就按列表顺序（在前面的更新）。
    ///     <para>
    ///         从前这里取的是「最后出现的一份」，于是云端一旦因为历史原因留下同名的旧件，
    ///         恢复就会拿到那份旧的——所以宁可比时间。
    ///     </para>
    /// </summary>
    private static CloudFileInfo? Newest(IEnumerable<CloudFileInfo> files)
    {
        CloudFileInfo? best = null;
        foreach (var file in files)
        {
            if (best is null)
            {
                best = file;
                continue;
            }

            var fileTime = file.UpdatedAt ?? DateTimeOffset.MinValue;
            var bestTime = best.UpdatedAt ?? DateTimeOffset.MinValue;
            if (fileTime > bestTime)
                best = file;
        }

        return best;
    }
}
