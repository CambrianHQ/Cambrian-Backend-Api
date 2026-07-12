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

/// <summary>
/// Regression suite for the release-readiness checklist dead-end (observed on a
/// live production track): a creator filled everything the edit
/// page offered, every save succeeded, yet the checklist's "AI disclosure" and
/// "Rights / ownership attestation" items stayed incomplete forever — because
/// nothing the page saved ever wrote TrackAuthorship.AiDisclosure or
/// Track.CommercialRightsVerified, the exact fields ComplianceScoreService
/// evaluates.
///
/// The fix routes both free attestations through PUT /creator/tracks/{id}
/// (aiDisclosure, rightsConfirmed) and makes every mutation response carry the
/// canonical persisted values plus the freshly evaluated releaseReadiness state.
/// These tests exercise the full journey and assert persistence from a NEW
/// DbContext scope — a stale in-memory tracked entity must not fake success.
/// </summary>
[Trait("Category", "Critical")]
public sealed class TrackEditReadinessAttestationTests : IClassFixture<CambrianApiFixture>
{
    private const string Password = "Test1234!@";

    private readonly CambrianApiFixture _fixture;

    public TrackEditReadinessAttestationTests(CambrianApiFixture fixture) => _fixture = fixture;

    private sealed record CreatorTrack(HttpClient Creator, string CreatorUserId, Guid TrackId);

    private async Task<CreatorTrack> CreateCreatorWithTrackAsync()
    {
        var seed = Guid.NewGuid().ToString("N");
        var email = $"readiness-{seed}@cambrian.com";
        var client = await _fixture.CreateRoleClientAsync(email, Password, "Creator", $"readiness{seed[..8]}");
        await _fixture.SetCreatorTierAsync(email, Cambrian.Domain.Enums.CreatorTier.Creator);
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(userId, "Readiness Attestation Beat");
        return new CreatorTrack(client, userId, trackId);
    }

    private static JsonElement DataOf(JsonElement root) => root.GetProperty("data");

    private static JsonElement ChecklistItem(JsonElement readiness, string key) =>
        readiness.GetProperty("checklistItems").EnumerateArray()
            .Single(i => i.GetProperty("key").GetString() == key);

    // ───── the exact user journey that was broken in production ─────

    [Fact]
    public async Task SavingDisclosureAndRights_CompletesBothChecklistItems_AndPersists()
    {
        var ctx = await CreateCreatorWithTrackAsync();

        // Before: both requirements incomplete.
        var before = await ctx.Creator.GetFromJsonAsync<JsonElement>($"/api/tracks/{ctx.TrackId}/compliance-score");
        ChecklistItem(DataOf(before), "ai_disclosure").GetProperty("status").GetString().Should().Be("incomplete");
        ChecklistItem(DataOf(before), "rights").GetProperty("status").GetString().Should().Be("incomplete");

        // Save both attestations through the normal edit endpoint.
        var save = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            aiDisclosure = "Lyrics and vocals are mine; Suno v5 generated the instrumental stems.",
            rightsConfirmed = true,
        });
        save.StatusCode.Should().Be(HttpStatusCode.OK);

        // The mutation response itself carries the persisted values and the
        // re-evaluated readiness — the UI never has to guess.
        var saved = DataOf(await save.Content.ReadFromJsonAsync<JsonElement>());
        saved.GetProperty("aiDisclosure").GetString().Should().Contain("Suno v5");
        saved.GetProperty("commercialRightsVerified").GetBoolean().Should().BeTrue();
        var readiness = saved.GetProperty("releaseReadiness");
        ChecklistItem(readiness, "ai_disclosure").GetProperty("status").GetString().Should().Be("complete");
        ChecklistItem(readiness, "rights").GetProperty("status").GetString().Should().Be("complete");

        // Persisted for real — read back from a NEW DbContext scope, not the
        // request's tracked entities.
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == ctx.TrackId);
            track.CommercialRightsVerified.Should().BeTrue();
            var authorship = await db.TrackAuthorships.AsNoTracking().SingleAsync(a => a.TrackId == ctx.TrackId);
            authorship.AiDisclosure.Should().Contain("Suno v5");
        }

        // A fresh readiness read (the same endpoint the checklist UI uses)
        // agrees after "reload".
        var after = await ctx.Creator.GetFromJsonAsync<JsonElement>($"/api/tracks/{ctx.TrackId}/compliance-score");
        ChecklistItem(DataOf(after), "ai_disclosure").GetProperty("status").GetString().Should().Be("complete");
        ChecklistItem(DataOf(after), "rights").GetProperty("status").GetString().Should().Be("complete");
    }

    [Fact]
    public async Task OwnerTrackDetail_ReturnsSavedAttestations_ForHydration()
    {
        var ctx = await CreateCreatorWithTrackAsync();

        (await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            aiDisclosure = "No generative AI was used.",
            rightsConfirmed = true,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // GET /creator/tracks/{guid} — the edit page's hydration source.
        var detail = await ctx.Creator.GetFromJsonAsync<JsonElement>($"/creator/tracks/{ctx.TrackId}");
        var data = DataOf(detail);
        data.GetProperty("id").GetString().Should().Be(ctx.TrackId.ToString());
        data.GetProperty("aiDisclosure").GetString().Should().Be("No generative AI was used.");
        data.GetProperty("commercialRightsVerified").GetBoolean().Should().BeTrue();
        ChecklistItem(data.GetProperty("releaseReadiness"), "ai_disclosure")
            .GetProperty("status").GetString().Should().Be("complete");
    }

    [Fact]
    public async Task WhitespaceDisclosure_ClearsIt_AndChecklistReturnsToIncomplete()
    {
        var ctx = await CreateCreatorWithTrackAsync();

        (await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            aiDisclosure = "Suno v5 stems.",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var clear = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            aiDisclosure = "   ",
        });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = DataOf(await clear.Content.ReadFromJsonAsync<JsonElement>());
        cleared.GetProperty("aiDisclosure").ValueKind.Should().Be(JsonValueKind.Null);
        ChecklistItem(cleared.GetProperty("releaseReadiness"), "ai_disclosure")
            .GetProperty("status").GetString().Should().Be("incomplete");

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var authorship = await db.TrackAuthorships.AsNoTracking().SingleAsync(a => a.TrackId == ctx.TrackId);
        authorship.AiDisclosure.Should().BeNull();
    }

    [Fact]
    public async Task RevokingRights_FlipsChecklistBackToIncomplete()
    {
        var ctx = await CreateCreatorWithTrackAsync();

        (await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new { rightsConfirmed = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var revoke = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new { rightsConfirmed = false });
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = DataOf(await revoke.Content.ReadFromJsonAsync<JsonElement>());
        data.GetProperty("commercialRightsVerified").GetBoolean().Should().BeFalse();
        ChecklistItem(data.GetProperty("releaseReadiness"), "rights")
            .GetProperty("status").GetString().Should().Be("incomplete");

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == ctx.TrackId);
        track.CommercialRightsVerified.Should().BeFalse();
    }

    [Fact]
    public async Task DisclosureSaveViaEditEndpoint_NeverWipesNarrativeAuthorshipFields()
    {
        var ctx = await CreateCreatorWithTrackAsync();

        // Seed a full authorship document (as the Creator+ suite would write it).
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.TrackAuthorships.Add(new Cambrian.Domain.Entities.TrackAuthorship
            {
                Id = Guid.NewGuid(),
                TrackId = ctx.TrackId,
                Edits = "Re-arranged the bridge",
                ArrangementNotes = "Dropped the second chorus",
                ProcessNotes = "Mixed in Ableton",
                LyricsAuthored = true,
                AiDisclosure = "old disclosure",
            });
            await db.SaveChangesAsync();
        }

        (await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            aiDisclosure = "new disclosure",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var row = await db.TrackAuthorships.AsNoTracking().SingleAsync(a => a.TrackId == ctx.TrackId);
            row.AiDisclosure.Should().Be("new disclosure");
            row.Edits.Should().Be("Re-arranged the bridge", "a details-page save must never wipe narrative authorship");
            row.ArrangementNotes.Should().Be("Dropped the second chorus");
            row.ProcessNotes.Should().Be("Mixed in Ableton");
            row.LyricsAuthored.Should().BeTrue();
        }
    }

    [Fact]
    public async Task OmittingAttestations_KeepsStoredValues()
    {
        var ctx = await CreateCreatorWithTrackAsync();

        (await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            aiDisclosure = "No generative AI was used.",
            rightsConfirmed = true,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // A later ordinary metadata edit that says nothing about attestations
        // must not disturb them.
        var edit = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            title = "Renamed, attestations untouched",
        });
        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = DataOf(await edit.Content.ReadFromJsonAsync<JsonElement>());
        data.GetProperty("aiDisclosure").GetString().Should().Be("No generative AI was used.");
        data.GetProperty("commercialRightsVerified").GetBoolean().Should().BeTrue();
        ChecklistItem(data.GetProperty("releaseReadiness"), "ai_disclosure")
            .GetProperty("status").GetString().Should().Be("complete");
        ChecklistItem(data.GetProperty("releaseReadiness"), "rights")
            .GetProperty("status").GetString().Should().Be("complete");
    }

    [Fact]
    public async Task NonOwner_CannotReadOrWriteAttestations()
    {
        var ctx = await CreateCreatorWithTrackAsync();

        var otherSeed = Guid.NewGuid().ToString("N");
        var otherEmail = $"readiness-intruder-{otherSeed}@cambrian.com";
        var other = await _fixture.CreateRoleClientAsync(otherEmail, Password, "Creator", $"intruder{otherSeed[..8]}");
        await _fixture.SetCreatorTierAsync(otherEmail, Cambrian.Domain.Enums.CreatorTier.Creator);

        (await other.GetAsync($"/creator/tracks/{ctx.TrackId}")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await other.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new { rightsConfirmed = true })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == ctx.TrackId);
        track.CommercialRightsVerified.Should().BeFalse("a non-owner must never be able to attest rights");
    }
}
