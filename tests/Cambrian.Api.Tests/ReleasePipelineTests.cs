using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Fixture with a deterministic in-memory mastering engine so the in-process
/// MasteringWorker can run release-pipeline jobs end-to-end without ffmpeg.
/// The engine fails when the source file name contains "fail" (failure-path tests).
/// </summary>
public class ReleasePipelineFixture : CambrianApiFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMasteringEngine>();
            services.AddSingleton<IMasteringEngine, FakeMasteringEngine>();
        });
    }

    private sealed class FakeMasteringEngine : IMasteringEngine
    {
        public string Name => "fake";
        public bool RequiresApproval => false;

        public Task<MasteringEngineResult> MasterAsync(MasteringEngineRequest request, CancellationToken ct = default)
        {
            if (request.SourceFileName.Contains("fail", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Synthetic mastering failure.");

            return Task.FromResult(new MasteringEngineResult
            {
                Wav = new byte[] { 0x52, 0x49, 0x46, 0x46 },
                Mp3 = new byte[] { 0xFF, 0xFB, 0x90, 0x00 },
                InputLufs = -18.2,
                OutputLufs = -14.0,
                OutputTruePeakDbtp = -1.0,
            });
        }

        public Task<MasteringEngineResult> FinalizeAsync(MasteringEngineRequest request, string engineRef, CancellationToken ct = default)
            => MasterAsync(request, ct);
    }
}

public class ReleasePipelineTests : IClassFixture<ReleasePipelineFixture>
{
    private readonly ReleasePipelineFixture _fixture;

    public ReleasePipelineTests(ReleasePipelineFixture fixture) => _fixture = fixture;

    // ── Readiness scoring ──

    [Fact]
    public async Task Readiness_ReturnsWeightedChecks_ForBareTrack()
    {
        var email = $"readiness-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await SeedTrackAsync(userId, t => { t.CoverArtUrl = null; });

        var res = await client.GetAsync($"/api/tracks/{trackId}/readiness");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var checks = data.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("key").GetString()!, c => c.GetProperty("status").GetString()!);

        Assert.Equal(5, checks.Count);
        Assert.Equal("fail", checks["loudness"]);     // no mastering measurement yet
        Assert.Equal("fail", checks["aiDisclosure"]); // no disclosure on file
        Assert.Equal("fail", checks["cover"]);        // no cover art
        Assert.Equal("fail", checks["provenance"]);   // no stamp
        Assert.True(data.GetProperty("score").GetInt32() < 50);
    }

    [Fact]
    public async Task Readiness_OtherUsersTrack_Returns404()
    {
        var ownerEmail = $"owner-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.CreateAuthenticatedClientAsync(ownerEmail);
        var ownerId = await _fixture.GetUserIdAsync(ownerEmail);
        var trackId = await SeedTrackAsync(ownerId);

        var stranger = await _fixture.CreateAuthenticatedClientAsync($"stranger-{Guid.NewGuid():N}@cambrian.com");
        var res = await stranger.GetAsync($"/api/tracks/{trackId}/readiness");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Readiness_CacheInvalidates_WhenAuthorshipChanges()
    {
        var email = $"cacheinv-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await SeedTrackAsync(userId);

        // Prime the cache: disclosure fails.
        var before = await GetChecksAsync(client, trackId);
        Assert.Equal("fail", before["aiDisclosure"]);

        // Upsert authorship (the invalidation path under test).
        using (var scope = _fixture.Services.CreateScope())
        {
            var tracks = scope.ServiceProvider.GetRequiredService<ITrackRepository>();
            var authorship = scope.ServiceProvider.GetRequiredService<IAuthorshipService>();
            var track = await tracks.GetByIdAsync(trackId);
            await authorship.UpsertAsync(track!, new Cambrian.Application.DTOs.Provenance.TrackAuthorshipRequest
            {
                AiDisclosure = "Suno v4 used for initial sketch; all arrangement and lyrics human.",
                LyricsAuthored = true,
            });
        }

        var after = await GetChecksAsync(client, trackId);
        Assert.Equal("pass", after["aiDisclosure"]);
    }

    // ── Release-ready jobs ──

    [Fact]
    public async Task ReleaseReady_DebitsCredit_RunsAllStages_ProducesSignedArtifacts()
    {
        var email = $"pipeline-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        await _fixture.SetCreatorTierAsync(email, Cambrian.Domain.Enums.CreatorTier.Pro);
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await SeedTrackAsync(userId);

        var start = await client.PostAsync($"/api/tracks/{trackId}/release-ready", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("jobId").GetGuid();

        var job = await WaitForJobAsync(client, jobId, "done");

        Assert.Equal("done", job.GetProperty("status").GetString());

        var stages = job.GetProperty("stages").EnumerateArray()
            .Where(s => s.GetProperty("status").GetString() == "completed")
            .Select(s => s.GetProperty("stage").GetString())
            .ToList();
        Assert.Contains("mastering", stages);
        Assert.Contains("metadata", stages);
        Assert.Contains("cover", stages);
        Assert.Contains("disclosure", stages);
        Assert.Contains("provenance", stages);

        var artifacts = job.GetProperty("artifacts").EnumerateArray()
            .ToDictionary(a => a.GetProperty("kind").GetString()!, a => a.GetProperty("url").GetString()!);
        Assert.Contains("master_wav", artifacts.Keys);
        Assert.Contains("master_mp3", artifacts.Keys);
        Assert.Contains("disclosure", artifacts.Keys);
        Assert.All(artifacts.Values, url => Assert.StartsWith("https://", url));

        // Exactly one credit consumed.
        Assert.Equal(1, await CountChargedJobsAsync(userId));

        // The provenance stage stamped the track.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var track = await db.Tracks.AsNoTracking().FirstAsync(t => t.Id == trackId);
        Assert.False(string.IsNullOrEmpty(track.ContentHash));
        Assert.False(string.IsNullOrEmpty(track.Signature));
    }

    [Fact]
    public async Task ReleaseReady_FreeTier_Returns402()
    {
        var email = $"nocredits-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await SeedTrackAsync(userId);

        var res = await client.PostAsync($"/api/tracks/{trackId}/release-ready", null);
        Assert.Equal(HttpStatusCode.PaymentRequired, res.StatusCode);
    }

    [Fact]
    public async Task ReleaseReady_SameAudioRerun_Warns_WithoutDoubleCharge()
    {
        var email = $"idem-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        await _fixture.SetCreatorTierAsync(email, Cambrian.Domain.Enums.CreatorTier.Pro);
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await SeedTrackAsync(userId);

        var first = await client.PostAsync($"/api/tracks/{trackId}/release-ready", null);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstJobId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("jobId").GetGuid();

        var second = await client.PostAsync($"/api/tracks/{trackId}/release-ready", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstJobId, body.GetProperty("data").GetProperty("jobId").GetGuid());
        Assert.False(body.GetProperty("data").GetProperty("created").GetBoolean());

        Assert.Equal(1, await CountChargedJobsAsync(userId));
    }

    [Fact]
    public async Task ReleaseReady_StageFailure_MarksFailed_AndReleasesCredit()
    {
        var email = $"failure-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        await _fixture.SetCreatorTierAsync(email, Cambrian.Domain.Enums.CreatorTier.Pro);
        var userId = await _fixture.GetUserIdAsync(email);
        // "fail" in the audio file name makes the fake engine throw.
        var trackId = await SeedTrackAsync(userId, t => t.AudioUrl = "tracks/fail-source.mp3");

        var start = await client.PostAsync($"/api/tracks/{trackId}/release-ready", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("jobId").GetGuid();

        // Credit is consumed up front…
        Assert.Equal(1, await CountChargedJobsAsync(userId));

        var job = await WaitForJobAsync(client, jobId, "failed");
        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(job.GetProperty("error").GetString()));

        // …and automatically released on terminal failure (failed jobs leave the count).
        Assert.Equal(0, await CountChargedJobsAsync(userId));
    }

    // ── Helpers ──

    private async Task<Guid> SeedTrackAsync(string creatorId, Action<Track>? customize = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            CambrianTrackId = $"CAMB-TRK-{trackId.ToString()[..8].ToUpper()}",
            Title = "Release Pipeline Test",
            Description = "A complete description.",
            Genre = "Hip-Hop",
            Mood = "dark",
            Tempo = "140",
            Price = 29.99m,
            LicenseType = "standard",
            AudioUrl = $"tracks/{trackId:N}.mp3",
            CoverArtUrl = "covers/test-cover.jpg",
            CreatorId = creatorId,
        };
        customize?.Invoke(track);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return trackId;
    }

    private async Task<Dictionary<string, string>> GetChecksAsync(HttpClient client, Guid trackId)
    {
        var res = await client.GetAsync($"/api/tracks/{trackId}/readiness");
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        return data.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("key").GetString()!, c => c.GetProperty("status").GetString()!);
    }

    /// <summary>Poll GET /api/jobs/{id} until the worker reaches a terminal status.</summary>
    private static async Task<JsonElement> WaitForJobAsync(HttpClient client, Guid jobId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            var res = await client.GetAsync($"/api/jobs/{jobId}");
            res.EnsureSuccessStatusCode();
            last = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var status = last.GetProperty("status").GetString();
            if (status == expected || status is "done" or "failed")
                return last;
            await Task.Delay(500);
        }
        return last;
    }

    private async Task<int> CountChargedJobsAsync(string userId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.MasteringJobs.CountAsync(
            j => j.CreatorId == userId && j.ChargedAt != null && j.Status != "failed");
    }
}
