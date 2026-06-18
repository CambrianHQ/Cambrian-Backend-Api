using System.Net;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Regression;

/// <summary>
/// Launch-gate guards for the anonymous AI discovery surface:
/// (1) it must never leak the raw object-storage key (bucket layout / creator ids), and
/// (2) it must only expose <c>public</c> tracks — hidden/limited tracks must not be
/// enumerable by id (mirrors the public-only filter the search path already applies).
/// </summary>
[Trait("Category", "Critical")]
public sealed class AiDiscoverySecurityTests : IClassFixture<CambrianApiFixture>
{
    private const string RawAudioKey = "tracks/secret-creator-42/master.mp3";

    private readonly CambrianApiFixture _fixture;

    public AiDiscoverySecurityTests(CambrianApiFixture fixture) => _fixture = fixture;

    private async Task<string> SeedCreatorAsync()
    {
        var email = $"ai-sec-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var creatorId = await _fixture.GetUserIdAsync(email);
        await _fixture.SetUsernameAsync(email, $"ai{Guid.NewGuid():N}"[..12]);
        return creatorId;
    }

    private Guid SeedTrack(string creatorId, string visibility)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
            Title = "AI Security Probe",
            Price = 9.99m,
            NonExclusivePriceCents = 999,
            Status = "available",
            AudioUrl = RawAudioKey,
            CreatorId = creatorId,
            Genre = "Electronic",
            Visibility = visibility,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task AiDiscovery_Preview_DoesNotLeakRawStorageKey()
    {
        var creatorId = await SeedCreatorAsync();
        var trackId = SeedTrack(creatorId, "public");

        using var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/ai-discovery/tracks/{trackId}/preview");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The raw object-storage key must never appear anywhere in the response.
        Assert.DoesNotContain(RawAudioKey, body);

        var url = JsonDocument.Parse(body).RootElement
            .GetProperty("preview").GetProperty("url").GetString();
        Assert.NotEqual(RawAudioKey, url);
        Assert.Equal($"/stream/{trackId}/audio", url); // public proxy route, not the key
    }

    [Fact]
    public async Task AiDiscovery_Details_HiddenTrack_Returns404()
    {
        var creatorId = await SeedCreatorAsync();
        var trackId = SeedTrack(creatorId, "hidden");

        using var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/ai-discovery/tracks/{trackId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AiDiscovery_Preview_HiddenTrack_Returns404()
    {
        var creatorId = await SeedCreatorAsync();
        var trackId = SeedTrack(creatorId, "hidden");

        using var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/ai-discovery/tracks/{trackId}/preview");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AiDiscovery_Details_PublicTrack_IsReturned()
    {
        var creatorId = await SeedCreatorAsync();
        var trackId = SeedTrack(creatorId, "public");

        using var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/ai-discovery/tracks/{trackId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(RawAudioKey, body); // details must not leak the key either
    }

    [Fact]
    public async Task AiDiscovery_Details_UseNonLicensingUsageCopy()
    {
        var creatorId = await SeedCreatorAsync();
        var trackId = SeedTrack(creatorId, "public");

        using var client = _fixture.CreateClient();
        var body = await (await client.GetAsync($"/ai-discovery/tracks/{trackId}")).Content.ReadAsStringAsync();

        // Platform is NOT a licensing marketplace — the AI copy must use usage-terms language.
        Assert.DoesNotContain("under this license", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No resale of license", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Standard License", body, StringComparison.OrdinalIgnoreCase);
    }
}
