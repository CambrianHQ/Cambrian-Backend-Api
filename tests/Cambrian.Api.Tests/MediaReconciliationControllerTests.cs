using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// HTTP-level coverage for the admin media reconciliation endpoints.
/// POST is fire-and-forget: it returns 202 and executes the scan on a fresh DI
/// scope guarded by the process-wide <see cref="MediaReconciliationRunGuard"/>.
/// Because that guard is static across the whole test process, every test that
/// touches it acquires with bounded retries and releases in a finally block,
/// keeping the hold window tiny so parallel test classes are never starved.
/// </summary>
public sealed class MediaReconciliationControllerTests : IClassFixture<CambrianApiFixture>
{
    private const string RunsUrl = "/admin/media-reconciliation/runs";
    private const string SecretObjectKey = "tracks/secret-key-abc123.mp3";

    private readonly CambrianApiFixture _fixture;

    public MediaReconciliationControllerTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Endpoints_require_authentication()
    {
        var client = _fixture.CreateClient();

        var post = await client.PostAsJsonAsync(RunsUrl, new { remediate = false });
        var list = await client.GetAsync(RunsUrl);
        var single = await client.GetAsync($"{RunsUrl}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, single.StatusCode);
    }

    [Fact]
    public async Task Endpoints_require_admin_role()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            $"media-reconcile-user-{Guid.NewGuid():N}@example.test",
            "Test1234!@");

        var post = await client.PostAsJsonAsync(RunsUrl, new { remediate = false });
        var list = await client.GetAsync(RunsUrl);

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
    }

    [Fact]
    public async Task Admin_run_is_accepted_and_report_never_exposes_object_keys()
    {
        var email = $"media-reconcile-admin-{Guid.NewGuid():N}@example.test";
        var client = await _fixture.CreateRoleClientAsync(
            email, "Test1234!@", "Admin", $"mediarecadmin{Guid.NewGuid():N}"[..24]);
        var userId = await _fixture.GetUserIdAsync(email);
        // A distinctive object key that must never appear in any API response,
        // plus the matching storage object so the run can complete normally.
        await SeedTrackWithMediaAsync(userId, SecretObjectKey);
        await SeedStorageObjectAsync(SecretObjectKey);

        await WaitUntilRunGuardIsFreeAsync();
        var response = await client.PostAsJsonAsync(RunsUrl, new { remediate = false });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("data").GetProperty("runId").GetGuid();

        var (body, report) = await PollUntilTerminalAsync(client, runId);

        var run = report.GetProperty("data").GetProperty("run");
        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.True(run.GetProperty("tracksInspected").GetInt32() >= 1);
        // The seeded media row has no duration, so at least one finding exists —
        // proving the leak assertion below inspects a non-trivial report.
        Assert.True(report.GetProperty("data").GetProperty("findings").GetArrayLength() >= 1);
        Assert.DoesNotContain("secret-key-abc123", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_run_id_returns_404()
    {
        var client = await CreateAdminClientAsync();

        var response = await client.GetAsync($"{RunsUrl}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("reconciliation_run_not_found", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_returns_409_while_another_run_holds_the_guard()
    {
        var client = await CreateAdminClientAsync();

        await AcquireRunGuardAsync();
        try
        {
            var response = await client.PostAsJsonAsync(RunsUrl, new { remediate = false });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal("reconciliation_run_already_active", json.GetProperty("error").GetString());
        }
        finally
        {
            MediaReconciliationRunGuard.Release();
        }
    }

    [Fact]
    public async Task Admin_list_includes_the_created_run()
    {
        var client = await CreateAdminClientAsync();

        await WaitUntilRunGuardIsFreeAsync();
        var post = await client.PostAsJsonAsync(RunsUrl, new { remediate = false });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var created = await post.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("data").GetProperty("runId").GetGuid();

        var list = await client.GetAsync(RunsUrl);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var json = await list.Content.ReadFromJsonAsync<JsonElement>();
        var runs = json.GetProperty("data").EnumerateArray().ToList();
        Assert.Contains(runs, run => run.GetProperty("runId").GetGuid() == runId);

        // Let the background execution reach a terminal state so the fixture
        // never disposes the shared SQLite connection under an in-flight run.
        await PollUntilTerminalAsync(client, runId);
    }

    // ----- Helpers -----

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var email = $"media-reconcile-admin-{Guid.NewGuid():N}@example.test";
        return await _fixture.CreateRoleClientAsync(
            email, "Test1234!@", "Admin", $"mediarecadmin{Guid.NewGuid():N}"[..24]);
    }

    private async Task SeedTrackWithMediaAsync(string creatorId, string objectKey)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var trackId = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = trackId,
            CambrianTrackId = $"CAMB-TRK-{trackId.ToString("N")[..8].ToUpperInvariant()}",
            Title = "Reconciliation Seed",
            CreatorId = creatorId,
            Visibility = "hidden",
            AudioUrl = objectKey,
        });
        db.TrackMedia.Add(new TrackMedia
        {
            TrackId = trackId,
            ObjectKey = objectKey,
            State = TrackMediaStates.Uploaded,
            StateChangedAtUtc = DateTime.UtcNow,
            ContentType = "audio/mpeg",
            ConcurrencyToken = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedStorageObjectAsync(string objectKey)
    {
        var storage = _fixture.Services.GetRequiredService<IObjectStorage>();
        using var stream = new MemoryStream(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
        await storage.UploadAsync(stream, objectKey, "audio/mpeg");
    }

    /// <summary>Bounded poll of GET runs/{id} until the run leaves "running".</summary>
    private static async Task<(string Body, JsonElement Report)> PollUntilTerminalAsync(
        HttpClient client, Guid runId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            var response = await client.GetAsync($"{RunsUrl}/{runId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var report = JsonSerializer.Deserialize<JsonElement>(body);
            var status = report.GetProperty("data").GetProperty("run").GetProperty("status").GetString();
            if (status is "completed" or "failed" or "cancelled")
                return (body, report);
        }

        throw new TimeoutException($"Reconciliation run {runId} never reached a terminal status.");
    }

    /// <summary>
    /// Acquire the process-wide run guard with bounded retries — a previous
    /// test's background run may still be releasing it. Callers must release.
    /// </summary>
    private static async Task AcquireRunGuardAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (MediaReconciliationRunGuard.TryAcquire())
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("MediaReconciliationRunGuard was never released by a previous run.");
    }

    private static async Task WaitUntilRunGuardIsFreeAsync()
    {
        await AcquireRunGuardAsync();
        MediaReconciliationRunGuard.Release();
    }
}
