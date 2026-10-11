using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    public async Task<string?> UploadAsync(string filePath, string fileName, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(() =>
        {
            var content = new MultipartFormDataContent();
            AddPart(content, new StringContent(PlatformId), "client_id");
            AddPart(content, new StringContent(fileName), "filename");
            AddPart(
                content,
                new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
                "file",
                fileName);
            return new HttpRequestMessage(HttpMethod.Post, SectlAuthEndpoints.CloudUploadUrl) { Content = content };
        }, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        logger?.LogInformation("已上传云备份 {FileName}（{Size} 字节）。", fileName, bytes.Length);
        return ReadString(body, "file_id");
    }

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

    /// <summary>加一个 multipart 字段。</summary>
    /// <remarks>
    /// <c>MultipartFormDataContent</c> 默认把 <c>name</c>/<c>filename</c> 写成不带引号的形式，还会多带一个
    /// <c>filename*=utf-8''</c>；云端解析不了这种形状，只回 <c>400 invalid_request / No file data received</c>，
    /// 所以这里手动把两个参数都带上引号，与服务端实际接受的形状保持一致。
    /// </remarks>
    private static void AddPart(MultipartFormDataContent content, HttpContent part, string name, string? fileName = null)
    {
        var disposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };
        if (fileName is not null)
            disposition.FileName = $"\"{fileName}\"";

        part.Headers.ContentDisposition = disposition;
        content.Add(part);
    }

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
