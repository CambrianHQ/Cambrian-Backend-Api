using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Cambrian.Api.Tests.Unit.Application;

/// <summary>
/// A transient serialization/deadlock conflict (Postgres 40001/40P01) surfacing from a concurrent
/// last-credit charge is retried by <see cref="ReleaseCreditService.TryChargeAsync"/> to a clean
/// result instead of a 500, and a genuine (non-transient) error is never retried. The retried
/// attempt re-checks idempotency under the per-creator advisory lock, so it cannot double-charge.
/// </summary>
public sealed class ReleaseCreditServiceRetryTests
{
    private readonly IMasteringJobRepository _jobs = Substitute.For<IMasteringJobRepository>();
    private readonly ITransactionManager _tx = Substitute.For<ITransactionManager>();
    private readonly ReleaseCreditService _service;

    public ReleaseCreditServiceRetryTests()
    {
        var users = Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null);
        users.FindByIdAsync(Arg.Any<string>())
            .Returns(new ApplicationUser { Id = "u1", CreatorTier = CreatorTier.Creator });

        _tx.BeginTransactionAsync().Returns(Substitute.For<IAsyncDisposable>());

        // A chargeable job with monthly allowance available, so the path reaches commit.
        _jobs.GetForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MasteringJob { Id = Guid.NewGuid(), CreatorId = "u1", ChargedAt = null });
        _jobs.CountChargedThisMonthAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(0);

        _service = new ReleaseCreditService(
            users, _jobs,
            Substitute.For<IReleaseCreditPurchaseRepository>(),
            Substitute.For<IPaymentGateway>(),
            Substitute.For<IConfiguration>(),
            _tx, TimeProvider.System,
            Substitute.For<ILogger<ReleaseCreditService>>());
    }

    [Fact]
    public async Task TryCharge_SerializationConflictThenSuccess_RetriesAndSucceeds()
    {
        var commitCalls = 0;
        _tx.CommitAsync().Returns(_ =>
        {
            if (++commitCalls == 1) throw new FakeSerializationException(); // 40001 on first commit
            return Task.CompletedTask;
        });

        var ok = await _service.TryChargeAsync(Guid.NewGuid(), "u1");

        Assert.True(ok);
        await _tx.Received(2).CommitAsync(); // retried exactly once
    }

    [Fact]
    public async Task TryCharge_NonTransientError_DoesNotRetry()
    {
        _tx.CommitAsync().Returns(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.TryChargeAsync(Guid.NewGuid(), "u1"));
        await _tx.Received(1).CommitAsync(); // no retry on a real error
    }

    private sealed class FakeSerializationException : Exception
    {
        // Mirrors Npgsql.PostgresException.SqlState, which the retry guard reflects on.
        public string SqlState => "40001";
    }
}
