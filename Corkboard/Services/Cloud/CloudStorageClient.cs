using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Corkboard.Core;
using Corkboard.Core.Services.Cloud;
using Corkboard.Services.Auth;
using Microsoft.Extensions.Logging;

namespace Corkboard.Services.Cloud;

/// <summary>
///     思拓创联云存储的 HTTP 实现。
///     <para>
///         授权请求全部经 <see cref="ISectlAuthService.SendAuthorizedAsync" /> 发出（它会负责刷新令牌
///         与拒绝后重试），云存储的 <c>client_id</c> 用平台标识而不是 OAuth 客户端 ID。
///     </para>
/// </summary>
public sealed class CloudStorageClient(
    ISectlAuthService auth,
    IHttpClientFactory httpClientFactory,
    ILogger<CloudStorageClient>? logger = null) : ICloudStorageClient
{
    /// <summary>列表分页大小（服务端上限 1000，取小一点省流量）。</summary>
    private const int PageSize = 100;

    /// <summary>最多翻多少页，避免服务端 <c>has_more</c> 异常时死循环。</summary>
    private const int MaxPages = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static string PlatformId => GlobalConstants.PlatformId;

    public async Task<CloudStorageUsage> GetUsageAsync(bool reconcile = false, CancellationToken cancellationToken = default)
    {
        var url = $"{SectlAuthEndpoints.CloudUsageUrl}?client_id={Uri.EscapeDataString(PlatformId)}";
        if (reconcile)
            url += "&reconcile=1";

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return CloudStorageUsage.Parse(body);
    }

    public async Task<IReadOnlyList<CloudFileInfo>> ListFilesAsync(CancellationToken cancellationToken = default)
    {
        var files = new List<CloudFileInfo>();
        var offset = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{SectlAuthEndpoints.CloudFilesUrl}?client_id={Uri.EscapeDataString(PlatformId)}" +
                      $"&limit={PageSize}&offset={offset}";
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var batch = CloudFileInfo.ParseList(body);
            files.AddRange(batch);

            if (batch.Count == 0 || !HasMore(body))
                break;

            offset += batch.Count;
        }

        return files;
    }

    /// <summary>
    ///     上传一份文件。<paramref name="content" /> 直接给字节，用 base64 装进 JSON 体里传。
    ///     <para>
    ///         为什么不用 multipart：服务端只认带引号的字段名（手工拼引号才通），而且云存储会把收到的
    ///     字节按 UTF-8 解码一遍、二进制走 multipart 会被弄坏。对方的 base64 JSON 通道没有这两个毛病。
    ///     </para>
    /// </summary>
    public async Task<string?> UploadAsync(byte[] content, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.LongLength > CloudBackupPackage.MaxUploadBytes)
        {
            throw new CloudStorageException("payload_too_large",
                $"这次要传 {CloudStorageFormat.Bytes(content.LongLength)}，" +
                $"超过单次上传上限 {CloudStorageFormat.Bytes(CloudBackupPackage.MaxUploadBytes)}");
        }

        var payload = new UploadRequest(PlatformId, new UploadFile(fileName, "application/octet-stream",
            content.LongLength, "data:application/octet-stream;base64," + Convert.ToBase64String(content)));

        using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, SectlAuthEndpoints.CloudUploadUrl)
                {
                    Content = JsonContent.Create(payload, options: JsonOptions)
                },
                cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        logger?.LogInformation("已上传云备份 {FileName}（{Size} 字节）。", fileName, content.LongLength);
        return ReadString(body, "file_id");
    }

    /// <summary>上传请求体（<c>POST /api/cloud/upload</c> 的 base64 形态）。</summary>
    private sealed record UploadRequest(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("file")] UploadFile File);

    /// <summary>上传请求体里的文件条目。</summary>
    private sealed record UploadFile(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("data")] string Data);

    public async Task DownloadAsync(string fileId, string destinationPath, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, SectlAuthEndpoints.CloudFileDownloadUrl(fileId)),
                cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var downloadUrl = ReadString(body, "download_url");
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new CloudStorageException("invalid_response", "云端没有返回下载地址，请稍后再试");

        // 临时下载地址自带签名，不能再带 Authorization 头，所以走独立的无名客户端。
        using var http = httpClientFactory.CreateClient();
        using var download = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!download.IsSuccessStatusCode)
            throw new CloudStorageException("download_failed", "备份文件下载失败，请稍后再试");

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var file = File.Create(destinationPath);
        await download.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new { client_id = PlatformId }, JsonOptions);
        using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Delete, SectlAuthEndpoints.CloudFileUrl(fileId))
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>发一个带授权的请求，非 2xx 一律转成带中文说明的 <see cref="CloudStorageException" />。</summary>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        var response = await auth.SendAuthorizedAsync(createRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return response;

        var status = response.StatusCode;
        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            logger?.LogDebug(exception, "读取云存储错误响应失败。");
        }
        finally
        {
            response.Dispose();
        }

        logger?.LogDebug("云存储请求失败：HTTP {Status} {Body}", (int)status, body);
        throw ToException(status, body);
    }

    private static CloudStorageException ToException(HttpStatusCode status, string? body)
    {
        var error = CloudStorageError.TryParse(body);
        var code = error?.Code ?? status switch
        {
            HttpStatusCode.Unauthorized => "unauthorized",
            HttpStatusCode.Forbidden => "forbidden",
            HttpStatusCode.TooManyRequests => "rate_limited",
            _ => $"http_{(int)status}"
        };

        return new CloudStorageException(code, Describe(code, error?.Description));
    }

    /// <summary>把接口错误码翻成用户能看懂的一句话（服务端的说明只在没把握时才透传）。</summary>
    private static string Describe(string code, string? description) => code switch
    {
        "unauthorized" or "invalid_token" or "access_token_revoked" => "登录状态已失效，请重新登录",
        "insufficient_scope" => "当前登录缺少云存储权限，请重新登录",
        "cloud_service_disabled" => "思拓创联还没给这个平台开云服务",
        "storage_exceeded" => "云端空间不够了，先清理一些云文件",
        "limit_exceeded" => "云文件数量已达上限",
        "file_not_found" or "not_found" => "云端找不到这个文件，可能已被删除",
        "rate_limited" => "请求太频繁了，稍等一会儿再试",
        "forbidden" => "这个账号没有云存储权限",
        "invalid_request" => "云端拒绝了这次请求",
        _ => string.IsNullOrWhiteSpace(description) ? "云端暂时不可用，稍后再试" : description
    };

    private static string? ReadString(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasMore(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("has_more", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
