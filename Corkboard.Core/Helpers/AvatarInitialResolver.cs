using System.Globalization;

namespace Corkboard.Core.Helpers;

/// <summary>
///     解析头像缺失时替代显示的昵称首字。
/// </summary>
/// <remarks>
///     移植自上游 SecRandom-C 的同名工具（<c>SecRandom/Helpers/AvatarInitialResolver.cs</c>），
///     逻辑与上游保持一致，避免两端在同一个账号上显示出不同的首字。
/// </remarks>
public static class AvatarInitialResolver
{
    /// <summary>
    ///     取用于头像占位的首字：优先用昵称，昵称为空时退回账号 ID，都为空时返回 <c>?</c>。
    /// </summary>
    /// <param name="name">优先使用的昵称。</param>
    /// <param name="id">昵称为空时退回使用的账号 ID。</param>
    /// <returns>单个文本元素（可能是 emoji 的一个码位簇），任何输入下都不会为 null 或空串。</returns>
    public static string Resolve(string? name, string? id)
    {
        // 用 GetNextTextElement 而不是 [0]：昵称以 emoji 开头时，按码位切会把代理对切开，显示出半个方框。
        var displayText = string.IsNullOrWhiteSpace(name) ? id : name;
        if (string.IsNullOrWhiteSpace(displayText)) return "?";

        return StringInfo.GetNextTextElement(displayText.Trim());
    }
}
