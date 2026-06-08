using Cambrian.Api.Infrastructure;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// B1 regression: the static-file guard must serve public creator images (covers, avatars,
/// banners) and block only uploaded audio. Previously it allow-listed "covers" only, so every
/// uploaded avatar and banner was refused.
/// </summary>
public sealed class StaticUploadPolicyTests
{
    [Theory]
    [InlineData("/app/wwwroot/uploads/covers/abc.jpg")]
    [InlineData("/app/wwwroot/uploads/avatars/abc.png")]
    [InlineData("/app/wwwroot/uploads/banners/abc.png")]
    [InlineData(@"C:\site\wwwroot\uploads\avatars\abc.png")]
    [InlineData(@"C:\site\wwwroot\uploads\banners\abc.webp")]
    public void Serves_PublicImageUploads(string physicalPath)
        => Assert.False(StaticUploadPolicy.ShouldBlock(physicalPath));

    [Theory]
    [InlineData("/app/wwwroot/uploads/tracks/song.mp3")]
    [InlineData("/app/wwwroot/uploads/audio/song.mp3")]
    [InlineData(@"C:\site\wwwroot\uploads\tracks\song.mp3")]
    public void Blocks_UploadedAudio(string physicalPath)
        => Assert.True(StaticUploadPolicy.ShouldBlock(physicalPath));

    [Theory]
    [InlineData("/app/wwwroot/index.html")]
    [InlineData("/app/wwwroot/images/logo.png")]
    [InlineData(null)]
    [InlineData("")]
    public void DoesNotBlock_NonUploads(string? physicalPath)
        => Assert.False(StaticUploadPolicy.ShouldBlock(physicalPath));
}
