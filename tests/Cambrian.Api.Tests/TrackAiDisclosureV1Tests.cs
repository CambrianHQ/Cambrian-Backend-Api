using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

public sealed class TrackAiDisclosureV1Tests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;
    public TrackAiDisclosureV1Tests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task LegacyTrack_PublicContract_RemainsUnclassifiedWithoutCreatingRows()
    {
        var (_, userId) = await CreateCreatorAsync("ai-legacy");
        var trackId = await _fixture.SeedTrackAsync(userId, "Legacy Disclosure Beat");

        var response = await _fixture.CreateClient().GetAsync($"/api/v1/tracks/{trackId}/ai-disclosure");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("classification").GetString().Should().Be("Unclassified");
        data.GetProperty("version").GetInt32().Should().Be(0);
        data.GetProperty("details").GetProperty("collaborators").GetArrayLength().Should().Be(0);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        (await db.TrackAiDisclosures.CountAsync(x => x.TrackId == trackId)).Should().Be(0);
    }

    [Fact]
    public async Task OwnerCreate_PersistsStructuredDisclosure_AndPublicCanReadIt()
    {
        var (owner, userId) = await CreateCreatorAsync("ai-create");
        var trackId = await _fixture.SeedTrackAsync(userId, "Disclosed Beat");
        var created = await owner.PostAsJsonAsync($"/api/v1/tracks/{trackId}/ai-disclosure", new
        {
            classification = "AIAssisted",
            aiVocals = true,
            aiPostProduction = false,
            generatorTool = "Studio Tool",
            modelVersion = "2.1",
            creationDate = "2026-07-10",
            commercialUseLicenseBasis = "Paid commercial plan",
            voiceLikenessAuthorization = "Artist consent on file",
            humanWrittenLyrics = true,
            humanVocals = true,
            humanInstruments = true,
            arrangementEditing = true,
            dawWork = true,
            collaborators = new[] { "Artist One", "Producer Two" },
            humanContributionNarrative = "Humans wrote, performed, arranged, and mixed the recording.",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var publicResponse = await _fixture.CreateClient().GetAsync($"/api/v1/tracks/{trackId}/ai-disclosure");
        var data = (await publicResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("classification").GetString().Should().Be("AIAssisted");
        data.GetProperty("definition").GetString().Should().Contain("Humans substantially created");
        data.GetProperty("details").GetProperty("generatorTool").GetString().Should().Be("Studio Tool");
        data.GetProperty("details").GetProperty("collaborators").GetArrayLength().Should().Be(2);
        data.GetProperty("version").GetInt32().Should().Be(1);

        var history = await owner.GetAsync($"/api/v1/tracks/{trackId}/ai-disclosure/history");
        history.StatusCode.Should().Be(HttpStatusCode.OK);
        var revisions = (await history.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        revisions.GetArrayLength().Should().Be(1);
        revisions[0].GetProperty("action").GetString().Should().Be("Created");
    }

    [Fact]
    public async Task Mutations_AreOwnerOnly()
    {
        var (_, ownerId) = await CreateCreatorAsync("ai-owner");
        var trackId = await _fixture.SeedTrackAsync(ownerId, "Owner Disclosure Beat");
        var (intruder, _) = await CreateCreatorAsync("ai-intruder");
        var payload = new { classification = "AIGenerated", aiVocals = true };

        (await _fixture.CreateClient().PostAsJsonAsync($"/api/v1/tracks/{trackId}/ai-disclosure", payload))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await intruder.PostAsJsonAsync($"/api/v1/tracks/{trackId}/ai-disclosure", payload))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await intruder.GetAsync($"/api/v1/tracks/{trackId}/ai-disclosure/history"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CorrectionAndRevocation_IncrementVersion_PreserveHistory_AndRejectStaleWrites()
    {
        var (owner, userId) = await CreateCreatorAsync("ai-correct");
        var trackId = await _fixture.SeedTrackAsync(userId, "Corrected Disclosure Beat");
        (await owner.PostAsJsonAsync($"/api/v1/tracks/{trackId}/ai-disclosure", new
        {
            classification = "AIGenerated", aiVocals = true,
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var corrected = await owner.PutAsJsonAsync($"/api/v1/tracks/{trackId}/ai-disclosure", new
        {
            classification = "AIAssisted", aiVocals = false, humanVocals = true,
            expectedVersion = 1, correctionReason = "Corrected after reviewing session files",
        });
        corrected.StatusCode.Should().Be(HttpStatusCode.OK);

        var stale = await owner.PutAsJsonAsync($"/api/v1/tracks/{trackId}/ai-disclosure", new
        {
            classification = "AIGenerated", expectedVersion = 1,
        });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var revoked = await owner.PostAsJsonAsync($"/api/v1/tracks/{trackId}/ai-disclosure/revoke", new
        {
            reason = "Disclosure withdrawn pending documentation review", expectedVersion = 2,
        });
        revoked.StatusCode.Should().Be(HttpStatusCode.OK);
        var revokedData = (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        revokedData.GetProperty("classification").GetString().Should().Be("Unclassified");
        revokedData.GetProperty("isRevoked").GetBoolean().Should().BeTrue();
        revokedData.GetProperty("version").GetInt32().Should().Be(3);
        revokedData.GetProperty("details").GetProperty("aiVocals").ValueKind.Should().Be(JsonValueKind.Null);

        var history = (await (await owner.GetAsync($"/api/v1/tracks/{trackId}/ai-disclosure/history"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        history.GetArrayLength().Should().Be(3);
        history[0].GetProperty("action").GetString().Should().Be("Revoked");
        history[1].GetProperty("action").GetString().Should().Be("Corrected");
        history[2].GetProperty("snapshot").GetProperty("classification").GetString().Should().Be("AIGenerated");
    }

    [Theory]
    [InlineData("Unclassified")]
    [InlineData("NotAClassification")]
    public async Task InvalidClassification_Returns400(string classification)
    {
        var (owner, userId) = await CreateCreatorAsync("ai-invalid");
        var trackId = await _fixture.SeedTrackAsync(userId, "Invalid Disclosure Beat");
        (await owner.PostAsJsonAsync($"/api/v1/tracks/{trackId}/ai-disclosure", new { classification }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<(HttpClient Client, string UserId)> CreateCreatorAsync(string prefix)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@test.com";
        const string password = "Test1234!@";
        await _fixture.RegisterUserAsync(email, password);
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.FirstAsync(x => x.Email == email);
            user.Tier = "creator"; user.Role = "Creator";
            var username = $"u{Guid.NewGuid():N}"[..14];
            user.UserName = username; user.NormalizedUserName = username.ToUpperInvariant();
            await db.SaveChangesAsync();
        }
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await _fixture.LoginUserAsync(email, password));
        return (client, await _fixture.GetUserIdAsync(email));
    }
}
