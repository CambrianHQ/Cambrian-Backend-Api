using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Xunit;

namespace Cambrian.Api.Tests;

public sealed class PlayReconciliationControllerTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public PlayReconciliationControllerTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DryRun_requires_authentication()
    {
        var response = await _fixture.CreateClient().PostAsJsonAsync(
            "/admin/play-reconciliation/dry-run",
            new { trackIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Repair_requires_admin_role()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            $"play-reconcile-user-{Guid.NewGuid():N}@example.test",
            "Test1234!@");

        var response = await client.PostAsJsonAsync(
            "/admin/play-reconciliation/repair",
            new
            {
                trackIds = Array.Empty<Guid>(),
                trackBatchSize = 1,
                eventBatchSize = 1,
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_can_run_a_bounded_dry_run()
    {
        var client = await _fixture.CreateRoleClientAsync(
            $"play-reconcile-admin-{Guid.NewGuid():N}@example.test",
            "Test1234!@",
            "Admin",
            $"reconcileadmin{Guid.NewGuid():N}"[..24]);

        var response = await client.PostAsJsonAsync(
            "/admin/play-reconciliation/dry-run",
            new
            {
                trackIds = Array.Empty<Guid>(),
                mismatchLimit = 1,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(0, data.GetProperty("selectedTrackCount").GetInt32());
        Assert.Equal(0, data.GetProperty("mismatchedTrackCount").GetInt32());
        Assert.Equal(JsonValueKind.Array, data.GetProperty("mismatches").ValueKind);
    }

    [Fact]
    public async Task Admin_health_details_exposes_play_aggregation_and_chart_freshness()
    {
        var client = await _fixture.CreateRoleClientAsync(
            $"play-health-admin-{Guid.NewGuid():N}@example.test",
            "Test1234!@",
            "Admin",
            $"playhealth{Guid.NewGuid():N}"[..24]);

        var response = await client.GetAsync("/health/details");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var playPipeline = json.GetProperty("playPipeline");
        Assert.True(playPipeline.TryGetProperty("qualifiedEventCount", out _));
        Assert.True(playPipeline.TryGetProperty("pendingAggregationCount", out _));
        Assert.True(playPipeline.TryGetProperty("oldestPendingQualifiedAtUtc", out _));
        Assert.True(playPipeline.TryGetProperty("aggregationLagSeconds", out _));
        Assert.True(playPipeline.TryGetProperty("latestChartDataThroughUtc", out _));
        Assert.True(playPipeline.TryGetProperty("chartDataAgeSeconds", out _));
        Assert.True(playPipeline.TryGetProperty("staleChartWindowCount", out _));
        Assert.True(playPipeline.TryGetProperty("legacyNonzeroTrendingScoreCount", out _));
    }
}
