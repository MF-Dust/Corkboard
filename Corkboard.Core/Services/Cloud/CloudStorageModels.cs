using System.Globalization;
using System.Text.Json;

namespace Corkboard.Core.Services.Cloud;

/// <summary>云端容量与文件条的模型，以及接口返回体的解析。</summary>
public sealed record CloudStorageUsage(
    long UsedStorage,
    long TotalStorage,
    long AvailableStorage,
    int Percentage,
    int FileCount,
    long PlatformUsedStorage,
    int PlatformFileCount,
    string UsedText,
    string TotalText,
    string AvailableText,
    string PlatformUsedText)
{
    /// <summary>还没取到数据时的空用量。</summary>
    public static CloudStorageUsage Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, CloudStorageFormat.Bytes(0), CloudStorageFormat.Bytes(0),
        CloudStorageFormat.Bytes(0), CloudStorageFormat.Bytes(0));

    /// <summary>解析 <c>GET /api/cloud/storage/usage</c> 的返回体。</summary>
    public static CloudStorageUsage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var used = GetInt64(root, "used_storage");
        var total = GetInt64(root, "total_storage");
        var available = GetInt64(root, "available_storage");
        var platformUsed = GetInt64(root, "platform_used_storage");

        return new CloudStorageUsage(
            used,
            total,
            available,
            (int)GetInt64(root, "percentage"),
            (int)GetInt64(root, "file_count"),
            platformUsed,
            (int)GetInt64(root, "platform_file_count"),
            GetText(root, "used_storage_formatted") ?? CloudStorageFormat.Bytes(used),
            GetText(root, "total_storage_formatted") ?? CloudStorageFormat.Bytes(total),
            GetText(root, "available_storage_formatted") ?? CloudStorageFormat.Bytes(available),
            GetText(root, "platform_used_storage_formatted") ?? CloudStorageFormat.Bytes(platformUsed));
    }

    internal static long GetInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    internal static string? GetText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>云端的一个文件（<c>GET /api/cloud/files</c> 里的一条）。</summary>
public sealed record CloudFileInfo(string FileId, string FileName, long Size, DateTimeOffset? UpdatedAt)
{
    /// <summary>体积的可读写法。</summary>
    public string SizeText => CloudStorageFormat.Bytes(Size);

    /// <summary>解析文件列表返回体。</summary>
    public static IReadOnlyList<CloudFileInfo> ParseList(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<CloudFileInfo>();
        foreach (var item in files.EnumerateArray())
        {
            var fileId = CloudStorageUsage.GetText(item, "file_id");
            var name = CloudStorageUsage.GetText(item, "filename");
            if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(name))
                continue;

            result.Add(new CloudFileInfo(fileId, name, CloudStorageUsage.GetInt64(item, "size"), GetTime(item, "updated_at")));
        }

        return result;
    }

    private static DateTimeOffset? GetTime(JsonElement element, string name)
    {
        var text = CloudStorageUsage.GetText(element, name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }
}

/// <summary>云端返回的错误体（<c>{ "error": "...", "error_description": "..." }</c>）。</summary>
public sealed record CloudStorageError(string Code, string Description)
{
    /// <summary>尝试从响应体里读出错误；不是错误体就返回 <c>null</c>。</summary>
    public static CloudStorageError? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var code = CloudStorageUsage.GetText(document.RootElement, "error");
            if (string.IsNullOrEmpty(code))
                return null;

            var description = CloudStorageUsage.GetText(document.RootElement, "error_description");
            return new CloudStorageError(code, string.IsNullOrWhiteSpace(description) ? code : description);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>云存储调用失败。<see cref="Exception.Message" /> 是可以直接给用户看的中文说明。</summary>
public sealed class CloudStorageException : Exception
{
    public CloudStorageException(string code, string description) : base(description) => Code = code;

    /// <summary>接口返回的错误码（网络层失败时是本地码）。</summary>
    public string Code { get; }
}

/// <summary>体积的可读写法（与云端 <c>*_formatted</c> 同一个口径：1024 进制、最多一位小数）。</summary>
public static class CloudStorageFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var text = value < 10 && unit > 0
            ? value.ToString("0.#", CultureInfo.InvariantCulture)
            : Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        return $"{text} {Units[unit]}";
    }
}
