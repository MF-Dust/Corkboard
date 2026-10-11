using System.IO.Compression;
using Corkboard.Core.Services.Cloud;

namespace Corkboard.Core.Tests;

/// <summary>云存储用量、文件列表、错误体、备份包与自动同步策略的单测。</summary>
public class CloudStorageTests
{
    private const string UsageJson = """
    {
      "used_storage": 1048576,
      "used_storage_formatted": "1 MB",
      "total_storage": 10485760,
      "total_storage_formatted": "10 MB",
      "available_storage": 9437184,
      "available_storage_formatted": "9 MB",
      "percentage": 10,
      "file_count": 3,
      "platform_used_storage": 524288,
      "platform_used_storage_formatted": "512 KB",
      "platform_file_count": 2,
      "reconciled": false
    }
    """;

    /// <summary>用量返回体照文档给的字段解析，优先用服务端给的可读串。</summary>
    [Fact]
    public void Usage_ParsesDocumentedFields()
    {
        var usage = CloudStorageUsage.Parse(UsageJson);

        Assert.Equal(1048576, usage.UsedStorage);
        Assert.Equal(10485760, usage.TotalStorage);
        Assert.Equal(9437184, usage.AvailableStorage);
        Assert.Equal(10, usage.Percentage);
        Assert.Equal(3, usage.FileCount);
        Assert.Equal(524288, usage.PlatformUsedStorage);
        Assert.Equal(2, usage.PlatformFileCount);
        Assert.Equal("1 MB", usage.UsedText);
        Assert.Equal("512 KB", usage.PlatformUsedText);
    }

    /// <summary>服务端没给可读串时自己格式化，且缺字段不抛异常。</summary>
    [Fact]
    public void Usage_FallsBackToLocalFormatting()
    {
        var usage = CloudStorageUsage.Parse("""{ "used_storage": 2048, "platform_used_storage": 0 }""");

        Assert.Equal("2 KB", usage.UsedText);
        Assert.Equal("0 B", usage.PlatformUsedText);
        Assert.Equal(0, usage.TotalStorage);
    }

    /// <summary>体积写法用 1024 进制、10 以下带一位小数。</summary>
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(10485760, "10 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void Format_UsesReadableUnits(long bytes, string expected)
    {
        Assert.Equal(expected, CloudStorageFormat.Bytes(bytes));
    }

    /// <summary>文件列表按文件名找到备份件。</summary>
    [Fact]
    public void Files_FindBackupsByName()
    {
        const string json = """
        {
          "files": [
            { "file_id": "cf_1", "filename": "photo.png", "size": 100, "updated_at": "2026-04-11T08:00:00.000Z" },
            { "file_id": "cf_2", "filename": "corkboard-board.zip", "size": 2048, "updated_at": "2026-04-12T08:00:00.000Z" },
            { "file_id": "cf_3", "filename": "corkboard-settings.json", "size": 512, "updated_at": "2026-04-12T09:00:00.000Z" }
          ],
          "total": 3,
          "has_more": false
        }
        """;

        var files = CloudFileInfo.ParseList(json);

        Assert.Equal(3, files.Count);
        Assert.Equal("cf_2", CloudBackupFiles.FindBoard(files)!.FileId);
        Assert.Equal("cf_3", CloudBackupFiles.FindSettings(files)!.FileId);
        Assert.Equal("2 KB", CloudBackupFiles.FindBoard(files)!.SizeText);
        Assert.NotNull(CloudBackupFiles.FindBoard(files)!.UpdatedAt);
    }

    /// <summary>同名备份有好几份（历史上重复上传留下的）时取最新的一份，旧件也要能被一次列全。</summary>
    [Fact]
    public void Files_WithDuplicatesUseTheNewest()
    {
        // 服务端实测按时间倒序返回，这里也照这个顺序：第一份最新。
        const string json = """
        {
          "files": [
            { "file_id": "cf_new_board", "filename": "corkboard-board.zip", "size": 3072, "updated_at": "2026-04-13T08:00:00.000Z" },
            { "file_id": "cf_new_settings", "filename": "corkboard-settings.json", "size": 1024, "updated_at": "2026-04-13T08:00:01.000Z" },
            { "file_id": "cf_old_settings", "filename": "corkboard-settings.json", "size": 512, "updated_at": "2026-04-12T08:00:00.000Z" },
            { "file_id": "cf_old_board", "filename": "corkboard-board.zip", "size": 2048, "updated_at": "2026-04-12T08:00:00.000Z" }
          ],
          "total": 4,
          "has_more": false
        }
        """;

        var files = CloudFileInfo.ParseList(json);

        Assert.Equal("cf_new_board", CloudBackupFiles.FindBoard(files)!.FileId);
        Assert.Equal("cf_new_settings", CloudBackupFiles.FindSettings(files)!.FileId);
        Assert.Equal(new[] { "cf_new_board", "cf_old_board" },
            CloudBackupFiles.AllBoardArchives(files).Select(file => file.FileId));
        Assert.Equal(new[] { "cf_new_settings", "cf_old_settings" },
            CloudBackupFiles.AllSettingsFiles(files).Select(file => file.FileId));
    }

    /// <summary>
    ///     二进制备份走 base64：云存储会把上传的字节按 UTF-8 解码（非法字节变 U+FFFD），
    ///     所以压缩包必须先变成纯 ASCII 文本再传，取回来还能原样还原。
    /// </summary>
    [Fact]
    public async Task Payload_Base64RoundTripsBinary()
    {
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)i;

        var encoded = CloudBackupPayload.Encode(bytes);

        Assert.True(encoded.All(character => character < 128));
        Assert.Equal(bytes, CloudBackupPayload.Decode(encoded));
        Assert.Equal(bytes, CloudBackupPayload.Decode("  " + encoded + Environment.NewLine));
        Assert.Equal(bytes, CloudBackupPayload.Decode("\uFEFF" + encoded));

        var directory = Path.Combine(Path.GetTempPath(), "corkboard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.bin");
            var encodedPath = Path.Combine(directory, "encoded.txt");
            var decodedPath = Path.Combine(directory, "decoded.bin");
            var token = TestContext.Current.CancellationToken;
            await File.WriteAllBytesAsync(source, bytes, token);

            await CloudBackupPayload.WriteEncodedAsync(source, encodedPath, token);
            await CloudBackupPayload.WriteDecodedAsync(encodedPath, decodedPath, token);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(decodedPath, token));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>作业板备份的云端名带 base64 后缀，挑件时也认它。</summary>
    [Fact]
    public void Files_RecognizesBase64BoardArchive()
    {
        Assert.Equal("corkboard-board.zip.b64", CloudBackupFiles.BoardCloudName);
        Assert.True(CloudBackupFiles.IsBoardArchive(CloudBackupFiles.BoardArchiveName));
        Assert.True(CloudBackupFiles.IsBoardArchive(CloudBackupFiles.BoardCloudName));
        Assert.False(CloudBackupFiles.IsBase64BoardArchive(CloudBackupFiles.BoardArchiveName));
        Assert.True(CloudBackupFiles.IsBase64BoardArchive(CloudBackupFiles.BoardCloudName));
        Assert.False(CloudBackupFiles.IsSettingsFile(CloudBackupFiles.BoardCloudName));

        var files = CloudFileInfo.ParseList("""
        {
          "files": [
            { "file_id": "cf_b64", "filename": "corkboard-board.zip.b64", "size": 4096, "updated_at": "2026-04-13T08:00:00.000Z" },
            { "file_id": "cf_old", "filename": "corkboard-board.zip", "size": 2048, "updated_at": "2026-04-12T08:00:00.000Z" }
          ],
          "total": 2,
          "has_more": false
        }
        """);

        Assert.Equal("cf_b64", CloudBackupFiles.FindBoard(files)!.FileId);
        Assert.Equal(2, CloudBackupFiles.AllBoardArchives(files).Count());
    }

    /// <summary>错误体解析；正常返回体不会被当成错误。</summary>
    [Fact]
    public void Error_ParsesOnlyErrorBodies()
    {
        var error = CloudStorageError.TryParse("""{ "error": "storage_exceeded", "error_description": "Storage quota exceeded" }""");
        Assert.Equal("storage_exceeded", error!.Code);
        Assert.Equal("Storage quota exceeded", error.Description);
        Assert.Null(CloudStorageError.TryParse("""{ "success": true }"""));
        Assert.Null(CloudStorageError.TryParse(null));
        Assert.Null(CloudStorageError.TryParse("<html>"));
    }

    /// <summary>自动拉取的判定：本机有未同步改动时绝不自动拉。</summary>
    [Fact]
    public void Policy_AutoPullOnlyWhenLocalIsClean()
    {
        var lastSync = new DateTimeOffset(2026, 4, 11, 8, 0, 0, TimeSpan.Zero);
        var remoteNewer = lastSync.AddHours(1);

        Assert.False(CloudSyncPolicy.ShouldAutoPull(null, lastSync, lastSync));
        // 新设备：云端有备份、本机什么也没有
        Assert.True(CloudSyncPolicy.ShouldAutoPull(remoteNewer, null, null));
        // 本机有数据但从没同步过：不能拿云端盖掉
        Assert.False(CloudSyncPolicy.ShouldAutoPull(remoteNewer, null, lastSync));
        // 本机自上次同步后没改过 → 可以自动拉
        Assert.True(CloudSyncPolicy.ShouldAutoPull(remoteNewer, lastSync, lastSync));
        // 本机改过了 → 交给用户决定
        Assert.False(CloudSyncPolicy.ShouldAutoPull(remoteNewer, lastSync, lastSync.AddMinutes(5)));
        // 云端不比本机新
        Assert.False(CloudSyncPolicy.ShouldAutoPull(lastSync, lastSync, lastSync));
    }

    /// <summary>需不需要上传的判定。</summary>
    [Fact]
    public void Policy_NeedsUploadWhenLocalIsNewer()
    {
        var lastSync = new DateTimeOffset(2026, 4, 11, 8, 0, 0, TimeSpan.Zero);

        Assert.False(CloudSyncPolicy.NeedsUpload(null, lastSync));
        Assert.True(CloudSyncPolicy.NeedsUpload(lastSync, null));
        Assert.True(CloudSyncPolicy.NeedsUpload(lastSync.AddMinutes(1), lastSync));
        Assert.False(CloudSyncPolicy.NeedsUpload(lastSync, lastSync));
    }

    /// <summary>自动上传的防抖截止时间：只在更早的时候才提前，不能被后到的长等待推后。</summary>
    [Fact]
    public void Policy_NextDeadlineKeepsTheEarlierOne()
    {
        var now = new DateTimeOffset(2026, 4, 11, 8, 0, 0, TimeSpan.Zero);

        // 没排过任何等待：按这次的延迟算。
        var boardDeadline = CloudSyncPolicy.NextDeadline(null, now, TimeSpan.FromSeconds(5));
        Assert.Equal(now.AddSeconds(5), boardDeadline);

        // 作业板改动后紧跟一次设置落盘（45 秒）：还得按作业板那 5 秒算。
        Assert.Equal(boardDeadline, CloudSyncPolicy.NextDeadline(boardDeadline, now, TimeSpan.FromSeconds(45)));
        Assert.Equal(boardDeadline, CloudSyncPolicy.NextDeadline(boardDeadline, now.AddSeconds(1), TimeSpan.FromSeconds(45)));

        // 反过来：先排着 45 秒、这时作业板改了，要提前到 5 秒。
        var settingsDeadline = CloudSyncPolicy.NextDeadline(null, now, TimeSpan.FromSeconds(45));
        Assert.Equal(now.AddSeconds(5), CloudSyncPolicy.NextDeadline(settingsDeadline, now, TimeSpan.FromSeconds(5)));

        // 已经过点的截止时间照样保留：那次上传本来就要发生了，再往后推只会白等，
        // 而且上传时看的是当时的改动，新改动照样会一起传上去。
        var overdue = now.AddSeconds(-1);
        Assert.Equal(overdue, CloudSyncPolicy.NextDeadline(overdue, now, TimeSpan.FromSeconds(5)));
    }

    /// <summary>备份包能原样打回来，清单不落到数据目录里。</summary>
    [Fact]
    public void Archive_RoundTripsBoardData()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var notes = Path.Combine(source, "2026", "10", "11", "notes.json");
            Directory.CreateDirectory(Path.GetDirectoryName(notes)!);
            File.WriteAllText(notes, """{"notes":[]}""");

            var archivePath = Path.Combine(root, "board.zip");
            CloudBackupArchive.Create(source, archivePath, new CloudBackupManifest("Corkboard", "1.0.0", "pf_test", DateTimeOffset.UnixEpoch));

            var manifest = CloudBackupArchive.ReadManifest(archivePath);
            Assert.NotNull(manifest);
            Assert.Equal("pf_test", manifest!.PlatformId);

            var target = Path.Combine(root, "restored");
            Assert.Equal(1, CloudBackupArchive.Extract(archivePath, target));
            Assert.Equal("""{"notes":[]}""", File.ReadAllText(Path.Combine(target, "2026", "10", "11", "notes.json")));
            Assert.False(File.Exists(Path.Combine(target, CloudBackupFiles.ManifestEntryName)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>备份包里带目录穿越路径时必须拒绝恢复。</summary>
    [Fact]
    public void Archive_RejectsPathTraversal()
    {
        var root = CreateTempDirectory();
        try
        {
            var archivePath = Path.Combine(root, "evil.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escaped.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("bad");
            }

            var target = Path.Combine(root, "restored");
            Assert.Throws<CloudStorageException>(() => CloudBackupArchive.Extract(archivePath, target));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "corkboard-cloud-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
