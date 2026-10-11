using Corkboard.Core.Services.Cloud;

namespace Corkboard.Services.Cloud;

/// <summary>
///     思拓创联云存储的文件接口（列表、上传、下载、删除、用量）。
///     <para>
///         授权由 <see cref="Auth.ISectlAuthService" /> 统一处理，本接口不碰令牌；
///         失败一律抛 <see cref="CloudStorageException" />，<c>Message</c> 可直接给用户看。
///     </para>
/// </summary>
public interface ICloudStorageClient
{
    /// <summary>取空间占用（账号总用量 + 本平台占用）。</summary>
    Task<CloudStorageUsage> GetUsageAsync(bool reconcile = false, CancellationToken cancellationToken = default);

    /// <summary>列出账号在本平台下的全部云文件。</summary>
    Task<IReadOnlyList<CloudFileInfo>> ListFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>上传一个文件，返回云端分配的文件 ID（返回体里没有就为 <c>null</c>）。</summary>
    Task<string?> UploadAsync(byte[] content, string fileName, CancellationToken cancellationToken = default);

    /// <summary>把云文件下载到本地路径。</summary>
    Task DownloadAsync(string fileId, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>删除一个云文件。</summary>
    Task DeleteAsync(string fileId, CancellationToken cancellationToken = default);
}
