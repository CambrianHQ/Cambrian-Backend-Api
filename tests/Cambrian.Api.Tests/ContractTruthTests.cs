using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Integration tests that prove the backend matches contract truth for:
/// - Track pricing fields on GET /tracks/{id}
/// - Creator profile/settings GET and PATCH round-trip
/// - Canonical slug routing (Creator.Username fallback)
/// - Payout history serialization
/// - URL validation for social links
/// - Visibility flags (showEarnings, showDownloadStats)
/// </summary>
public sealed class ContractTruthTests : IClassFixture<CambrianApiFixture>, IAsyncLifetime
{
    private readonly CambrianApiFixture _fixture;

    public ContractTruthTests(CambrianApiFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.SetFeatureFlagAsync("creator_storefront", true);
        await _fixture.SetFeatureFlagAsync("creator_profiles", true);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ────────────────────────────────────────────────────────────
    //  Track pricing: GET /tracks/{id} returns correct values
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TrackDetail_LegacyPrice_FallsBackCorrectly()
    {
        // Seed a track with only the legacy Price field (PriceCents = 0)
        var email = $"ct-price-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var trackId = await _fixture.SeedTrackAsync(userId, "Legacy Priced Beat");

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/tracks/{trackId}");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        // Legacy Price is $29.99, so all price tiers should fall back to it
        Assert.Equal(29.99m, data.GetProperty("nonExclusivePrice").GetDecimal());
        Assert.Equal(29.99m, data.GetProperty("exclusivePrice").GetDecimal());
        Assert.Equal(29.99m, data.GetProperty("copyrightBuyoutPrice").GetDecimal());
    }

    [Fact]
    public async Task TrackDetail_CentsPricing_ReturnsCorrectDollars()
    {
        var email = $"ct-cents-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var trackId = await SeedTrackWithPriceCentsAsync(userId, "Cents Beat",
            nonExCents: 1999, exCents: 4999, buyoutCents: 9999);

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/tracks/{trackId}");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.Equal(19.99m, data.GetProperty("price").GetDecimal());
        Assert.Equal(19.99m, data.GetProperty("nonExclusivePrice").GetDecimal());
        Assert.Equal(49.99m, data.GetProperty("exclusivePrice").GetDecimal());
        Assert.Equal(99.99m, data.GetProperty("copyrightBuyoutPrice").GetDecimal());
    }

    [Fact]
    public async Task TrackDetail_IncludesFeeBreakdown()
    {
        var email = $"ct-fee-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var trackId = await SeedTrackWithPriceCentsAsync(userId, "Fee Beat",
            nonExCents: 10000, exCents: 20000, buyoutCents: 50000);

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/tracks/{trackId}");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        // Verify fee fields exist and are non-zero
        Assert.True(data.GetProperty("platformFeePercent").GetDecimal() > 0);
        Assert.True(data.GetProperty("nonExclusivePlatformFee").GetDecimal() > 0);
        Assert.True(data.GetProperty("nonExclusiveCreatorEarnings").GetDecimal() > 0);
        Assert.True(data.GetProperty("exclusivePlatformFee").GetDecimal() > 0);
        Assert.True(data.GetProperty("exclusiveCreatorEarnings").GetDecimal() > 0);
        Assert.True(data.GetProperty("copyrightBuyoutPlatformFee").GetDecimal() > 0);
        Assert.True(data.GetProperty("copyrightBuyoutCreatorEarnings").GetDecimal() > 0);
    }

    [Fact]
    public async Task TrackDetail_IncludesCreatorSlug()
    {
        var email = $"ct-slug-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var username = $"slug-{Guid.NewGuid():N}"[..16];
        await _fixture.SeedCreatorAsync(userId, username);

        var trackId = await _fixture.SeedTrackAsync(userId, "Slug Beat");

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/tracks/{trackId}");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.TryGetProperty("creatorSlug", out _));
    }

    [Fact]
    public async Task CreatorTrackEdit_ReturnsDollarAndCentPricing()
    {
        var email = $"ct-edit-{Guid.NewGuid():N}@test.com";
        var username = $"edit-{Guid.NewGuid():N}"[..16];
        var client = await CreateCreatorClientAsync(email, username: username);
        var userId = await _fixture.GetUserIdAsync(email);

        var trackId = await SeedTrackWithPriceCentsAsync(userId, "Editable Beat",
            nonExCents: 999, exCents: 4999, buyoutCents: 19999);

        var res = await client.PutAsJsonAsync($"/creator/tracks/{trackId}", new
        {
            nonExclusivePriceCents = 999,
            exclusivePriceCents = 4999,
            copyrightBuyoutPriceCents = 19999
        });
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.Equal(9.99m, data.GetProperty("price").GetDecimal());
        Assert.Equal(9.99m, data.GetProperty("nonExclusivePrice").GetDecimal());
        Assert.Equal(49.99m, data.GetProperty("exclusivePrice").GetDecimal());
        Assert.Equal(199.99m, data.GetProperty("copyrightBuyoutPrice").GetDecimal());
        Assert.Equal(999, data.GetProperty("nonExclusivePriceCents").GetInt32());
        Assert.Equal(4999, data.GetProperty("exclusivePriceCents").GetInt32());
        Assert.Equal(19999, data.GetProperty("copyrightBuyoutPriceCents").GetInt32());
    }

    [Fact]
    public async Task UserProfile_TracksIncludeDollarPricingAliases()
    {
        var email = $"ct-user-prices-{Guid.NewGuid():N}@test.com";
        var username = $"prices-{Guid.NewGuid():N}"[..16];
        var client = await CreateCreatorClientAsync(email, username: username);
        var userId = await _fixture.GetUserIdAsync(email);

        await SeedTrackWithPriceCentsAsync(userId, "Profile Beat",
            nonExCents: 999, exCents: 4999, buyoutCents: 19999);

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/users/{username}");
        res.EnsureSuccessStatusCode();

        var track = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data")
            .GetProperty("tracks")[0];

        Assert.Equal(9.99m, track.GetProperty("price").GetDecimal());
        Assert.Equal(9.99m, track.GetProperty("nonExclusivePrice").GetDecimal());
        Assert.Equal(49.99m, track.GetProperty("exclusivePrice").GetDecimal());
        Assert.Equal(199.99m, track.GetProperty("copyrightBuyoutPrice").GetDecimal());
        Assert.Equal(999, track.GetProperty("nonExclusivePriceCents").GetInt32());
        Assert.Equal(4999, track.GetProperty("exclusivePriceCents").GetInt32());
        Assert.Equal(19999, track.GetProperty("copyrightBuyoutPriceCents").GetInt32());
    }

    // ────────────────────────────────────────────────────────────
    //  Profile/settings GET + PATCH round-trip
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProfileSettings_GetPatchRoundTrip()
    {
        var email = $"ct-rt-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);

        // Create profile
        var slug = $"rt-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Round trip test",
            showEarnings = false,
            showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        // GET — verify initial state
        var getRes = await client.GetAsync("/creator-profile/me");
        getRes.EnsureSuccessStatusCode();
        var getData = (await getRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.False(getData.GetProperty("showEarnings").GetBoolean());
        Assert.False(getData.GetProperty("showDownloadStats").GetBoolean());

        // PATCH — toggle both on
        var patchRes = await client.PatchAsJsonAsync("/creator-profile/me/settings", new
        {
            showEarnings = true,
            showDownloadStats = true,
        });
        patchRes.EnsureSuccessStatusCode();
        var patchData = (await patchRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(patchData.GetProperty("showEarnings").GetBoolean());
        Assert.True(patchData.GetProperty("showDownloadStats").GetBoolean());

        // GET again — verify PATCH persisted
        var getRes2 = await client.GetAsync("/creator-profile/me");
        getRes2.EnsureSuccessStatusCode();
        var getData2 = (await getRes2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(getData2.GetProperty("showEarnings").GetBoolean());
        Assert.True(getData2.GetProperty("showDownloadStats").GetBoolean());
    }

    [Fact]
    public async Task ProfileSettings_PartialPatch_OnlyChangesSpecifiedFields()
    {
        var email = $"ct-pp-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);

        var slug = $"pp-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Partial patch",
            showEarnings = true,
            showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        // PATCH only showDownloadStats
        var patchRes = await client.PatchAsJsonAsync("/creator-profile/me/settings", new
        {
            showDownloadStats = true,
        });
        patchRes.EnsureSuccessStatusCode();
        var patchData = (await patchRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        // showEarnings should remain true (unchanged)
        Assert.True(patchData.GetProperty("showEarnings").GetBoolean());
        Assert.True(patchData.GetProperty("showDownloadStats").GetBoolean());
    }

    [Fact]
    public async Task ProfileSettings_PatchWithoutProfile_Returns404()
    {
        var email = $"ct-noprof-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);

        var res = await client.PatchAsJsonAsync("/creator-profile/me/settings", new
        {
            showEarnings = true,
        });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task ProfileGetMe_IncludesDisplayNameAndUsername()
    {
        var email = $"ct-dn-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var username = $"dn-{Guid.NewGuid():N}"[..16];
        await _fixture.SeedCreatorAsync(userId, username, "My Display Name");

        var slug = $"dn-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "DisplayName test",
            showEarnings = false,
            showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        var res = await client.GetAsync("/creator-profile/me");
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.True(data.TryGetProperty("displayName", out _));
        Assert.True(data.TryGetProperty("username", out _));
        Assert.Equal(username, data.GetProperty("username").GetString());
    }

    [Fact]
    public async Task ProfilePublic_IncludesDisplayNameAndUsername()
    {
        var email = $"ct-pub-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var username = $"pub-{Guid.NewGuid():N}"[..16];
        await _fixture.SeedCreatorAsync(userId, username, "Public Name");

        var slug = $"pub-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Public test",
            showEarnings = false,
            showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}");
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.True(data.TryGetProperty("displayName", out _));
        Assert.True(data.TryGetProperty("username", out _));
    }

    // ────────────────────────────────────────────────────────────
    //  Canonical slug routing (Creator.Username fallback)
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SlugFallback_ResolvesCreatorUsernameForProfile()
    {
        var email = $"ct-sf-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var username = $"sf-{Guid.NewGuid():N}"[..16];
        await _fixture.SeedCreatorAsync(userId, username);

        // Create profile with a DIFFERENT slug
        var profileSlug = $"different-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug = profileSlug,
            bio = "Slug fallback test",
            showEarnings = false,
            showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        // GET by Creator.Username should still find the profile
        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{username}");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(profileSlug, data.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task SlugFallback_StorefrontResolvesCreatorUsername()
    {
        var email = $"ct-sfs-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var username = $"sfs-{Guid.NewGuid():N}"[..16];
        await _fixture.SeedCreatorAsync(userId, username);

        var profileSlug = $"sfslug-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug = profileSlug,
            bio = "Storefront fallback",
            showEarnings = false,
            showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        await _fixture.SeedTrackAsync(userId, "Fallback Beat");

        // GET storefront by Creator.Username (not profile slug)
        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{username}/storefront");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.TryGetProperty("profile", out _));
        Assert.True(data.TryGetProperty("tracks", out _));
    }

    [Fact]
    public async Task SlugFallback_UnknownSlug_Still404()
    {
        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync("/creator-profile/totally-nonexistent-slug-xyz");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ────────────────────────────────────────────────────────────
    //  URL validation for social links
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProfilePut_RejectsInvalidSocialLinkUrl()
    {
        var email = $"ct-url-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);

        var slug = $"url-{Guid.NewGuid():N}"[..20];
        var res = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "URL test",
            showEarnings = false,
            showDownloadStats = false,
            socialLinks = new[]
            {
                new { platform = "twitter", url = "not-a-url" }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ProfilePut_AcceptsValidSocialLinkUrl()
    {
        var email = $"ct-vurl-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);

        var slug = $"vurl-{Guid.NewGuid():N}"[..20];
        var res = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Valid URL test",
            showEarnings = false,
            showDownloadStats = false,
            socialLinks = new[]
            {
                new { platform = "twitter", url = "https://twitter.com/user" },
                new { platform = "spotify", url = "https://open.spotify.com/artist/123" }
            }
        });

        res.EnsureSuccessStatusCode();
    }

    // ────────────────────────────────────────────────────────────
    //  Payout history serialization
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayoutHistory_IncludesTimestampsAndFailureReason()
    {
        var email = $"ct-payout-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        // Seed payout records directly
        await SeedPayoutAsync(userId, 5000, "completed", failureReason: null);
        await SeedPayoutAsync(userId, 3000, "failed", failureReason: "Stripe transfer error: insufficient funds");

        var res = await client.GetAsync("/payouts/history");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.True(data.GetArrayLength() >= 2);

        // Check that timestamps are present on all records
        foreach (var payout in data.EnumerateArray())
        {
            Assert.True(payout.TryGetProperty("requestedAt", out _));
            Assert.True(payout.TryGetProperty("status", out _));
            Assert.True(payout.TryGetProperty("amount", out _));
        }

        // Find the failed payout and verify failureReason is serialized
        var failed = data.EnumerateArray().First(p =>
            p.GetProperty("status").GetString() == "failed");
        Assert.True(failed.TryGetProperty("failureReason", out var reason));
        Assert.False(string.IsNullOrEmpty(reason.GetString()));
    }

    // ────────────────────────────────────────────────────────────
    //  Visibility flags round-trip
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task VisibilityFlags_StorefrontRespects_ShowEarnings()
    {
        var email = $"ct-vis-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var slug = $"vis-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Visibility test",
            showEarnings = true,
            showDownloadStats = true,
        })).EnsureSuccessStatusCode();

        var trackId = await _fixture.SeedTrackAsync(userId, "Vis Beat");
        await SeedCompletedPurchaseAsync(userId, trackId, 2000);

        // Verify earnings visible
        var publicClient = _fixture.CreateClient();
        var res1 = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res1.EnsureSuccessStatusCode();
        var stats1 = (await res1.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("stats");
        Assert.True(stats1.GetProperty("totalEarnings").GetDecimal() > 0);

        // Toggle off via PATCH
        var patchRes = await client.PatchAsJsonAsync("/creator-profile/me/settings", new
        {
            showEarnings = false,
        });
        patchRes.EnsureSuccessStatusCode();

        // Verify earnings now hidden
        var res2 = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res2.EnsureSuccessStatusCode();
        var stats2 = (await res2.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("stats");
        Assert.Equal(0m, stats2.GetProperty("totalEarnings").GetDecimal());
    }

    // ────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────

    private async Task<HttpClient> CreateCreatorClientAsync(string email, string? username = null)
    {
        var password = "Test1234!@";
        await _fixture.RegisterUserAsync(email, password);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email);
            user.Tier = "creator";
            user.Role = "Creator";
            // Set a real username so [RequireUsername] passes
            var resolvedUsername = username ?? $"ct-{Guid.NewGuid():N}"[..16];
            user.UserName = resolvedUsername;
            user.NormalizedUserName = resolvedUsername.ToUpperInvariant();
            await db.SaveChangesAsync();
        }

        var client = _fixture.CreateClient();
        var loginRes = await client.PostAsJsonAsync("/auth/login", new { email, password });
        loginRes.EnsureSuccessStatusCode();
        var json = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Guid> SeedTrackWithPriceCentsAsync(
        string creatorId, string title, int nonExCents, int exCents, int buyoutCents)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
            Title = title,
            Price = 0m, // Legacy field — should NOT be used when cents are set
            NonExclusivePriceCents = nonExCents,
            ExclusivePriceCents = exCents,
            CopyrightBuyoutPriceCents = buyoutCents,
            LicenseType = "standard",
            AudioUrl = "tracks/test.mp3",
            CreatorId = creatorId,
            Genre = "Hip-Hop",
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedPayoutAsync(string creatorId, int amountCents, string status, string? failureReason)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.Payouts.Add(new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            AmountCents = amountCents,
            Status = status,
            RequestedAt = DateTime.UtcNow.AddHours(-1),
            CompletedAt = status == "completed" ? DateTime.UtcNow : null,
            FailureReason = failureReason,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedCompletedPurchaseAsync(string creatorId, Guid trackId, int amountCents)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var buyerId = "buyer-" + Guid.NewGuid().ToString("N")[..8];
        db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = buyerId,
            TrackId = trackId,
            AmountCents = amountCents,
            Status = "completed",
            LicenseType = "non-exclusive",
            PaymentMethod = "stripe",
            CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
