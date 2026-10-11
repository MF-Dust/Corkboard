using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Corkboard.Core.Services.Cloud;

/// <summary>备份包里的一个分片。</summary>
public sealed record CloudBackupPackagePart(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>备份包的清单：说明这一份备份由哪些分片组成、整包的摘要是什么。</summary>
public sealed record CloudBackupPackageManifest(
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("producer_version")] string ProducerVersion,
    [property: JsonPropertyName("backup_id")] string BackupId,
    [property: JsonPropertyName("archive_name")] string ArchiveName,
    [property: JsonPropertyName("archive_bytes")] long ArchiveBytes,
    [property: JsonPropertyName("archive_sha256")] string ArchiveSha256,
    [property: JsonPropertyName("part_bytes")] int PartBytes,
    [property: JsonPropertyName("created_utc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("roots")] IReadOnlyList<string> Roots,
    [property: JsonPropertyName("parts")] IReadOnlyList<CloudBackupPackagePart> Parts);

/// <summary>
///     云备份的分片包格式：把压缩包切成若干片分别上传，再传一份清单。
/// </summary>
/// <remarks>
///     <para>
///         为什么要分片：这条链路上真正把关请求体大小的是 Appwrite 的 <c>functions/executions</c>
///         <c>body</c> 参数上限 10 MiB（写死在 app 代码里，不受环境变量控制），而 api-proxy 早先还只放 5 MiB
///         （2026-10-11 已对齐到 10 MiB，见 <see cref="MaxUploadBodyBytes" />）；整包上传迟早会撞墙——
///         作业里的图片是以 data URI 嵌在正文里的，几篇带图的作业就能到几十 MB。分片之后单次请求就是几 MB，
///         顺带还能只重传失败的那一片。
///     </para>
///     <para>
///         为什么清单最后传：中途断掉的上传在云端只留下一堆没有清单的分片，看不出来是一份备份，
///         所以恢复只认「清单 + 清单里声明的全部分片」这一种完整形态。
///     </para>
///     <para>
///         校验是两层的：每片 SHA-256 对上清单里的记录，拼起来整包再对一次 <c>archive_sha256</c>，
///         免得云端把某一片悄悄弄坏却拼出一份看起来正常的备份。
///     </para>
/// </remarks>
public static class CloudBackupPackage
{
    /// <summary>清单里的格式标识，用来拒绝别的应用留下的备份。</summary>
    public const string Format = "corkboard-cloud-backup";

    /// <summary>清单版本。字段只增不改时才会 +1。</summary>
    public const int SchemaVersion = 1;

    /// <summary>作业板备份文件的前缀。</summary>
    public const string BoardPrefix = "corkboard-board-";

    /// <summary>分片文件的后缀。</summary>
    public const string PartExtension = ".part";

    /// <summary>清单文件的后缀。</summary>
    public const string ManifestSuffix = "-manifest.json";

    /// <summary>
    ///     每片的原始字节数。3 MiB 的归档 base64 之后约 4 MiB，落在
    ///     <see cref="MaxUploadBodyBytes" /> 之内还有富余（上游 SecRandom 用的是 512 KiB，这里放大是因为
    ///     每片都要单独跑一次云函数执行，片太多会让一次备份拖上几分钟）。
    /// </summary>
    public const int DefaultPartBytes = 3 * 1024 * 1024;

    /// <summary>
    ///     云函数请求体的安全上限：超过就会在网关那一层被打回。实测这条链路上真正的地板是 Appwrite 的
    ///     <c>functions/executions</c> <c>body</c> 参数上限 10 MiB（写死在 app 代码里，不由环境变量控制），
    ///     api-proxy 的 <c>MAX_BODY_BYTES</c> 也已对齐到 10 MiB，超了回干净的 413；这里按 6 MiB 留余量。
    /// </summary>
    public const long MaxUploadBodyBytes = 6L * 1024 * 1024;

    /// <summary>一份备份最多能有多少分片（片号是两位，超过就排不下了）。</summary>
    public const int MaxPartCount = 32;

    /// <summary>base64 之外的 JSON 外壳（client_id、文件名、摘要这些）预留的字节数。</summary>
    public const int JsonEnvelopeOverheadBytes = 1024;

    /// <summary>单次上传的字节上限（客户端守一道，免得拼出请求再被服务端打回）。</summary>
    public const long MaxUploadBytes = 12L * 1024 * 1024;

    /// <summary>
    ///     整包上限。取 <see cref="MaxPartCount" /> × <see cref="DefaultPartBytes" /> = 96 MiB，
    ///     正好把分片数用满，再多也没有片号可以表示（账号总共只有 100 MB，实际上先卡在配额上）。
    /// </summary>
    public const long MaxArchiveBytes = (long)MaxPartCount * DefaultPartBytes;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    /// <summary>造一个备份标识（按时间可读 + 一小段随机，用来区分同一秒里的两次上传）。</summary>
    public static string CreateBackupId(DateTimeOffset now) =>
        $"{now:yyyyMMdd-HHmmss}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}";

    /// <summary>
    ///     备份标识是不是安全的。它会从云端文件名的前缀里被解析出来，还会被拿去拼本地暂存路径名，
    ///     所以长度、<c>..</c> 与字符集都要卡住。
    /// </summary>
    public static bool IsSafeBackupId(string? backupId) =>
        !string.IsNullOrWhiteSpace(backupId) &&
        backupId.Length <= 64 &&
        !backupId.Contains("..", StringComparison.Ordinal) &&
        backupId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    /// <summary>第 <paramref name="index" /> 片（从 1 开始）的云端文件名。</summary>
    public static string BuildPartName(string backupId, int index, int partCount) =>
        $"{BoardPrefix}{backupId}-p{index:D2}of{partCount:D2}{PartExtension}";

    /// <summary>清单的云端文件名。</summary>
    public static string BuildManifestName(string backupId) => $"{BoardPrefix}{backupId}{ManifestSuffix}";

    /// <summary>是不是这个格式的作业板备份文件（分片或清单）。</summary>
    public static bool IsBoardFile(string? fileName) => TryParseFileName(fileName, out _, out _);

    /// <summary>
    ///     从云端文件名里解析备份标识，并判断它是清单还是分片；
    ///     不是这个格式（包括早期那种整包名）就返回 <c>false</c>。
    /// </summary>
    public static bool TryParseFileName(string? fileName, out string backupId, out bool isManifest)
    {
        backupId = string.Empty;
        isManifest = false;

        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.StartsWith(BoardPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = fileName[BoardPrefix.Length..];

        if (rest.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var id = rest[..^ManifestSuffix.Length];
            if (!IsSafeBackupId(id))
                return false;

            backupId = id;
            isManifest = true;
            return true;
        }

        if (!rest.EndsWith(PartExtension, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = rest[..^PartExtension.Length];
        var marker = body.LastIndexOf("-p", StringComparison.Ordinal);
        if (marker <= 0)
            return false;

        var shape = body[(marker + 2)..];
        var separator = shape.IndexOf("of", StringComparison.Ordinal);
        if (separator <= 0 ||
            !int.TryParse(shape[..separator], out var index) ||
            !int.TryParse(shape[(separator + 2)..], out var count))
            return false;

        var parsedId = body[..marker];
        if (index < 1 || count < 1 || count > MaxPartCount || index > count || !IsSafeBackupId(parsedId))
            return false;

        backupId = parsedId;
        return true;
    }

    /// <summary>把归档切成有序的分片；空归档也返回一个空片，免得出现「零个分片」这种形态。</summary>
    public static IReadOnlyList<byte[]> Slice(byte[] archive, int partBytes = DefaultPartBytes)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (partBytes is <= 0 or > DefaultPartBytes)
            throw new ArgumentOutOfRangeException(nameof(partBytes));

        if (archive.Length == 0)
            return [Array.Empty<byte>()];

        var parts = new List<byte[]>((archive.Length + partBytes - 1) / partBytes);
        for (var offset = 0; offset < archive.Length; offset += partBytes)
        {
            var length = Math.Min(partBytes, archive.Length - offset);
            var part = new byte[length];
            Array.Copy(archive, offset, part, 0, length);
            parts.Add(part);
        }

        return parts;
    }

    /// <summary>为一次上传造清单；体积、片数与请求体预算都在这里卡住。</summary>
    public static CloudBackupPackageManifest BuildManifest(string backupId,
        IReadOnlyList<byte[]> parts,
        int partBytes,
        IReadOnlyList<string> roots,
        DateTimeOffset createdUtc,
        string producerVersion,
        string archiveName,
        byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(payload);

        if (!IsSafeBackupId(backupId))
            throw new InvalidDataException("备份标识无效。");
        if (partBytes is <= 0 or > DefaultPartBytes)
            throw new InvalidDataException("备份分片大小无效。");
        if (payload.LongLength > MaxArchiveBytes)
            throw new InvalidDataException($"备份包超过 {CloudStorageFormat.Bytes(MaxArchiveBytes)}，没法上传。");
        if (parts.Count > MaxPartCount)
            throw new InvalidDataException("备份分片太多了。");

        EnsureUploadBodySize(partBytes);

        var entries = new CloudBackupPackagePart[parts.Count];
        for (var i = 0; i < parts.Count; i++)
            entries[i] = new CloudBackupPackagePart(i + 1, BuildPartName(backupId, i + 1, parts.Count),
                parts[i].LongLength, Hash(parts[i]));

        return new CloudBackupPackageManifest(Format, SchemaVersion, producerVersion, backupId, archiveName,
            payload.LongLength, Hash(payload), partBytes, createdUtc, roots, entries);
    }

    /// <summary>清单是不是这个格式、字段有没有被改坏。</summary>
    public static void ValidateManifest(CloudBackupPackageManifest? manifest)
    {
        if (manifest is null)
            throw new InvalidDataException("云端备份清单内容无效。");
        if (!string.Equals(manifest.Format, Format, StringComparison.Ordinal))
            throw new InvalidDataException("云端备份清单格式不受支持。");
        if (manifest.SchemaVersion != SchemaVersion)
            throw new InvalidDataException("云端备份清单版本不受支持。");
        if (manifest.PartBytes is <= 0 or > DefaultPartBytes)
            throw new InvalidDataException("云端备份清单的分片大小无效。");
        if (manifest.ArchiveBytes <= 0 || manifest.ArchiveBytes > MaxArchiveBytes)
            throw new InvalidDataException("云端备份清单的归档长度无效。");
        if (string.IsNullOrWhiteSpace(manifest.ArchiveSha256))
            throw new InvalidDataException("云端备份清单的归档摘要无效。");
        if (manifest.Parts is null || manifest.Parts.Count == 0 || manifest.Parts.Count > MaxPartCount)
            throw new InvalidDataException("云端备份清单的分片描述无效。");

        for (var i = 0; i < manifest.Parts.Count; i++)
        {
            var part = manifest.Parts[i];
            if (part is null || part.Index != i + 1 || part.Length <= 0 ||
                string.IsNullOrWhiteSpace(part.Name) || string.IsNullOrWhiteSpace(part.Sha256))
                throw new InvalidDataException("云端备份清单的分片描述无效。");
        }
    }

    /// <summary>按清单把分片拼回来，逐片与整包各校验一次摘要。</summary>
    public static byte[] MergeAndVerify(CloudBackupPackageManifest manifest, IReadOnlyList<byte[]> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ValidateManifest(manifest);

        if (parts.Count != manifest.Parts.Count)
            throw new InvalidDataException("云端备份的分片数量与清单不一致。");

        var total = 0L;
        for (var i = 0; i < parts.Count; i++)
        {
            var expected = manifest.Parts[i];
            var actual = parts[i];
            if (actual.LongLength != expected.Length ||
                !string.Equals(Hash(actual), expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"云端备份第 {expected.Index} 个分片校验失败。");
            total += actual.LongLength;
        }

        if (total != manifest.ArchiveBytes)
            throw new InvalidDataException("云端备份归档长度校验失败。");

        var merged = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, merged, offset, part.Length);
            offset += part.Length;
        }

        if (!string.Equals(Hash(merged), manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("云端备份归档校验失败。");

        return merged;
    }

    /// <summary>清单里声明、但云端文件列表里找不到的分片名（用来提示「备份不完整」）。</summary>
    public static IReadOnlyList<string> MissingParts(CloudBackupPackageManifest manifest,
        IEnumerable<CloudFileInfo> files)
    {
        var present = files.Select(file => file.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return manifest.Parts
            .Where(part => part is not null && !present.Contains(part.Name))
            .Select(part => part.Name)
            .ToList();
    }

    /// <summary>一片（base64 之后）会不会超出请求体预算；分片大小不对时直接拒绝。</summary>
    public static void EnsureUploadBodySize(int partBytes)
    {
        if (partBytes <= 0)
            throw new InvalidDataException("云端备份分片体积无效。");

        var body = 4L * ((partBytes + 2) / 3) + JsonEnvelopeOverheadBytes;
        if (body > MaxUploadBodyBytes)
            throw new InvalidDataException("云端备份分片体积超过上传限制。");
    }

    /// <summary>大写十六进制的 SHA-256。</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string SerializeManifest(CloudBackupPackageManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);

    /// <summary>读清单；读不出来返回 <c>null</c>（调用方当成「备份损坏」处理）。</summary>
    public static CloudBackupPackageManifest? DeserializeManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<CloudBackupPackageManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
