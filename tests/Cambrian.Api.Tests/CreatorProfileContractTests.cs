using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Contract shape tests for CreatorProfile endpoints.
/// Ensures the API response shape matches the agreed-upon DTO contract
/// so frontend and backend never drift.
/// </summary>
[Trait("Category", "Critical")]
public sealed class CreatorProfileContractTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CreatorProfileContractTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetMyProfile_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/creator-profile/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task GetBySlug_NonExistent_Returns404()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/creator-profile/nonexistent-slug-xyz");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task UpsertProfile_ReturnsPublicSafeStatsShape()
    {
        // Register a creator-tier user
        var email = $"creator-contract-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";
        var client = await CreateCreatorClientAsync(email, password);

        // Upsert profile
        var slug = $"test-{Guid.NewGuid():N}"[..20];
        var res = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Test bio",
            niche = "cinematic AI composer",
            socialLinks = new[] { new { platform = "twitter", url = "https://twitter.com/test" } },
            showEarnings = true,
            showDownloadStats = false,
        });

        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Verify envelope shape
        Assert.True(json.GetProperty("success").GetBoolean());
        var data = json.GetProperty("data");

        // Verify all required CreatorProfileDto fields exist
        Assert.True(data.TryGetProperty("id", out _), "Missing 'id'");
        Assert.True(data.TryGetProperty("userId", out _), "Missing 'userId'");
        Assert.True(data.TryGetProperty("slug", out _), "Missing 'slug'");
        Assert.True(data.TryGetProperty("bio", out _), "Missing 'bio'");
        Assert.True(data.TryGetProperty("niche", out _), "Missing 'niche'");
        Assert.True(data.TryGetProperty("profileImageUrl", out _), "Missing 'profileImageUrl'");
        Assert.True(data.TryGetProperty("bannerImageUrl", out _), "Missing 'bannerImageUrl'");
        Assert.True(data.TryGetProperty("socialLinks", out _), "Missing 'socialLinks'");
        Assert.True(data.TryGetProperty("stats", out _), "Missing 'stats'");
        Assert.True(data.TryGetProperty("createdAt", out _), "Missing 'createdAt'");
        Assert.True(data.TryGetProperty("updatedAt", out _), "Missing 'updatedAt'");

        // Verify stats sub-object shape
        var stats = data.GetProperty("stats");
        Assert.True(stats.TryGetProperty("totalDownloads", out _), "Missing 'stats.totalDownloads'");
        Assert.False(stats.TryGetProperty("totalEarnings", out _), "Public profile stats must not expose creator earnings");

        // Verify values match what we sent
        Assert.Equal(slug, data.GetProperty("slug").GetString());
        Assert.Equal("Test bio", data.GetProperty("bio").GetString());
        Assert.Equal("cinematic AI composer", data.GetProperty("niche").GetString());
    }

    /// <summary>
    /// B5 regression: PUT /creator-profile/me must persist displayName. It previously dropped the
    /// field silently — the PUT echoed the old name and a subsequent GET still returned the old name.
    /// </summary>
    [Fact]
    public async Task UpsertProfile_PersistsDisplayName_AndRoundTrips()
    {
        var email = $"creator-dn-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email, "Test1234!@");

        var slug = $"dn-{Guid.NewGuid():N}"[..20];
        var putRes = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            displayName = "BUGPROBE-name",
            bio = "x",
            showEarnings = false,
            showDownloadStats = false,
        });
        putRes.EnsureSuccessStatusCode();
        var putData = (await putRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("BUGPROBE-name", putData.GetProperty("displayName").GetString());

        // The new name must survive a round trip (the bug was a silent revert to the old name).
        var getRes = await client.GetAsync("/creator-profile/me");
        getRes.EnsureSuccessStatusCode();
        var getData = (await getRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("BUGPROBE-name", getData.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task UpsertProfile_PersistsStudioSetupAndJourney_AndRoundTrips()
    {
        var email = $"creator-studio-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email, "Test1234!@");

        var slug = $"studio-{Guid.NewGuid():N}"[..20];
        var putRes = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "studio test",
            showEarnings = false,
            showDownloadStats = false,
            // Free-text by design: niche gear must round-trip verbatim, not map to an enum.
            // Daw is a tag list — same chip/array shape and validation as the other
            // studio fields (aiTools, instruments, hardware, plugins, gear).
            studioSetup = new
            {
                daw = new[] { "Ableton Live 12", "Reaper for stems" },
                aiTools = new[] { "Suno v5.5", "RVC" },
                instruments = new[] { "kalimba", "Otamatone Deluxe" },
                plugins = new[] { "FabFilter Pro-Q 4" },
                workflowNotes = "Generate 6 takes, comp in Reaper, master in Ableton.",
            },
            journeyEntries = new object[]
            {
                new { type = "milestone", title = "First 1k plays", date = "2026-05-01" },
                new { type = "event", title = "AI Music Night", venue = "The Basement, Austin", date = "2026-08-15", link = "https://example.com/tickets" },
            },
        });
        putRes.EnsureSuccessStatusCode();
        var putData = (await putRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Ableton Live 12", putData.GetProperty("studioSetup").GetProperty("daw")[0].GetString());
        Assert.Equal("Reaper for stems", putData.GetProperty("studioSetup").GetProperty("daw")[1].GetString());
        Assert.Equal("Otamatone Deluxe", putData.GetProperty("studioSetup").GetProperty("instruments")[1].GetString());
        Assert.Equal(2, putData.GetProperty("journeyEntries").GetArrayLength());

        // Public read surface (anonymous) must expose both sections.
        var publicClient = _fixture.CreateClient();
        var pubRes = await publicClient.GetAsync($"/creator-profile/{slug}");
        pubRes.EnsureSuccessStatusCode();
        var pubData = (await pubRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Suno v5.5", pubData.GetProperty("studioSetup").GetProperty("aiTools")[0].GetString());
        var entry = pubData.GetProperty("journeyEntries")[1];
        Assert.Equal("event", entry.GetProperty("type").GetString());
        Assert.Equal("The Basement, Austin", entry.GetProperty("venue").GetString());

        // Upsert WITHOUT the sections (null = not sent) must keep the stored values.
        var putRes2 = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "edited bio only",
            showEarnings = false,
            showDownloadStats = false,
        });
        putRes2.EnsureSuccessStatusCode();
        var kept = (await putRes2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Ableton Live 12", kept.GetProperty("studioSetup").GetProperty("daw")[0].GetString());
        Assert.Equal(2, kept.GetProperty("journeyEntries").GetArrayLength());
    }

    [Fact]
    public async Task UpsertProfile_AcceptsLegacyStringDaw_ForBackwardCompatibility()
    {
        // Profiles saved before Daw became a tag list stored it as a plain JSON
        // string. The FlexibleStringListConverter must still accept that shape on
        // write (a client resubmitting an old payload) and always normalize it to
        // the array shape on the way back out.
        var email = $"creator-legacy-daw-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email, "Test1234!@");

        var slug = $"legacy-daw-{Guid.NewGuid():N}"[..20];
        var json = $$"""
            { "slug": "{{slug}}", "bio": "x", "showEarnings": false, "showDownloadStats": false,
              "studioSetup": { "daw": "FL Studio 21" } }
            """;
        var putRes = await client.PutAsync("/creator-profile/me",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        putRes.EnsureSuccessStatusCode();
        var putData = (await putRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var dawTags = putData.GetProperty("studioSetup").GetProperty("daw");
        Assert.Equal(JsonValueKind.Array, dawTags.ValueKind);
        Assert.Equal("FL Studio 21", dawTags[0].GetString());
    }

    [Fact]
    public async Task UpsertProfile_RejectsInvalidJourneyEntry()
    {
        var email = $"creator-journey-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email, "Test1234!@");

        var slug = $"journey-{Guid.NewGuid():N}"[..20];
        // javascript: link must be rejected (same rule as social links).
        var bad = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "x",
            showEarnings = false,
            showDownloadStats = false,
            journeyEntries = new object[]
            {
                new { type = "event", title = "Show", link = "javascript:alert(1)" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Unknown entry type must be rejected.
        var badType = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "x",
            showEarnings = false,
            showDownloadStats = false,
            journeyEntries = new object[] { new { type = "manifesto", title = "??" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
    }

    [Fact]
    public async Task GetBySlug_ReturnsExpectedShape_AfterCreation()
    {
        var email = $"creator-slug-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";
        var client = await CreateCreatorClientAsync(email, password);

        var slug = $"slugtest-{Guid.NewGuid():N}"[..20];
        var upsertRes = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Slug test bio",
            showEarnings = false,
            showDownloadStats = false,
        });
        upsertRes.EnsureSuccessStatusCode();

        // Now fetch via public endpoint (no auth needed)
        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal(slug, data.GetProperty("slug").GetString());
        Assert.Equal("Slug test bio", data.GetProperty("bio").GetString());
        Assert.True(data.TryGetProperty("stats", out _), "Missing 'stats' in public endpoint");
    }

    [Fact]
    public async Task Collections_ReturnsExpectedShape()
    {
        var email = $"creator-coll-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";
        var client = await CreateCreatorClientAsync(email, password);

        // Create profile first
        var slug = $"colltest-{Guid.NewGuid():N}"[..20];
        var upsertRes = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Collections test",
            showEarnings = false,
            showDownloadStats = false,
        });
        upsertRes.EnsureSuccessStatusCode();

        // Create a collection
        var createRes = await client.PostAsJsonAsync("/creator-profile/me/collections", new
        {
            title = "My Album",
            description = "Best beats",
            trackIds = "",
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var createJson = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var collData = createJson.GetProperty("data");

        // Verify TrackCollectionDto shape
        Assert.True(collData.TryGetProperty("id", out _), "Missing 'id'");
        Assert.True(collData.TryGetProperty("title", out _), "Missing 'title'");
        Assert.True(collData.TryGetProperty("description", out _), "Missing 'description'");
        Assert.True(collData.TryGetProperty("coverImageUrl", out _), "Missing 'coverImageUrl'");
        Assert.True(collData.TryGetProperty("trackIds", out _), "Missing 'trackIds'");
        Assert.True(collData.TryGetProperty("createdAt", out _), "Missing 'createdAt'");
        Assert.True(collData.TryGetProperty("updatedAt", out _), "Missing 'updatedAt'");

        Assert.Equal("My Album", collData.GetProperty("title").GetString());

        // Fetch collections via public endpoint
        var publicClient = _fixture.CreateClient();
        var listRes = await publicClient.GetAsync($"/creator-profile/{slug}/collections");
        listRes.EnsureSuccessStatusCode();

        var listJson = await listRes.Content.ReadFromJsonAsync<JsonElement>();
        var items = listJson.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.True(items.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Collections_CreateAndUpdate_PersistCoverImageUrl()
    {
        var email = $"creator-coll-cover-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";
        var client = await CreateCreatorClientAsync(email, password);

        var slug = $"collcover-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Collections test",
            showEarnings = false,
            showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        var createRes = await client.PostAsJsonAsync("/creator-profile/me/collections", new
        {
            title = "Cover Album",
            description = "Best beats",
            coverImageUrl = "covers/albums/original-cover.jpg",
            trackIds = "",
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var created = (await createRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Contains("/images/covers/albums/original-cover.jpg", created.GetProperty("coverImageUrl").GetString());

        var collectionId = created.GetProperty("id").GetString();
        var updateRes = await client.PutAsJsonAsync($"/creator-profile/me/collections/{collectionId}", new
        {
            title = "Cover Album",
            description = "Best beats",
            coverImageUrl = "covers/albums/updated-cover.jpg",
            trackIds = ""
        });
        updateRes.EnsureSuccessStatusCode();

        var updated = (await updateRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Contains("/images/covers/albums/updated-cover.jpg", updated.GetProperty("coverImageUrl").GetString());

        var publicClient = _fixture.CreateClient();
        var listRes = await publicClient.GetAsync($"/creator-profile/{slug}/collections");
        listRes.EnsureSuccessStatusCode();
        var collections = (await listRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Contains("/images/covers/albums/updated-cover.jpg", collections[0].GetProperty("coverImageUrl").GetString());
    }

    [Fact]
    public async Task Collections_Create_CreatorWithoutUsername_ReturnsCreated()
    {
        var email = $"creator-coll-nouser-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";

        await _fixture.RegisterUserAsync(email, password);
        await _fixture.SetUserRoleAsync(email, "Creator");

        var token = await _fixture.LoginUserAsync(email, password);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var createRes = await client.PostAsJsonAsync("/creator-profile/me/collections", new
        {
            title = "Upload Flow Album",
            description = "Created before username setup",
            trackIds = "",
        });

        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
    }

    [Fact]
    public async Task Collections_Create_AllowsStaleTokenAfterCreatorPromotion()
    {
        var email = $"creator-coll-stale-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";

        var staleToken = await _fixture.RegisterUserAsync(email, password);
        await _fixture.SetUserRoleAsync(email, "Creator");
        await _fixture.SetUsernameAsync(email, $"u{Guid.NewGuid():N}"[..12]);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", staleToken);

        var createRes = await client.PostAsJsonAsync("/creator-profile/me/collections", new
        {
            title = "Stale Token Album",
            description = "Created after promotion",
            trackIds = "",
        });

        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
    }

    [Fact]
    public async Task SlugConflict_Returns409()
    {
        // User A creates profile with a slug
        var emailA = $"creator-a-{Guid.NewGuid():N}@test.com";
        var passwordA = "Test1234!@";
        var clientA = await CreateCreatorClientAsync(emailA, passwordA);

        var slug = $"unique-{Guid.NewGuid():N}"[..20];
        var resA = await clientA.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "User A",
            showEarnings = false,
            showDownloadStats = false,
        });
        resA.EnsureSuccessStatusCode();

        // User B tries to use the same slug
        var emailB = $"creator-b-{Guid.NewGuid():N}@test.com";
        var passwordB = "Test1234!@";
        var clientB = await CreateCreatorClientAsync(emailB, passwordB);

        var resB = await clientB.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "User B",
            showEarnings = false,
            showDownloadStats = false,
        });
        Assert.Equal(HttpStatusCode.Conflict, resB.StatusCode);
    }

    // ---- F1/F2/F3: Authorization gap tests ----

    [Fact]
    public async Task UpsertProfile_RegularUser_Returns403()
    {
        var email = $"user-{Guid.NewGuid():N}@test.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");

        var res = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug = "should-fail",
            bio = "I am a regular user",
            showEarnings = false,
            showDownloadStats = false,
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task PatchSettings_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();

        var res = await client.PatchAsync("/creator-profile/me/settings",
            JsonContent.Create(new { showEarnings = true }));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task UploadAvatar_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 0xFF, 0xD8 }), "file", "avatar.jpg");
        var res = await client.PostAsync("/creator-profile/me/avatar", form);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task UploadBanner_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 0xFF, 0xD8 }), "file", "banner.jpg");
        var res = await client.PostAsync("/creator-profile/me/banner", form);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>
    /// Banner/avatar uploads must reject anything over 5 MB — matching the client's advertised
    /// and enforced "5 MB max" copy (ImageUploadField / CREATOR_PROFILE_MAX_IMAGE_BYTES) and,
    /// separately, bounding the worst-case payload the /{username}/opengraph-image share-card
    /// route has to fetch and rasterize (an oversized banner there was the root cause of
    /// Cloudflare 1102 "Worker exceeded resource limits" on public profile views/shares).
    /// The previous 10 MB cap matched neither.
    /// </summary>
    [Fact]
    public async Task UploadBanner_RejectsFileOverFiveMegabytes()
    {
        var email = $"creator-bigbanner-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email, "Test1234!@");

        var oversized = new byte[6 * 1024 * 1024]; // 6 MB, over the 5 MB cap
        var pngMagicBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        Array.Copy(pngMagicBytes, oversized, pngMagicBytes.Length);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(oversized), "file", "huge-banner.png");
        var res = await client.PostAsync("/creator-profile/me/banner", form);

        // Either the [RequestSizeLimit] pipeline guard or the in-handler MaxProfileImageSize
        // check can reject it first depending on buffering — both must fail the request, never
        // silently accept and store a payload that big.
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    /// <summary>
    /// Album/collection cover art keeps its own, separate, larger 10 MB cap (advertised as
    /// "up to 10MB" in AlbumEditorModal) — the banner/avatar tightening to 5 MB must not have
    /// collaterally shrunk this unrelated upload path.
    /// </summary>
    [Fact]
    public async Task UploadCollectionCover_StillAllowsUpToTenMegabytes()
    {
        var email = $"creator-albumcover-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email, "Test1234!@");

        var createRes = await client.PostAsJsonAsync("/creator-profile/me/collections", new { title = "Test Album" });
        createRes.EnsureSuccessStatusCode();
        var collectionId = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("id").GetString();

        var eightMb = new byte[8 * 1024 * 1024]; // over the 5 MB profile-image cap, under the 10 MB album cap
        var pngMagicBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        Array.Copy(pngMagicBytes, eightMb, pngMagicBytes.Length);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(eightMb), "file", "cover.png");
        var res = await client.PostAsync($"/creator-profile/me/collections/{collectionId}/cover", form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    /// <summary>
    /// Register user, set tier to creator in DB, then re-login to get a JWT with tier=creator claim.
    /// </summary>
    private async Task<HttpClient> CreateCreatorClientAsync(string email, string password)
    {
        // Register
        await _fixture.RegisterUserAsync(email, password);

        // Set tier, role, and username in DB to simulate completed creator onboarding
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var users = await db.Users.ToListAsync();
            foreach (var u in users)
            {
                if (u.Email == email)
                {
                    u.Tier = "creator";
                    u.Role = "Creator";
                    u.UserName = $"u{Guid.NewGuid():N}"[..12];
                    u.NormalizedUserName = u.UserName.ToUpperInvariant();
                    break;
                }
            }
            await db.SaveChangesAsync();
        }

        // Re-login to get fresh JWT with tier=creator
        var client = _fixture.CreateClient();
        var loginRes = await client.PostAsJsonAsync("/auth/login", new { email, password });
        loginRes.EnsureSuccessStatusCode();
        var json = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
