namespace Corkboard.Core.Services.Cloud;

/// <summary>
///     云端里一份作业板备份的组成：分片包的清单 + 它的分片，或者最早那种一整包传上去的裸文件。
/// </summary>
/// <param name="BackupId">分片包的备份标识；老格式没有这个。</param>
/// <param name="Manifest">清单文件（新格式才有）。</param>
/// <param name="Parts">同一个备份标识下的分片。</param>
/// <param name="Legacy">老格式的整包文件（<c>corkboard-board.zip</c> 与 base64 版）。</param>
public sealed record CloudBoardBackup(
    string? BackupId,
    CloudFileInfo? Manifest,
    IReadOnlyList<CloudFileInfo> Parts,
    IReadOnlyList<CloudFileInfo> Legacy)
{
    /// <summary>云端到底有没有作业板备份。</summary>
    public bool HasAny => Manifest is not null || Legacy.Count > 0;

    /// <summary>这份备份里最新的一条云端记录（自动拉取比时间要用）。</summary>
    public CloudFileInfo? Newest => CloudBackupFiles.Newest([.. Parts, .. Legacy, .. Manifest is null ? [] : new[] { Manifest }]);

    /// <summary>最新那份老格式整包（只剩它没有清单时走老路径恢复）。</summary>
    public CloudFileInfo? NewestLegacy => CloudBackupFiles.Newest(Legacy);
}

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

    /// <summary>是不是老格式的作业板整包（含 base64 传的那份，兼容之前留下的裸 zip 名）。</summary>
    public static bool IsBoardArchive(string? fileName) =>
        string.Equals(fileName, BoardArchiveName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, BoardCloudName, StringComparison.OrdinalIgnoreCase);

    /// <summary>是不是 base64 传的作业板备份。</summary>
    public static bool IsBase64BoardArchive(string? fileName) =>
        string.Equals(fileName, BoardCloudName, StringComparison.OrdinalIgnoreCase);

    /// <summary>是不是设置备份。</summary>
    public static bool IsSettingsFile(string? fileName) =>
        string.Equals(fileName, SettingsFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     在文件列表里找作业板备份的最后一条记录（老格式整包或新格式的清单/分片，取时间最新的那份）。
    ///     只关心「云端有没有、更新到几点」的地方用它；真要恢复得用
    ///     <see cref="ReadBoardBackup" /> 把一份备份认全。
    /// </summary>
    public static CloudFileInfo? FindBoard(IEnumerable<CloudFileInfo> files) => Newest(AllBoardFiles(files));

    /// <summary>文件列表里所有作业板备份：老格式整包 + 新格式的清单与分片。</summary>
    public static IEnumerable<CloudFileInfo> AllBoardFiles(IEnumerable<CloudFileInfo> files) =>
        files.Where(file => IsBoardArchive(file.FileName) || CloudBackupPackage.IsBoardFile(file.FileName));

    /// <summary>
    ///     读一遍云端文件列表，把作业板备份认成一份：新格式只认清单最新的那一代（连它的分片），
    ///     老格式取最新的整包。认不出来就返回空的一份，不抛异常。
    /// </summary>
    public static CloudBoardBackup ReadBoardBackup(IEnumerable<CloudFileInfo> files)
    {
        var all = files.ToList();

        var manifests = new List<CloudFileInfo>();
        var parts = new List<(string BackupId, CloudFileInfo File)>();
        foreach (var file in all)
        {
            if (!CloudBackupPackage.TryParseFileName(file.FileName, out var backupId, out var isManifest))
                continue;

            if (isManifest)
                manifests.Add(file);
            else
                parts.Add((backupId, file));
        }

        var manifest = Newest(manifests);
        string? currentId = null;
        if (manifest is not null)
        {
            CloudBackupPackage.TryParseFileName(manifest.FileName, out var parsedId, out _);
            currentId = parsedId;
        }

        var currentParts = currentId is null
            ? []
            : parts.Where(part => string.Equals(part.BackupId, currentId, StringComparison.Ordinal))
                .Select(part => part.File)
                .ToList();

        return new CloudBoardBackup(currentId, manifest, currentParts,
            all.Where(file => IsBoardArchive(file.FileName)).ToList());
    }

    /// <summary>在文件列表里找设置备份（同名有多份时取最新的一份）。</summary>
    public static CloudFileInfo? FindSettings(IEnumerable<CloudFileInfo> files) =>
        Newest(files.Where(file => IsSettingsFile(file.FileName)));

    /// <summary>文件列表里所有作业板备份（用来把同名旧件一次删干净）。</summary>
    public static IEnumerable<CloudFileInfo> AllBoardArchives(IEnumerable<CloudFileInfo> files) =>
        files.Where(file => IsBoardArchive(file.FileName));

    /// <summary>文件列表里所有设置备份。</summary>
    public static IEnumerable<CloudFileInfo> AllSettingsFiles(IEnumerable<CloudFileInfo> files) =>
        files.Where(file => IsSettingsFile(file.FileName));

    /// <summary>这个云端文件是不是 Corkboard 自己传上去的备份（作业板整包 / 分片 / 清单，或设置文件）。</summary>
    public static bool IsManagedFile(CloudFileInfo file) =>
        IsBoardArchive(file.FileName) || CloudBackupPackage.IsBoardFile(file.FileName) ||
        IsSettingsFile(file.FileName);

    /// <summary>
    ///     文件列表里属于 Corkboard 云备份的全部文件，用来「一键清空云端」。
    /// </summary>
    /// <remarks>
    ///     只认自己这一套文件名：平台上还可能有别的文件（用户从别处传的、别的应用留下的），
    ///     清空云端不能把它们一起删掉。中断的上传留下的孤儿分片也在内——它们占着配额，
    ///     而 <see cref="ReadBoardBackup" /> 认不出是一份完整备份，正是最该清掉的那类垃圾。
    /// </remarks>
    public static IReadOnlyList<CloudFileInfo> AllManagedFiles(IEnumerable<CloudFileInfo> files) =>
        files.Where(IsManagedFile).ToList();

    /// <summary>
    ///     取最新的一份。<paramref name="files" /> 是服务端返回的列表（实测按时间倒序），
    ///     先用 <see cref="CloudFileInfo.UpdatedAt" /> 比，时间缺失就按列表顺序（在前面的更新）。
    ///     <para>
    ///         从前这里取的是「最后出现的一份」，于是云端一旦因为历史原因留下同名的旧件，
    ///         恢复就会拿到那份旧的——所以宁可比时间。
    ///     </para>
    /// </summary>
    public static CloudFileInfo? Newest(IEnumerable<CloudFileInfo> files)
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
