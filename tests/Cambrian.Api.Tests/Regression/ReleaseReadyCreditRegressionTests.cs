using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Regression;

/// <summary>
/// Regression guard for the frozen Release Ready credit rule: a creator at their
/// monthly allowance MUST be blocked from submitting (no free master, no job slipping
/// to 'queued'). Exercised end-to-end through the HTTP controller so the
/// InsufficientCreditsException → 403 mapping can never silently regress.
/// </summary>
[Trait("Category", "Critical")]
public sealed class ReleaseReadyCreditRegressionTests : IClassFixture<RelationalCambrianApiFixture>
{
    private readonly RelationalCambrianApiFixture _fixture;

    public ReleaseReadyCreditRegressionTests(RelationalCambrianApiFixture fixture) => _fixture = fixture;

    private const int CreatorAllowance = 3; // TierManifest.Creator.ReleaseReadyCreditsPerMonth

    [Fact]
    public async Task Submit_AtMonthlyAllowance_Returns403_AndJobNeverQueues()
    {
        var email = $"rr-block-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        await SetCreatorTierAsync(userId);

        // Exhaust the full monthly allowance with already-charged jobs this month.
        for (var i = 0; i < CreatorAllowance; i++)
            await SeedJobAsync(userId, status: "queued", chargedAt: DateTime.UtcNow);

        // A fresh validated job the creator now tries to submit with 0 credits left.
        var jobId = await SeedJobAsync(userId, status: "validated");

        var response = await client.PostAsync($"/release-ready/jobs/{jobId}/submit", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a creator with no remaining credits must be refused (insufficient_credits → 403)");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();

        // The denied job must NOT have advanced to 'queued' and must NOT have been charged.
        var job = await GetJobAsync(jobId);
        job.Status.Should().Be("validated", "a blocked submit must not push the job into the queue");
        job.ChargedAt.Should().BeNull("a refused charge must not stamp the credit ledger");

        // And the month's charged, non-failed count is unchanged at the allowance.
        await AssertChargedNonFailedCountAsync(userId, CreatorAllowance);
    }

    // ── Helpers ──

    private async Task SetCreatorTierAsync(string userId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        user.CreatorTier = CreatorTier.Creator;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedJobAsync(string userId, string status, DateTime? chargedAt = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var jobId = Guid.NewGuid();
        db.MasteringJobs.Add(new MasteringJob
        {
            Id = jobId,
            CreatorId = userId,
            Engine = "ffmpeg",
            Status = status,
            SourceKey = $"release-ready/source/{jobId}.wav",
            SourceFileName = "audio.wav",
            ChargedAt = chargedAt,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return jobId;
    }

    private async Task<MasteringJob> GetJobAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.MasteringJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
    }

    private async Task AssertChargedNonFailedCountAsync(string userId, int expected)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var count = await db.MasteringJobs.CountAsync(
            j => j.CreatorId == userId
                 && j.ChargedAt != null
                 && j.ChargedAt >= monthStart
                 && j.Status != "failed");
        count.Should().Be(expected);
    }
}
