using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.PlayCounts;

/// <summary>
/// End-to-end coverage for POST /admin/play-counts/reconcile through the real HTTP stack
/// (routing, auth, DI, controller, service) — the unit-level PlayCountReconciliationTests cover
/// the service's behavior in depth; this proves it's wired up correctly end to end and that the
/// operation is admin-only.
/// </summary>
public sealed class PlayCountAdminEndpointTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public PlayCountAdminEndpointTests(CambrianApiFixture fixture) => _fixture = fixture;

    private async Task<HttpClient> CreateAdminAsync(string tag) =>
        await _fixture.CreateRoleClientAsync($"playcount-admin-{tag}@test.com", "Test1234!@", "Admin", $"pcadmin{tag}");

    [Fact]
    public async Task Reconcile_DryRun_ReportsDriftWithoutRepairing_ThenRepair_FixesIt()
    {
        var admin = await CreateAdminAsync("dryrun");
        var creatorEmail = "playcount-admin-dryrun-creator@test.com";
        await _fixture.RegisterUserAsync(creatorEmail);
        var creatorId = await _fixture.GetUserIdAsync(creatorEmail);
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Admin Reconcile Beat");

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            for (var i = 0; i < 4; i++)
            {
                db.StreamSessions.Add(new Cambrian.Domain.Entities.StreamSession
                {
                    Id = Guid.NewGuid(),
                    TrackId = trackId,
                    StartedAt = DateTime.UtcNow,
                    IdempotencyKey = Guid.NewGuid().ToString(),
                    Qualified = true,
                });
            }
            await db.SaveChangesAsync();
        }

        var dryRunResponse = await admin.PostAsync(
            $"/admin/play-counts/reconcile?dryRun=true&repair=false&trackIds={trackId}", content: null);
        dryRunResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var dryRunData = (await dryRunResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        dryRunData.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        dryRunData.GetProperty("mismatchesFound").GetInt32().Should().Be(1);
        dryRunData.GetProperty("mismatchesRepaired").GetInt32().Should().Be(0);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var stat = await db.TrackStats.AsNoTracking().FirstOrDefaultAsync(s => s.TrackId == trackId);
            (stat?.PlayCount ?? 0).Should().Be(0, "dry-run must not write anything");
        }

        var repairResponse = await admin.PostAsync(
            $"/admin/play-counts/reconcile?dryRun=false&repair=true&trackIds={trackId}", content: null);
        repairResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var repairData = (await repairResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        repairData.GetProperty("mismatchesRepaired").GetInt32().Should().Be(1);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var stat = await db.TrackStats.AsNoTracking().SingleAsync(s => s.TrackId == trackId);
            stat.PlayCount.Should().Be(4);
        }
    }

    [Fact]
    public async Task Reconcile_NonAdmin_Returns403_Anonymous_Returns401()
    {
        var trackId = Guid.NewGuid();

        var anonResponse = await _fixture.CreateClient().PostAsync($"/admin/play-counts/reconcile?trackIds={trackId}", content: null);
        anonResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var fanClient = await _fixture.CreateAuthenticatedClientAsync("playcount-admin-nonadmin@test.com", "Test1234!@");
        var fanResponse = await fanClient.PostAsync($"/admin/play-counts/reconcile?trackIds={trackId}", content: null);
        fanResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
