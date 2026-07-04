using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Subscription expiry enforcement (comp-grant prerequisite). A subscription
/// only grants a tier while Status='active' AND it is not past ExpiresAt.
/// Bug proven on prod: two comped Pro subs sat past their ExpiresAt but still
/// resolved as active, so tier never lapsed — and a "6-month" grant would have
/// been permanent. GetActiveAsync now enforces expiry at read time; the sweep
/// keeps stored Status truthful.
/// </summary>
public sealed class SubscriptionExpiryTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public SubscriptionExpiryTests(CambrianApiFixture fixture) => _fixture = fixture;

    private static Subscription Sub(string userId, string plan, string status, DateTime? expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Plan = plan,
        Status = status,
        StartedAt = DateTime.UtcNow.AddMonths(-6),
        ExpiresAt = expiresAt,
    };

    [Fact]
    public async Task GetActiveAsync_excludes_a_lapsed_active_subscription()
    {
        var userId = $"expiry-lapsed-{Guid.NewGuid():N}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        // active by Status, but ExpiresAt is in the past → must NOT grant tier.
        db.Subscriptions.Add(Sub(userId, "pro", "active", DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        var repo = new SubscriptionRepository(db);
        Assert.Null(await repo.GetActiveAsync(userId));
    }

    [Fact]
    public async Task GetActiveAsync_returns_a_future_dated_and_a_perpetual_subscription()
    {
        var future = $"expiry-future-{Guid.NewGuid():N}";
        var perpetual = $"expiry-perp-{Guid.NewGuid():N}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.Subscriptions.Add(Sub(future, "creator", "active", DateTime.UtcNow.AddMonths(6)));
        db.Subscriptions.Add(Sub(perpetual, "creator", "active", null)); // no end date
        await db.SaveChangesAsync();

        var repo = new SubscriptionRepository(db);
        var f = await repo.GetActiveAsync(future);
        var p = await repo.GetActiveAsync(perpetual);
        Assert.NotNull(f);
        Assert.Equal("creator", f!.Plan);
        Assert.NotNull(p);
        Assert.Equal("creator", p!.Plan);
    }

    [Fact]
    public async Task ExpireLapsedAsync_flips_only_past_active_subs_to_expired()
    {
        var lapsed = $"sweep-lapsed-{Guid.NewGuid():N}";
        var future = $"sweep-future-{Guid.NewGuid():N}";
        var perpetual = $"sweep-perp-{Guid.NewGuid():N}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var lapsedSub = Sub(lapsed, "pro", "active", DateTime.UtcNow.AddDays(-2));
        db.Subscriptions.Add(lapsedSub);
        db.Subscriptions.Add(Sub(future, "creator", "active", DateTime.UtcNow.AddMonths(1)));
        db.Subscriptions.Add(Sub(perpetual, "creator", "active", null));
        await db.SaveChangesAsync();

        var repo = new SubscriptionRepository(db);
        var count = await repo.ExpireLapsedAsync(DateTime.UtcNow);

        Assert.True(count >= 1);
        var reloaded = await db.Subscriptions.FindAsync(lapsedSub.Id);
        Assert.Equal("expired", reloaded!.Status);
        // Future + perpetual remain active.
        Assert.NotNull(await repo.GetActiveAsync(future));
        Assert.NotNull(await repo.GetActiveAsync(perpetual));
    }
}
