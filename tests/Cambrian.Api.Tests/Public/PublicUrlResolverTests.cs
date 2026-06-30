using Cambrian.Application.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Cambrian.Api.Tests.Public;

/// <summary>
/// Unit tests for <see cref="PublicUrlResolver"/> — the single source of the public API's
/// canonical and media URLs. Proves URLs are built from configuration (never the request
/// host), so production output contains no localhost and no raw bucket origins.
/// </summary>
public sealed class PublicUrlResolverTests
{
    private static PublicUrlResolver Build(string? frontend, string? api = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = frontend,
                ["App:ApiBaseUrl"] = api,
            })
            .Build();
        return new PublicUrlResolver(config);
    }

    [Fact]
    public void ProductionConfig_ProducesHttpsCanonicalUrls_NoLocalhost()
    {
        var r = Build("https://cambrianmusic.com");

        Assert.Equal("https://cambrianmusic.com/track/CAMB-TRK-1", r.TrackUrl("CAMB-TRK-1"));
        Assert.Equal("https://cambrianmusic.com/creator/dj-nova", r.CreatorUrl("dj-nova"));
        Assert.Equal("https://cambrianmusic.com/genres/hip-hop", r.GenreUrl("hip-hop"));

        foreach (var url in new[] { r.TrackUrl("x"), r.CreatorUrl("x"), r.GenreUrl("x"), r.SiteUrl("pricing"), r.AudioStreamUrl("x") })
        {
            Assert.StartsWith("https://", url);
            Assert.DoesNotContain("localhost", url);
        }
    }

    [Fact]
    public void EmptyConfig_FallsBackToHttpsDefault_NeverLocalhost()
    {
        var r = Build(frontend: "", api: "");
        var url = r.TrackUrl("CAMB-TRK-9");
        Assert.StartsWith("https://", url);
        Assert.DoesNotContain("localhost", url);
    }

    [Fact]
    public void ImageUrl_StripsBucketOrigin_AndProxiesThroughImages()
    {
        var r = Build("https://cambrianmusic.com", "https://api.cambrianmusic.com");

        // Absolute R2/S3 URL → bucket origin + bucket name stripped, proxied through /images/.
        var fromAbsolute = r.ImageUrl("https://r2-private.example/cambrianaudio/covers/abc.jpg");
        Assert.Equal("https://api.cambrianmusic.com/images/covers/abc.jpg", fromAbsolute);
        Assert.DoesNotContain("r2-private.example", fromAbsolute!);
        Assert.DoesNotContain("cambrianaudio", fromAbsolute!);

        // Bare object key → proxied through /images/ on the API base.
        Assert.Equal("https://api.cambrianmusic.com/images/covers/x.jpg", r.ImageUrl("covers/x.jpg"));
    }

    [Fact]
    public void ImageUrl_NullOrEmpty_ReturnsNull()
    {
        var r = Build("https://cambrianmusic.com");
        Assert.Null(r.ImageUrl(null));
        Assert.Null(r.ImageUrl(""));
    }

    [Fact]
    public void ApiBaseUrl_DefaultsToSiteUrl_WhenUnset()
    {
        var r = Build("https://cambrianmusic.com");
        Assert.StartsWith("https://cambrianmusic.com/stream/", r.AudioStreamUrl("t1"));
    }

    [Theory]
    [InlineData("Hip-Hop", "hip-hop")]
    [InlineData("R&B / Soul", "r-b-soul")]
    [InlineData("  Lo-Fi  ", "lo-fi")]
    public void Slugify_ProducesUrlSafeSlugs(string input, string expected)
    {
        var r = Build("https://cambrianmusic.com");
        Assert.Equal(expected, r.Slugify(input));
    }
}
