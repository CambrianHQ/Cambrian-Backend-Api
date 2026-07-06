using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Search/link QA pass: pins the public search + profile-lookup contract the
/// frontend depends on for /search and /@{username} links.
///  - creator search matches partial + case-insensitive terms and always
///    returns a non-empty slug (frontends build /@{slug} links from it),
///  - GET /creators/search accepts both ?query= and the ?q= alias,
///  - username lookup is case-insensitive,
///  - catalog ?search= matches title/genre/creator and never returns
///    non-public tracks,
///  - public payloads carry no email/Stripe/earnings fields.
/// </summary>
public sealed class SearchAndProfileQaTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public SearchAndProfileQaTests(CambrianApiFixture fixture) => _fixture = fixture;

    private static readonly string[] PrivateFieldFragments =
    {
        "email", "stripe", "earning", "payout", "revenue", "balance", "platformfee"
    };

    private static void AssertNoPrivateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var name = prop.Name.ToLowerInvariant();
                foreach (var fragment in PrivateFieldFragments)
                    Assert.DoesNotContain(fragment, name);
                AssertNoPrivateFields(prop.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AssertNoPrivateFields(item);
        }
    }

    [Fact]
    public async Task CreatorSearch_PartialCaseInsensitive_ReturnsSlugAlways()
    {
        var marker = $"qs{Guid.NewGuid():N}"[..10];
        await _fixture.SeedCreatorAsync($"user-{Guid.NewGuid():N}", marker + "beats", "Neon Wave");

        var client = _fixture.CreateClient();
        // Partial term, wrong case — must still match.
        var res = await client.GetAsync($"/creators/search?query={marker.ToUpperInvariant()}");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.GetArrayLength() >= 1);
        foreach (var c in data.EnumerateArray())
        {
            var slug = c.GetProperty("slug").GetString();
            Assert.False(string.IsNullOrWhiteSpace(slug), "search result slug must never be empty");
        }
    }

    [Fact]
    public async Task CreatorSearch_QParamAlias_Works()
    {
        var marker = $"qa{Guid.NewGuid():N}"[..10];
        await _fixture.SeedCreatorAsync($"user-{Guid.NewGuid():N}", marker + "alias");

        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/creators/search?q={marker}");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.GetArrayLength() >= 1, "?q= alias must bind to the search query");
    }

    [Fact]
    public async Task CreatorSearch_ByDisplayName_Matches()
    {
        var marker = $"dn{Guid.NewGuid():N}"[..10];
        await _fixture.SeedCreatorAsync($"user-{Guid.NewGuid():N}", $"u{Guid.NewGuid():N}"[..12], $"The {marker} Project");

        var client = _fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/creators/search", new { query = marker });
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.GetArrayLength() >= 1, "display-name substring must match");
    }

    [Fact]
    public async Task CreatorSearch_ResultsCarryNoPrivateFields()
    {
        var marker = $"pv{Guid.NewGuid():N}"[..10];
        await _fixture.SeedCreatorAsync($"user-{Guid.NewGuid():N}", marker + "priv");

        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/creators/search?query={marker}");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        AssertNoPrivateFields(data);
    }

    [Fact]
    public async Task CreatorByUsername_IsCaseInsensitive()
    {
        var username = $"case{Guid.NewGuid():N}"[..14];
        await _fixture.SeedCreatorAsync($"user-{Guid.NewGuid():N}", username);

        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/api/creators/by-username/{username.ToUpperInvariant()}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(username, data.GetProperty("username").GetString());
    }

    [Fact]
    public async Task CreatorByUsername_PublicPayload_CarriesNoPrivateFields()
    {
        var username = $"pub{Guid.NewGuid():N}"[..14];
        await _fixture.SeedCreatorAsync($"user-{Guid.NewGuid():N}", username);

        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/api/creators/by-username/{username}");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        AssertNoPrivateFields(data);
    }

    /// <summary>
    /// A catalog-visible creator needs a REAL ApplicationUser row (the browse
    /// query's Creator include inner-joins AspNetUsers) and the CreatorUuid FK
    /// (artist-name search matches via the canonical CreatorEntity join).
    /// </summary>
    private async Task<(string UserId, Guid CreatorUuid, string Username)> SeedCatalogCreatorAsync()
    {
        var email = $"qa-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var username = $"artist{Guid.NewGuid():N}"[..16];
        var creatorUuid = await _fixture.SeedCreatorAsync(userId, username);
        return (userId, creatorUuid, username);
    }

    [Fact]
    public async Task CatalogSearch_MatchesTitle_PartialCaseInsensitive()
    {
        var (userId, creatorUuid, _) = await SeedCatalogCreatorAsync();
        var marker = $"Zx{Guid.NewGuid():N}"[..10];
        await _fixture.SeedTrackWithCreatorUuidAsync(userId, creatorUuid, $"Midnight {marker} Anthem");

        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/catalog?search={marker.ToLowerInvariant()}&pageSize=10");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.GetArrayLength() >= 1, "partial lowercase title term must match");
        AssertNoPrivateFields(data);
    }

    [Fact]
    public async Task CatalogSearch_MatchesCreatorUsername()
    {
        var (userId, creatorUuid, username) = await SeedCatalogCreatorAsync();
        await _fixture.SeedTrackWithCreatorUuidAsync(userId, creatorUuid, "Untitled Session");

        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/catalog?search={username}&pageSize=10");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.GetArrayLength() >= 1, "creator username must be searchable");
    }

    [Fact]
    public async Task CatalogSearch_NeverReturnsHiddenTracks()
    {
        var (userId, creatorUuid, _) = await SeedCatalogCreatorAsync();
        var marker = $"Hh{Guid.NewGuid():N}"[..10];
        // Sanity pair: a public track with the same marker IS findable, so an
        // empty result for the hidden one can't be a false pass.
        await _fixture.SeedTrackWithCreatorUuidAsync(userId, creatorUuid, $"Public {marker} Cut");
        await _fixture.SeedTrackWithCreatorUuidAsync(userId, creatorUuid, $"Secret {marker} Draft", visibility: "hidden");

        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/catalog?search={marker}&pageSize=10");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        foreach (var t in data.EnumerateArray())
            Assert.DoesNotContain("Secret", t.GetProperty("title").GetString());
    }
}
