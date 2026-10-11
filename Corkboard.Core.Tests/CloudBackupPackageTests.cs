using Corkboard.Core.Services.Cloud;

namespace Corkboard.Core.Tests;

/// <summary>分片备份包（切分、清单、校验、拼回、云端文件归类）的单测。</summary>
public class CloudBackupPackageTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 11, 9, 30, 0, TimeSpan.Zero);

    private static CloudFileInfo File(string name, int minutes, long size = 1024) =>
        new($"id-{name}", name, size, BaseTime.AddMinutes(minutes));

    private static CloudBackupPackageManifest ManifestOf(byte[] payload, int partBytes = 256, string? backupId = null)
    {
        var id = backupId ?? CloudBackupPackage.CreateBackupId(BaseTime);
        var parts = CloudBackupPackage.Slice(payload, partBytes);
        return CloudBackupPackage.BuildManifest(id, parts, partBytes, ["board"], BaseTime, "1.0.0",
            CloudBackupFiles.BoardArchiveName, payload);
    }

    /// <summary>切出来的片按顺序拼回去要和原归档一致，且除最后一片外都是整片长。</summary>
    [Fact]
    public void Slice_SplitsArchiveIntoOrderedPartsOfTheConfiguredSize()
    {
        var archive = new byte[1000];
        for (var i = 0; i < archive.Length; i++)
            archive[i] = (byte)(i % 251);

        var parts = CloudBackupPackage.Slice(archive, 256);

        Assert.Equal(4, parts.Count);
        Assert.Equal(256, parts[0].Length);
        Assert.Equal(256, parts[1].Length);
        Assert.Equal(256, parts[2].Length);
        Assert.Equal(232, parts[3].Length);
        Assert.Equal(archive, parts.SelectMany(part => part).ToArray());
    }

    /// <summary>空归档也留一片，免得出现「零个分片」这种补不回来的形态。</summary>
    [Fact]
    public void Slice_KeepsEmptyArchiveAsOneEmptyPart()
    {
        var parts = CloudBackupPackage.Slice([], 256);

        Assert.Single(parts);
        Assert.Empty(parts[0]);
    }

    /// <summary>分片大小要么为正、要么不超过默认片长（也就是请求体预算）。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CloudBackupPackage.DefaultPartBytes + 1)]
    public void Slice_RejectsPartSizesOutsideTheBudget(int partBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CloudBackupPackage.Slice(new byte[8], partBytes));

    /// <summary>清单里的片名、序号、长度、摘要都要对得上归档。</summary>
    [Fact]
    public void BuildManifest_RecordsPartNamesAndHashes()
    {
        var archive = new byte[600];
        Random.Shared.NextBytes(archive);

        var manifest = ManifestOf(archive, 256, "20261011-093000-abcdef01");

        Assert.Equal(CloudBackupPackage.Format, manifest.Format);
        Assert.Equal(CloudBackupPackage.SchemaVersion, manifest.SchemaVersion);
        Assert.Equal(600, manifest.ArchiveBytes);
        Assert.Equal(CloudBackupPackage.Hash(archive), manifest.ArchiveSha256);
        Assert.Equal(3, manifest.Parts.Count);
        Assert.Equal(["board"], manifest.Roots);
        Assert.Equal("corkboard-board-20261011-093000-abcdef01-p01of03.part", manifest.Parts[0].Name);
        Assert.Equal(256, manifest.Parts[0].Length);
        Assert.Equal(88, manifest.Parts[2].Length);
        Assert.Equal([1, 2, 3], manifest.Parts.Select(part => part.Index).ToArray());
    }

    /// <summary>片数超过上限直接拒绝（片号两位排不下）。</summary>
    [Fact]
    public void BuildManifest_RejectsTooManyParts()
    {
        var parts = Enumerable.Repeat(Array.Empty<byte>(), CloudBackupPackage.MaxPartCount + 1).ToArray();

        var error = Assert.Throws<InvalidDataException>(() => CloudBackupPackage.BuildManifest(
            "20261011-093000-abcdef01", parts, 256, ["board"], BaseTime, "1.0.0",
            CloudBackupFiles.BoardArchiveName, []));

        Assert.Equal("备份分片太多了。", error.Message);
    }

    /// <summary>默认片长本身要留在请求体预算里，明显超标的片长要被拦下。</summary>
    [Fact]
    public void EnsureUploadBodySize_KeepsTheDefaultPartInsideTheRequestBudget()
    {
        CloudBackupPackage.EnsureUploadBodySize(CloudBackupPackage.DefaultPartBytes);

        var error = Assert.Throws<InvalidDataException>(
            () => CloudBackupPackage.EnsureUploadBodySize((int)CloudBackupPackage.MaxUploadBodyBytes));

        Assert.Equal("云端备份分片体积超过上传限制。", error.Message);
    }

    /// <summary>原样拼回来的归档要逐字节相等。</summary>
    [Fact]
    public void MergeAndVerify_ReturnsTheOriginalArchive()
    {
        var archive = new byte[1_000];
        Random.Shared.NextBytes(archive);
        var manifest = ManifestOf(archive, 256);
        var parts = CloudBackupPackage.Slice(archive, 256);

        Assert.Equal(archive, CloudBackupPackage.MergeAndVerify(manifest, parts));
    }

    /// <summary>哪一片被改坏就点哪一片的序号。</summary>
    [Fact]
    public void MergeAndVerify_RejectsTamperedPart()
    {
        var archive = new byte[600];
        Random.Shared.NextBytes(archive);
        var manifest = ManifestOf(archive, 256);
        var parts = CloudBackupPackage.Slice(archive, 256);
        parts[1][0] ^= 0xFF;

        var error = Assert.Throws<InvalidDataException>(() => CloudBackupPackage.MergeAndVerify(manifest, parts));

        Assert.Equal("云端备份第 2 个分片校验失败。", error.Message);
    }

    /// <summary>分片都对但整包摘要对不上，说明清单本身被改过。</summary>
    [Fact]
    public void MergeAndVerify_RejectsTamperedArchiveHash()
    {
        var archive = new byte[600];
        Random.Shared.NextBytes(archive);
        var manifest = ManifestOf(archive, 256) with { ArchiveSha256 = CloudBackupPackage.Hash([9, 9, 9]) };
        var parts = CloudBackupPackage.Slice(archive, 256);

        var error = Assert.Throws<InvalidDataException>(() => CloudBackupPackage.MergeAndVerify(manifest, parts));

        Assert.Equal("云端备份归档校验失败。", error.Message);
    }

    /// <summary>片数与清单对不上时先拦下来，不去拼一份假归档。</summary>
    [Fact]
    public void MergeAndVerify_RejectsMissingParts()
    {
        var archive = new byte[600];
        Random.Shared.NextBytes(archive);
        var manifest = ManifestOf(archive, 256);
        var parts = CloudBackupPackage.Slice(archive, 256).Take(2).ToArray();

        var error = Assert.Throws<InvalidDataException>(() => CloudBackupPackage.MergeAndVerify(manifest, parts));

        Assert.Equal("云端备份的分片数量与清单不一致。", error.Message);
    }

    /// <summary>别的应用留下的清单（格式标识不对）不能拿来恢复。</summary>
    [Fact]
    public void ValidateManifest_RejectsUnsupportedFormat()
    {
        var manifest = ManifestOf([1, 2, 3], 256);

        var wrongFormat = Assert.Throws<InvalidDataException>(() =>
            CloudBackupPackage.ValidateManifest(manifest with { Format = "secrandom-cloud-backup" }));
        Assert.Equal("云端备份清单格式不受支持。", wrongFormat.Message);

        var missing = Assert.Throws<InvalidDataException>(() => CloudBackupPackage.ValidateManifest(null));
        Assert.Equal("云端备份清单内容无效。", missing.Message);

        var noParts = Assert.Throws<InvalidDataException>(() =>
            CloudBackupPackage.ValidateManifest(manifest with { Parts = [] }));
        Assert.Equal("云端备份清单的分片描述无效。", noParts.Message);

        CloudBackupPackage.ValidateManifest(manifest);
    }

    /// <summary>生成的文件名要能被反解出来，别的名字一律不认。</summary>
    [Fact]
    public void TryParseFileName_RoundTripsGeneratedNames()
    {
        const string id = "20261011-093000-abcdef01";

        Assert.True(CloudBackupPackage.TryParseFileName(CloudBackupPackage.BuildManifestName(id),
            out var manifestId, out var isManifest));
        Assert.Equal(id, manifestId);
        Assert.True(isManifest);

        Assert.True(CloudBackupPackage.TryParseFileName(CloudBackupPackage.BuildPartName(id, 3, 12),
            out var partId, out var isPartManifest));
        Assert.Equal(id, partId);
        Assert.False(isPartManifest);

        foreach (var stranger in new[]
                 {
                     CloudBackupFiles.BoardArchiveName, CloudBackupFiles.BoardCloudName,
                     CloudBackupFiles.SettingsFileName, null, string.Empty,
                     "corkboard-board-20261011-093000-abcdef01-p00of03.part",
                     "corkboard-board-20261011-093000-abcdef01-p03of02.part",
                     "corkboard-board-..-manifest.json"
                 })
            Assert.False(CloudBackupPackage.TryParseFileName(stranger, out _, out _));
    }

    /// <summary>备份标识会被拿去拼本地路径，长度、<c>..</c> 与字符集都要卡住。</summary>
    [Fact]
    public void IsSafeBackupId_RejectsTraversalAndLongIds()
    {
        Assert.True(CloudBackupPackage.IsSafeBackupId("20261011-093000-abcdef01"));
        Assert.False(CloudBackupPackage.IsSafeBackupId(".."));
        Assert.False(CloudBackupPackage.IsSafeBackupId("a/../b"));
        Assert.False(CloudBackupPackage.IsSafeBackupId("备份"));
        Assert.False(CloudBackupPackage.IsSafeBackupId(new string('a', 65)));
        Assert.False(CloudBackupPackage.IsSafeBackupId(null));
    }

    /// <summary>清单要能原样写出去再读回来（本机与云端之间就靠这一份 JSON）。</summary>
    [Fact]
    public void SerializeManifest_RoundTripsEveryField()
    {
        var archive = new byte[600];
        Random.Shared.NextBytes(archive);
        var manifest = ManifestOf(archive, 256, "20261011-093000-abcdef01");

        var json = CloudBackupPackage.SerializeManifest(manifest);
        var read = CloudBackupPackage.DeserializeManifest(json);

        Assert.NotNull(read);
        Assert.Contains("\"schema_version\": 1", json);
        Assert.Contains("\"archive_sha256\"", json);
        Assert.Equal(manifest.BackupId, read.BackupId);
        Assert.Equal(manifest.ArchiveBytes, read.ArchiveBytes);
        Assert.Equal(manifest.ArchiveSha256, read.ArchiveSha256);
        Assert.Equal(manifest.PartBytes, read.PartBytes);
        Assert.Equal(manifest.CreatedUtc, read.CreatedUtc);
        Assert.Equal(manifest.Roots, read.Roots);
        Assert.Equal(manifest.Parts.Select(part => part.Name), read.Parts.Select(part => part.Name));
        Assert.Equal(manifest.Parts.Select(part => part.Sha256), read.Parts.Select(part => part.Sha256));
    }

    /// <summary>读不出来的清单一律当「备份损坏」，不往上层抛解析异常。</summary>
    [Fact]
    public void DeserializeManifest_ReturnsNullForBrokenJson()
    {
        Assert.Null(CloudBackupPackage.DeserializeManifest("这不是 JSON"));
        Assert.Null(CloudBackupPackage.DeserializeManifest("{ \"format\": "));
        Assert.Null(CloudBackupPackage.DeserializeManifest(null));
        Assert.Null(CloudBackupPackage.DeserializeManifest("   "));
    }

    /// <summary>缺片提示只列清单里有、云端没有的那些名字。</summary>
    [Fact]
    public void MissingParts_ListsOnlyAbsentNames()
    {
        var archive = new byte[600];
        Random.Shared.NextBytes(archive);
        var manifest = ManifestOf(archive, 256, "20261011-093000-abcdef01");
        var files = new[]
        {
            File(manifest.Parts[0].Name, 1),
            File(manifest.Parts[2].Name, 3)
        };

        Assert.Equal([manifest.Parts[1].Name], CloudBackupPackage.MissingParts(manifest, files));
        Assert.Empty(CloudBackupPackage.MissingParts(manifest,
            manifest.Parts.Select(part => File(part.Name, 1))));
    }

    /// <summary>云端文件列表里认清单最新的那一代，只带它自己的分片；老格式整包单独留着。</summary>
    [Fact]
    public void ReadBoardBackup_PicksTheNewestGenerationAndItsParts()
    {
        const string oldId = "20261010-120000-11111111";
        const string newId = "20261011-093000-22222222";
        var files = new[]
        {
            File(CloudBackupPackage.BuildPartName(newId, 1, 2), 5),
            File(CloudBackupPackage.BuildManifestName(oldId), 1),
            File(CloudBackupPackage.BuildPartName(oldId, 1, 2), 1),
            File(CloudBackupPackage.BuildPartName(oldId, 2, 2), 1),
            File(CloudBackupPackage.BuildManifestName(newId), 6),
            File(CloudBackupFiles.BoardCloudName, 0),
            File(CloudBackupFiles.SettingsFileName, 9)
        };

        var backup = CloudBackupFiles.ReadBoardBackup(files);

        Assert.True(backup.HasAny);
        Assert.Equal(newId, backup.BackupId);
        Assert.Equal(CloudBackupPackage.BuildManifestName(newId), backup.Manifest?.FileName);
        Assert.Equal([CloudBackupPackage.BuildPartName(newId, 1, 2)],
            backup.Parts.Select(part => part.FileName));
        Assert.Equal([CloudBackupFiles.BoardCloudName], backup.Legacy.Select(part => part.FileName));
        Assert.Equal(CloudBackupPackage.BuildManifestName(newId), backup.Newest?.FileName);
        Assert.Equal(CloudBackupFiles.BoardCloudName, backup.NewestLegacy?.FileName);
    }

    /// <summary>只剩老格式整包时也要认出来，恢复走老路径。</summary>
    [Fact]
    public void ReadBoardBackup_FallsBackToLegacyArchive()
    {
        var backup = CloudBackupFiles.ReadBoardBackup(new[] { File(CloudBackupFiles.BoardArchiveName, 1) });

        Assert.True(backup.HasAny);
        Assert.Null(backup.BackupId);
        Assert.Null(backup.Manifest);
        Assert.Empty(backup.Parts);
        Assert.Single(backup.Legacy);
        Assert.NotNull(backup.Newest);
    }

    /// <summary>云端什么都没有（比如刚登录）时返回空的一份，不抛异常。</summary>
    [Fact]
    public void ReadBoardBackup_WithoutBoardFilesIsEmpty()
    {
        var backup = CloudBackupFiles.ReadBoardBackup(new[] { File(CloudBackupFiles.SettingsFileName, 1) });

        Assert.False(backup.HasAny);
        Assert.Null(backup.Newest);
        Assert.Null(backup.NewestLegacy);
    }

    /// <summary>没有清单的分片是上传中断留下的残骸，不能当成可恢复的备份。</summary>
    [Fact]
    public void ReadBoardBackup_IgnoresPartsWithoutManifest()
    {
        const string id = "20261011-093000-33333333";
        var backup = CloudBackupFiles.ReadBoardBackup(new[]
        {
            File(CloudBackupPackage.BuildPartName(id, 1, 2), 1),
            File(CloudBackupPackage.BuildPartName(id, 2, 2), 1)
        });

        Assert.False(backup.HasAny);
        Assert.Null(backup.BackupId);
        Assert.Empty(backup.Parts);
    }
}
