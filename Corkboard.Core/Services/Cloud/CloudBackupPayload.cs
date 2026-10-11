using System.Text;

namespace Corkboard.Core.Services.Cloud;

/// <summary>
///     二进制备份在云端的传输编码。
///     <para>
///         云存储的上传端点会把 <c>file</c> 字段的字节当文本处理：先把原始字节按 UTF-8 解码
///         （非法序列变成 U+FFFD），再按 UTF-8 编码存下来。实测把一个 256 字节的 0x00..0xFF
///         文件用 <c>curl -F</c> 传上去，服务端报 512 字节、下载回来 sha256 与本地不一致，
///         多出来的正是每个非法字节变成一个 3 字节的 U+FFFD——作业板压缩包传上去同样会坏。
///         所以压缩包先 base64 成纯 ASCII 再传，取回来再解回字节，纯文本的设置文件不受影响。
///     </para>
/// </summary>
public static class CloudBackupPayload
{
    /// <summary>不带 BOM 的 UTF-8：备份里全是 ASCII，别白写三个字节的头。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>把二进制内容编成可以安全过一遍云的纯 ASCII 文本。</summary>
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes);

    /// <summary>把云端取回来的 base64 文本解回二进制内容（顺手容忍 BOM 与换行）。</summary>
    public static byte[] Decode(string text) => Convert.FromBase64String(text.Trim('\uFEFF', ' ', '\t', '\r', '\n'));

    /// <summary>把二进制内容编成文本再写成文件（按 UTF-8 写，内容全是 ASCII）。</summary>
    public static async Task WriteEncodedAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(destinationPath, Encode(bytes), Utf8NoBom, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>读回文本、解成二进制再写成文件。</summary>
    public static async Task WriteDecodedAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(destinationPath, Decode(text), cancellationToken).ConfigureAwait(false);
    }
}
