using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

[Trait("Category", "ReleaseReady")]
public sealed class MasteringJobLeaseTests : IClassFixture<RelationalCambrianApiFixture>
{
    private readonly RelationalCambrianApiFixture _fixture;

    public MasteringJobLeaseTests(RelationalCambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ExpiredProcessingJob_WithRetryRemaining_IsReclaimedWithNewLease()
    {
        await ClearMasteringJobsAsync();
        var oldLease = Guid.NewGuid();
        var jobId = await SeedJobAsync(new MasteringJob
        {
            CreatorId = "lease-reclaim-user",
            Status = "processing",
            RetryCount = 0,
            ProcessingLeaseId = oldLease,
            ProcessingLeaseExpiresAt = DateTime.UtcNow.AddMinutes(-5),
        });

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();

        var claimed = await repo.ClaimNextQueuedAsync();

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(jobId);
        claimed.Status.Should().Be("processing");
        claimed.RetryCount.Should().Be(1);
        claimed.ProcessingLeaseId.Should().NotBe(oldLease);
        claimed.ProcessingLeaseExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task ProcessingJob_WithValidLease_IsNotStolen()
    {
        await ClearMasteringJobsAsync();
        await SeedJobAsync(new MasteringJob
        {
            CreatorId = "lease-valid-user",
            Status = "processing",
            ProcessingLeaseId = Guid.NewGuid(),
            ProcessingLeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();

        var claimed = await repo.ClaimNextQueuedAsync();

        claimed.Should().BeNull();
    }

    [Fact]
    public async Task QueuedJob_SurvivesRestart_AndIsClaimableWithLease()
    {
        await ClearMasteringJobsAsync();
        var jobId = await SeedJobAsync(new MasteringJob
        {
            CreatorId = "lease-restart-user",
            Status = "queued",
        });

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();

        var claimed = await repo.ClaimNextQueuedAsync();

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(jobId);
        claimed.Status.Should().Be("processing");
        claimed.ProcessingLeaseId.Should().NotBeNull();
        claimed.ProcessingLeaseExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task ExpiredProcessingJob_AtRetryLimit_BecomesFailedAndReleasesCredit()
    {
        await ClearMasteringJobsAsync();
        var creatorId = "lease-credit-user";
        var jobId = await SeedJobAsync(new MasteringJob
        {
            CreatorId = creatorId,
            Status = "processing",
            RetryCount = 1,
            ChargedAt = DateTime.UtcNow,
            CreditSource = "monthly",
            ProcessingLeaseId = Guid.NewGuid(),
            ProcessingLeaseExpiresAt = DateTime.UtcNow.AddMinutes(-5),
        });

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();
            (await repo.ClaimNextQueuedAsync()).Should().BeNull("the only claimable job was terminally failed");
        }

        var job = await GetJobAsync(jobId);
        job.Status.Should().Be("failed");
        job.Error.Should().Contain("lease expired");

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();
            var used = await repo.CountChargedThisMonthAsync(
                creatorId,
                new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc));
            used.Should().Be(0, "failed stale jobs are excluded from derived credit usage");
        }
    }

    [Fact]
    public async Task TerminalTransition_RequiresActiveLease_AndOnlyWinsOnce()
    {
        await ClearMasteringJobsAsync();
        var leaseId = Guid.NewGuid();
        var jobId = await SeedJobAsync(new MasteringJob
        {
            CreatorId = "lease-terminal-user",
            Status = "processing",
            ProcessingLeaseId = leaseId,
            ProcessingLeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            MasteredWavKey = "release-ready/master/test/master.wav",
            MasteredMp3Key = "release-ready/master/test/master.mp3",
        });

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();
        var job = await repo.GetAsync(jobId) ?? throw new InvalidOperationException("Seeded job missing.");

        (await repo.MarkDoneAsync(job, Guid.NewGuid())).Should().BeFalse("a non-owner lease cannot complete the job");
        (await repo.MarkDoneAsync(job, leaseId)).Should().BeTrue();
        (await repo.MarkFailedAsync(jobId, leaseId, "late failure")).Should().BeFalse("terminal jobs cannot be overwritten");

        var saved = await GetJobAsync(jobId);
        saved.Status.Should().Be("done");
        saved.Error.Should().BeNull();
    }

    [Fact]
    public async Task FailedJob_CannotBeOverwrittenByLateWorker()
    {
        await ClearMasteringJobsAsync();
        var leaseId = Guid.NewGuid();
        var jobId = await SeedJobAsync(new MasteringJob
        {
            CreatorId = "lease-failed-late-user",
            Status = "failed",
            Error = "terminal failure",
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            ProcessingLeaseId = leaseId,
            ProcessingLeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();

        var staleJob = await repo.GetAsync(jobId) ?? throw new InvalidOperationException("Seeded job missing.");
        staleJob.MasteredWavKey = "release-ready/master/late/master.wav";
        staleJob.MasteredMp3Key = "release-ready/master/late/master.mp3";

        (await repo.MarkDoneAsync(staleJob, leaseId)).Should().BeFalse();
        (await repo.MarkFailedAsync(jobId, leaseId, "late failure")).Should().BeFalse();

        var saved = await GetJobAsync(jobId);
        saved.Status.Should().Be("failed");
        saved.Error.Should().Be("terminal failure");
        saved.MasteredWavKey.Should().BeNull();
        saved.MasteredMp3Key.Should().BeNull();
    }

    [Fact]
    public async Task TerminalJobs_AreNeverReprocessed()
    {
        await ClearMasteringJobsAsync();
        await SeedJobAsync(new MasteringJob
        {
            CreatorId = "lease-done-user",
            Status = "done",
            CompletedAt = DateTime.UtcNow,
        });
        await SeedJobAsync(new MasteringJob
        {
            CreatorId = "lease-terminal-failed-user",
            Status = "failed",
            CompletedAt = DateTime.UtcNow,
            ProcessingLeaseExpiresAt = DateTime.UtcNow.AddMinutes(-30),
        });

        await using var scope = _fixture.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();

        var claimed = await repo.ClaimNextQueuedAsync();

        claimed.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentClaims_OnlyOneWorkerOwnsQueuedJob()
    {
        await ClearMasteringJobsAsync();
        var jobId = await SeedJobAsync(new MasteringJob
        {
            CreatorId = "lease-race-user",
            Status = "queued",
        });

        var claims = await Task.WhenAll(ClaimInNewScopeAsync(), ClaimInNewScopeAsync());

        claims.Count(j => j?.Id == jobId).Should().Be(1);
        claims.Count(j => j is null).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentTerminalTransitions_OnlyOneWins()
    {
        await ClearMasteringJobsAsync();
        var leaseId = Guid.NewGuid();
        var jobId = await SeedJobAsync(new MasteringJob
        {
            CreatorId = "lease-terminal-race-user",
            Status = "processing",
            ProcessingLeaseId = leaseId,
            ProcessingLeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });

        var results = await Task.WhenAll(
            MarkDoneInNewScopeAsync(jobId, leaseId),
            MarkFailedInNewScopeAsync(jobId, leaseId));

        results.Count(won => won).Should().Be(1);
        var saved = await GetJobAsync(jobId);
        saved.Status.Should().BeOneOf("done", "failed");
        saved.ProcessingLeaseId.Should().BeNull();
        saved.ProcessingLeaseExpiresAt.Should().BeNull();
    }

    private async Task<MasteringJob?> ClaimInNewScopeAsync()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();
        return await repo.ClaimNextQueuedAsync();
    }

    private async Task<bool> MarkDoneInNewScopeAsync(Guid jobId, Guid leaseId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();
        var job = await repo.GetAsync(jobId) ?? throw new InvalidOperationException("Seeded job missing.");
        job.MasteredWavKey = "release-ready/master/race/master.wav";
        job.MasteredMp3Key = "release-ready/master/race/master.mp3";
        return await repo.MarkDoneAsync(job, leaseId);
    }

    private async Task<bool> MarkFailedInNewScopeAsync(Guid jobId, Guid leaseId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();
        return await repo.MarkFailedAsync(jobId, leaseId, "race failure");
    }

    private async Task<Guid> SeedJobAsync(MasteringJob job)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        job.Id = job.Id == Guid.Empty ? Guid.NewGuid() : job.Id;
        job.Engine = string.IsNullOrWhiteSpace(job.Engine) ? "ffmpeg" : job.Engine;
        job.Kind = string.IsNullOrWhiteSpace(job.Kind) ? "mastering" : job.Kind;
        job.SourceKey = string.IsNullOrWhiteSpace(job.SourceKey) ? $"release-ready/source/{job.Id}.wav" : job.SourceKey;
        job.SourceFileName = string.IsNullOrWhiteSpace(job.SourceFileName) ? "audio.wav" : job.SourceFileName;
        job.CreatedAt = DateTime.UtcNow;
        db.MasteringJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task ClearMasteringJobsAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        await db.MasteringJobs.ExecuteDeleteAsync();
    }

    private async Task<MasteringJob> GetJobAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.MasteringJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
    }
}
