using Corkboard.Core.Helpers;

namespace Corkboard.Core.Tests;

/// <summary>
///     <see cref="AvatarInitialResolver" /> 的单测，用例与上游 SecRandom-C 保持一一对应。
/// </summary>
public class AvatarInitialResolverTests
{
    /// <summary>昵称与账号 ID 都有时取昵称的首字，并忽略两端空白。</summary>
    [Fact]
    public void Resolve_PrefersNameOverId()
    {
        Assert.Equal("A", AvatarInitialResolver.Resolve(" Alice ", "2026001"));
    }

    /// <summary>昵称为空（null / 空串 / 全空白）时退回账号 ID 的首字。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_UsesIdWhenNameIsMissing(string? name)
    {
        Assert.Equal("2", AvatarInitialResolver.Resolve(name, " 2026001 "));
    }

    /// <summary>昵称与账号 ID 都没有可用内容时返回问号，保证头像圆圈里永远有东西显示。</summary>
    [Fact]
    public void Resolve_UsesFallbackWhenNameAndIdAreMissing()
    {
        Assert.Equal("?", AvatarInitialResolver.Resolve(" ", "\t"));
    }
}
