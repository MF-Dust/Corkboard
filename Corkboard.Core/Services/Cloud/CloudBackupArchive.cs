using System.IO.Compression;
using System.Text.Json;

namespace Corkboard.Core.Services.Cloud;

/// <summary>备份包里的清单：说明这份备份是谁、在什么时候、用哪个版本打的。</summary>
public sealed record CloudBackupManifest(string App, string Version, string PlatformId, DateTimeOffset CreatedAt);

/// <summary>作业板数据的打包与解包（zip 内含一份 <c>manifest.json</c>）。</summary>
public static class CloudBackupArchive
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    /// <summary>把目录里的全部文件打成一个 zip；目录不存在时只打清单。</summary>
    public static void Create(string sourceDirectory, string archivePath, CloudBackupManifest manifest)
    {
        var directory = Path.GetDirectoryName(archivePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(archivePath))
            File.Delete(archivePath);

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        var manifestEntry = archive.CreateEntry(CloudBackupFiles.ManifestEntryName, CompressionLevel.Optimal);
        using (var stream = manifestEntry.Open())
            JsonSerializer.Serialize(stream, manifest, ManifestOptions);

        if (!Directory.Exists(sourceDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            if (string.Equals(relative, CloudBackupFiles.ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                continue;

            archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
        }
    }

    /// <summary>读备份包里的清单；没有或者读不出来就返回 <c>null</c>。</summary>
    public static CloudBackupManifest? ReadManifest(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry(CloudBackupFiles.ManifestEntryName);
            if (entry is null)
                return null;

            using var stream = entry.Open();
            return JsonSerializer.Deserialize<CloudBackupManifest>(stream, ManifestOptions);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     把 zip 解到目标目录（覆盖同名文件），返回解出来的文件数。
    ///     <para>
    ///         清单文件不落盘；zip 里出现指向目标目录之外的路径时直接拒绝——
    ///         备份包来自网络，不能让它写到数据根外面去。
    ///     </para>
    /// </summary>
    public static int Extract(string archivePath, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var targetRoot = Path.GetFullPath(targetDirectory);

        using var archive = ZipFile.OpenRead(archivePath);
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            if (string.Equals(entry.FullName, CloudBackupFiles.ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                continue;

            var destination = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName));
            if (!destination.StartsWith(targetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new CloudStorageException("invalid_backup", "备份包里有指向数据目录之外的路径，已拒绝恢复");

            var folder = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            entry.ExtractToFile(destination, true);
            count++;
        }

        return count;
    }
}
