using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Regression;

/// <summary>
/// Launch-gate guard for spec item #15: the retired licensing / marketplace model
/// (exclusive + copyright-buyout pricing, "exclusive_buyout" license options,
/// marketplace copy) must NOT leak through any public API response.
///
/// Cambrian is a creator platform (tips / subscriptions / credits / authorship),
/// not a licensing marketplace. The track's standard price is still exposed as
/// <c>price</c> / <c>nonExclusivePrice</c> (intentionally retained); only the
/// exclusive/buyout/marketplace surface is forbidden.
/// </summary>
[Trait("Category", "Critical")]
public sealed class LicensingLeakRegressionTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public LicensingLeakRegressionTests(CambrianApiFixture fixture) => _fixture = fixture;

    // JSON property names that must never appear on a public track DTO.
    private static readonly string[] ForbiddenKeys =
    {
        "exclusivePrice", "copyrightBuyoutPrice", "exclusivePlatformFee",
        "exclusiveCreatorEarnings", "copyrightBuyoutPlatformFee",
        "copyrightBuyoutCreatorEarnings", "exclusiveSold", "isCopyrightTransferred",
        "licenseType", "exclusivity"
    };

    // Marketplace/licensing copy + enum values that must never appear in a response body.
    private static readonly string[] ForbiddenCopy =
    {
        "exclusive_buyout", "exclusive_sold", "copyright_transferred",
        "copyright transfer", "copyright ownership", "buyout",
        "marketplace", "exclusive license", "exclusive buyout"
    };

    private async Task<(string creatorId, string email)> SeedCreatorAsync()
    {
        var email = $"leak-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var creatorId = await _fixture.GetUserIdAsync(email);
        await _fixture.SetUserRoleAsync(email, "Creator");
        await _fixture.SetUsernameAsync(email, $"lk{Guid.NewGuid():N}"[..12]);
        return (creatorId, email);
    }

    private Guid SeedTrack(string creatorId, string title, string visibility, string status, bool exclusiveSold)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
            Title = title,
            Price = 9.99m,
            NonExclusivePriceCents = 999,
            // Legacy licensing values set on the ENTITY — the test proves they do NOT leak.
            ExclusivePriceCents = 49900,
            CopyrightBuyoutPriceCents = 249900,
            ExclusiveSold = exclusiveSold,
            Status = status,
            LicenseType = "standard",
            AudioUrl = "tracks/leak-test.mp3",
            CreatorId = creatorId,
            Genre = "Electronic",
            Visibility = visibility
        });
        db.SaveChanges();
        return id;
    }

    private static void AssertNoForbiddenKeys(JsonElement trackItem)
    {
        foreach (var prop in trackItem.EnumerateObject())
        {
            Assert.DoesNotContain(prop.Name, ForbiddenKeys, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void AssertNoForbiddenCopy(string body)
    {
        foreach (var token in ForbiddenCopy)
        {
            Assert.False(
                body.Contains(token, StringComparison.OrdinalIgnoreCase),
                $"Response body leaked forbidden licensing/marketplace copy: '{token}'");
        }
    }

    [Fact]
    public async Task Catalog_TrackItems_OmitLicensingFields()
    {
        var (creatorId, _) = await SeedCreatorAsync();
        var trackId = SeedTrack(creatorId, "Leak Guard Catalog", "public", "public", exclusiveSold: false);

        using var client = _fixture.CreateClient();
        var response = await client.GetAsync("/catalog?pageSize=50");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected 2xx, got {(int)response.StatusCode}: {body}");

        var json = JsonDocument.Parse(body).RootElement;
        var data = json.GetProperty("data");

        JsonElement? seeded = null;
        foreach (var item in data.EnumerateArray())
        {
            AssertNoForbiddenKeys(item);
            if (item.TryGetProperty("id", out var idProp)
                && Guid.TryParse(idProp.GetString(), out var g) && g == trackId)
            {
                seeded = item;
            }
        }

        Assert.True(seeded is not null, "Seeded public track was not returned by /catalog.");
        // Sanity: the standard price IS still exposed (only exclusive/buyout removed).
        Assert.True(seeded!.Value.TryGetProperty("nonExclusivePrice", out _));
        AssertNoForbiddenCopy(body);
    }

    [Fact]
    public async Task TrackDetail_OmitsLicensingFields()
    {
        var (creatorId, _) = await SeedCreatorAsync();
        var trackId = SeedTrack(creatorId, "Leak Guard Detail", "public", "public", exclusiveSold: false);

        using var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/tracks/{trackId}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected 2xx, got {(int)response.StatusCode}: {body}");

        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        AssertNoForbiddenKeys(data);
        AssertNoForbiddenCopy(body);
    }

    [Fact]
    public async Task TrackDetail_SanitizesRetiredStatus_ToAvailable()
    {
        var (creatorId, _) = await SeedCreatorAsync();
        // A track still carrying a retired status value, reachable via single-track read.
        var trackId = SeedTrack(creatorId, "Leak Guard Status", "public", "copyright_transferred", exclusiveSold: false);

        using var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/tracks/{trackId}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected 2xx, got {(int)response.StatusCode}: {body}");

        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        Assert.Equal("available", data.GetProperty("status").GetString());
        AssertNoForbiddenCopy(body);
    }

    [Fact]
    public async Task AiDiscovery_Search_OmitsLicensingFieldsAndCopy()
    {
        var (creatorId, _) = await SeedCreatorAsync();
        SeedTrack(creatorId, "Aurora Leak Probe", "public", "public", exclusiveSold: false);

        using var client = _fixture.CreateClient();
        var response = await client.GetAsync("/ai-discovery/tracks/search?query=Aurora");
        var body = await response.Content.ReadAsStringAsync();

        // Whatever the result count, the AI surface must not carry licensing/marketplace copy
        // or the removed license-option keys.
        AssertNoForbiddenCopy(body);
        Assert.DoesNotContain("\"licenseType\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"exclusivity\"", body, StringComparison.OrdinalIgnoreCase);
    }
}
