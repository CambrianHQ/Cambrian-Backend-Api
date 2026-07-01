using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests.Public;

/// <summary>
/// Integration tests for the public, read-only MCP discovery surface (/api/public/*).
/// Verifies 200s, exclusion of drafts/hidden content, absence of storage keys / emails /
/// Stripe fields, pagination limits, query validation, cache headers, canonical URLs,
/// OpenAPI coverage, and the ban on retired positioning terms.
/// </summary>
public sealed class PublicApiTests : IClassFixture<CambrianApiFixture>
{
    private const string SiteBase = "https://app.cambrian.test"; // App:FrontendUrl in the fixture

    private static readonly string[] BannedTerms =
    {
        "marketplace", "licensing marketplace", "license marketplace", "buyout", "exclusive license"
    };

    private readonly CambrianApiFixture _fixture;

    public PublicApiTests(CambrianApiFixture fixture) => _fixture = fixture;

    // ── 200 / shape ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/public/tracks/search")]
    [InlineData("/api/public/creators/search")]
    [InlineData("/api/public/genres")]
    [InlineData("/api/public/trending")]
    [InlineData("/api/public/latest")]
    [InlineData("/api/public/featured-creators")]
    [InlineData("/api/public/stats")]
    [InlineData("/api/public/pricing")]
    [InlineData("/api/public/faq")]
    [InlineData("/api/public/sitemap")]
    [InlineData("/api/public/release-ready")]
    [InlineData("/api/public/authorship")]
    [InlineData("/api/public/creator-guide")]
    public async Task PublicEndpoints_Return200_Anonymously(string url)
    {
        var client = _fixture.CreateClient(); // no auth header
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task TrackDetail_ReturnsPublicSafeShape_WithCanonicalAndRealMetrics()
    {
        var creatorId = await CreateCreatorUserAsync();
        var marker = Unique("title");
        var (id, camb) = await SeedTrackAsync(creatorId, t =>
        {
            t.Title = marker;
            t.Genre = "Hip-Hop";
            t.NonExclusivePriceCents = 1999;
            t.ContentHash = "deadbeef";
            t.Signature = "sig==";
            t.SignedAt = DateTime.UtcNow;
            t.CommercialRightsVerified = true;
        });
        await SeedStreamSessionAsync(id);

        var data = await GetDataAsync($"/api/public/tracks/{camb}");

        Assert.Equal(camb, data.GetProperty("trackId").GetString());
        Assert.Equal(marker, data.GetProperty("title").GetString());
        Assert.Equal($"{SiteBase}/track/{camb}", data.GetProperty("canonicalUrl").GetString());
        Assert.Equal("MusicRecording", data.GetProperty("structuredDataType").GetString());
        Assert.Equal(1999, data.GetProperty("priceCents").GetInt32());
        Assert.True(data.GetProperty("plays").GetInt32() >= 1, "play count should be real/live");
        Assert.Equal("verified", data.GetProperty("provenanceStatus").GetString());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("metaTitle").GetString()));
        Assert.False(string.IsNullOrEmpty(data.GetProperty("metaDescription").GetString()));
        // Audio is exposed only as a proxied stream URL, never the storage key.
        Assert.EndsWith($"/stream/{id}/audio", data.GetProperty("audioPreviewUrl").GetString());
    }

    [Fact]
    public async Task ProvenanceStatus_DefaultsToNone_WhenUnsigned()
    {
        var creatorId = await CreateCreatorUserAsync();
        var (_, camb) = await SeedTrackAsync(creatorId, t => t.Title = Unique("plain"));
        var data = await GetDataAsync($"/api/public/tracks/{camb}");
        Assert.Equal("none", data.GetProperty("provenanceStatus").GetString());
    }

    // ── Exclusion of private / draft / hidden content ──────────────────────────

    [Fact]
    public async Task Search_ExcludesHiddenLimitedAndRemovedTracks()
    {
        var creatorId = await CreateCreatorUserAsync();
        var marker = Unique("vis");

        var (publicId, _) = await SeedTrackAsync(creatorId, t => { t.Title = $"{marker} public"; t.Visibility = "public"; });
        var (hiddenId, _) = await SeedTrackAsync(creatorId, t => { t.Title = $"{marker} hidden"; t.Visibility = "hidden"; });
        var (limitedId, _) = await SeedTrackAsync(creatorId, t => { t.Title = $"{marker} limited"; t.Visibility = "limited"; });
        var (soldId, _) = await SeedTrackAsync(creatorId, t => { t.Title = $"{marker} sold"; t.ExclusiveSold = true; });

        var data = await GetDataAsync($"/api/public/tracks/search?q={marker}&pageSize=50");
        var ids = data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains(publicId.ToString(), ids);
        Assert.DoesNotContain(hiddenId.ToString(), ids);
        Assert.DoesNotContain(limitedId.ToString(), ids);
        Assert.DoesNotContain(soldId.ToString(), ids);
    }

    [Fact]
    public async Task TrackDetail_HiddenTrack_Returns404()
    {
        var creatorId = await CreateCreatorUserAsync();
        var (_, camb) = await SeedTrackAsync(creatorId, t => { t.Title = Unique("hid"); t.Visibility = "hidden"; });

        var res = await _fixture.CreateClient().GetAsync($"/api/public/tracks/{camb}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── Security: no storage keys / emails / Stripe data ───────────────────────

    [Fact]
    public async Task TrackDetail_DoesNotLeakStorageKeys_EmailsOrStripeData()
    {
        var email = $"secret-{Guid.NewGuid():N}@hidden-domain.test";
        var creatorId = await CreateCreatorUserAsync(email);
        var audioKey = $"tracks/secret-master-{Guid.NewGuid():N}.wav";
        var (id, camb) = await SeedTrackAsync(creatorId, t =>
        {
            t.Title = Unique("leak");
            t.AudioUrl = audioKey;
            t.CoverArtUrl = "https://r2-private.internal.example/cambrianaudio/covers/secret.jpg";
        });

        var res = await _fixture.CreateClient().GetAsync($"/api/public/tracks/{camb}");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();

        Assert.DoesNotContain(audioKey, body);                       // raw audio storage key absent
        Assert.DoesNotContain("r2-private.internal.example", body);  // bucket origin stripped
        Assert.DoesNotContain("cambrianaudio", body);                // bucket name stripped
        Assert.DoesNotContain(email, body);                          // email absent
        AssertNoStripeData(body);

        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        Assert.StartsWith($"{SiteBase}/images/", data.GetProperty("imageUrl").GetString());
    }

    [Fact]
    public async Task CreatorProfile_DoesNotLeakEmailOrStripeData()
    {
        var email = $"creator-{Guid.NewGuid():N}@hidden-domain.test";
        var creatorId = await CreateCreatorUserAsync(email);
        var slug = Unique("slug").Replace("_", "-").ToLowerInvariant();
        await SeedCreatorProfileAsync(creatorId, slug, "lofi beats producer");

        var res = await _fixture.CreateClient().GetAsync($"/api/public/creators/{slug}");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();

        Assert.DoesNotContain(email, body);
        AssertNoStripeData(body);

        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        Assert.Equal($"{SiteBase}/creator/{slug}", data.GetProperty("canonicalUrl").GetString());
        // earnings/revenue must never appear in the public stats block
        var stats = data.GetProperty("stats");
        Assert.False(stats.TryGetProperty("totalEarnings", out _));
        Assert.False(stats.TryGetProperty("earnings", out _));
        Assert.False(stats.TryGetProperty("revenue", out _));
    }

    [Fact]
    public async Task Stats_ExposesAuthorshipRecordCount_CountingOnlyIssuedRecords()
    {
        var creatorId = await CreateCreatorUserAsync();
        var (trackId, _) = await SeedTrackAsync(creatorId, t => t.Title = Unique("authorship-stats"));
        var before = await GetDataAsync("/api/public/stats");
        var beforeCount = before.GetProperty("authorshipRecordCount").GetInt32();

        await SeedAuthorshipRecordAsync(trackId, creatorId, status: "issued");
        await SeedAuthorshipRecordAsync(trackId, creatorId, status: "pending_payment");
        await SeedAuthorshipRecordAsync(trackId, creatorId, status: "failed");

        var after = await GetDataAsync("/api/public/stats");
        Assert.Equal(beforeCount + 1, after.GetProperty("authorshipRecordCount").GetInt32());
    }

    [Fact]
    public async Task Pricing_ExposesPlans_WithoutStripePriceIds()
    {
        var res = await _fixture.CreateClient().GetAsync("/api/public/pricing");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();

        AssertNoStripeData(body);
        Assert.DoesNotContain("price_test", body);

        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        var tiers = data.GetProperty("tiers");
        Assert.True(tiers.GetArrayLength() >= 3);
        foreach (var tier in tiers.EnumerateArray())
        {
            Assert.False(tier.TryGetProperty("stripePriceConfigKey", out _));
            Assert.True(tier.TryGetProperty("priceCentsMonthly", out _));
        }
    }

    // ── Pagination limits + query validation ───────────────────────────────────

    [Fact]
    public async Task Search_ClampsPageSizeToMaximum()
    {
        var data = await GetDataAsync("/api/public/tracks/search?pageSize=200");
        Assert.Equal(50, data.GetProperty("pageSize").GetInt32());
        Assert.True(data.GetProperty("items").GetArrayLength() <= 50);
    }

    [Theory]
    [InlineData("/api/public/tracks/search?page=0")]
    [InlineData("/api/public/tracks/search?pageSize=0")]
    [InlineData("/api/public/tracks/search?page=-3")]
    [InlineData("/api/public/creators/search?page=0")]
    [InlineData("/api/public/tracks/search?page=notanumber")]
    public async Task InvalidQueryParams_AreRejected(string url)
    {
        var res = await _fixture.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── Cache headers + canonical URLs ─────────────────────────────────────────

    [Theory]
    [InlineData("/api/public/tracks/search")]
    [InlineData("/api/public/pricing")]
    [InlineData("/api/public/faq")]
    [InlineData("/api/public/stats")]
    public async Task PublicEndpoints_SetPublicCacheHeaders(string url)
    {
        var res = await _fixture.CreateClient().GetAsync(url);
        res.EnsureSuccessStatusCode();
        Assert.NotNull(res.Headers.CacheControl);
        Assert.True(res.Headers.CacheControl!.Public, "Cache-Control should be public");
        Assert.True(res.Headers.CacheControl!.MaxAge.HasValue && res.Headers.CacheControl.MaxAge!.Value.TotalSeconds > 0);
    }

    [Fact]
    public async Task ListAndContentEndpoints_ReturnAbsoluteCanonicalUrls_NoLocalhost()
    {
        foreach (var url in new[] { "/api/public/tracks/search", "/api/public/pricing", "/api/public/faq", "/api/public/stats" })
        {
            var data = await GetDataAsync(url);
            var canonical = data.GetProperty("canonicalUrl").GetString();
            Assert.False(string.IsNullOrEmpty(canonical));
            Assert.StartsWith("https://", canonical!);
            Assert.DoesNotContain("localhost", canonical!, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── OpenAPI coverage ───────────────────────────────────────────────────────

    [Fact]
    public async Task OpenApi_Includes_AllPublicEndpoints()
    {
        var client = _fixture.CreateClient();
        var doc = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var paths = doc.GetProperty("paths");

        foreach (var expected in new[]
        {
            "/api/public/tracks/search", "/api/public/tracks/{trackId}",
            "/api/public/creators/search", "/api/public/creators/{slug}",
            "/api/public/genres", "/api/public/genres/{genre}",
            "/api/public/trending", "/api/public/latest", "/api/public/featured-creators",
            "/api/public/stats", "/api/public/pricing", "/api/public/faq", "/api/public/sitemap",
            "/api/public/release-ready", "/api/public/authorship", "/api/public/creator-guide",
        })
        {
            Assert.True(paths.TryGetProperty(expected, out _), $"OpenAPI is missing public path {expected}");
        }
    }

    // ── Banned positioning terms ───────────────────────────────────────────────

    [Fact]
    public async Task PublicResponses_DoNotContainRetiredPositioningTerms()
    {
        var creatorId = await CreateCreatorUserAsync();
        var slug = Unique("slug").Replace("_", "-").ToLowerInvariant();
        await SeedCreatorProfileAsync(creatorId, slug, "producer");
        await SeedTrackAsync(creatorId, t => t.Title = Unique("term"));

        var urls = new[]
        {
            "/api/public/pricing", "/api/public/faq", "/api/public/release-ready",
            "/api/public/authorship", "/api/public/creator-guide",
            "/api/public/tracks/search", $"/api/public/creators/{slug}", "/api/public/stats",
        };

        var client = _fixture.CreateClient();
        foreach (var url in urls)
        {
            var res = await client.GetAsync(url);
            res.EnsureSuccessStatusCode();
            var body = (await res.Content.ReadAsStringAsync()).ToLowerInvariant();
            foreach (var term in BannedTerms)
                Assert.False(body.Contains(term), $"'{term}' must not appear in {url}");
        }
    }

    // ── Genres ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenreDetail_UnknownGenre_Returns404()
    {
        var res = await _fixture.CreateClient().GetAsync($"/api/public/genres/{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private async Task<JsonElement> GetDataAsync(string url)
    {
        var res = await _fixture.CreateClient().GetAsync(url);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        return json.GetProperty("data").Clone();
    }

    private static void AssertNoStripeData(string body)
    {
        var lower = body.ToLowerInvariant();
        Assert.DoesNotContain("stripe", lower);
        Assert.DoesNotContain("acct_", lower);
        Assert.DoesNotContain("cus_", lower);
        Assert.DoesNotContain("price_", lower);
        Assert.DoesNotContain("\"email\"", lower);
        Assert.DoesNotContain("walletbalance", lower);
    }

    private async Task<string> CreateCreatorUserAsync(string? email = null)
    {
        email ??= $"pub-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email);
        return await _fixture.GetUserIdAsync(email);
    }

    private async Task<(Guid id, string camb)> SeedTrackAsync(string creatorId, Action<Track> configure)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        var track = new Track
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString("N")[..8].ToUpperInvariant()}",
            Title = "Public Track",
            Genre = "Hip-Hop",
            Visibility = "public",
            Status = "available",
            Price = 10m,
            NonExclusivePriceCents = 1000,
            AudioUrl = "tracks/test-beat.mp3",
            CreatorId = creatorId,
            CreatedAt = DateTime.UtcNow,
        };
        configure(track);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return (id, track.CambrianTrackId);
    }

    private async Task SeedStreamSessionAsync(Guid trackId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.StreamSessions.Add(new StreamSession
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            StartedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAuthorshipRecordAsync(Guid trackId, string creatorId, string status)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.AuthorshipRecords.Add(new AuthorshipRecord
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            CreatorId = creatorId,
            ArtistName = "Test Artist",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            IssuedAt = status == "issued" ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedCreatorProfileAsync(string userId, string slug, string bio)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.CreatorProfiles.Add(new CreatorProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Slug = slug,
            Bio = bio,
            Niche = "lofi",
            ProfileImageUrl = "covers/profile.jpg",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
