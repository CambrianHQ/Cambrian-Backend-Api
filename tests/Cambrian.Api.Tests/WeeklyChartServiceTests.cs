using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class WeeklyChartServiceTests
{
    [Fact]
    public async Task Failed_lazy_refresh_preserves_current_week_snapshot_and_marks_it_stale()
    {
        var now = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var weekStart = StartOfIsoWeekUtc(now);
        var trackId = Guid.NewGuid();
        var existing = new[]
        {
            new WeeklyChartSnapshot
            {
                Id = Guid.NewGuid(),
                WeekStartUtc = weekStart,
                WeekEndUtc = weekStart.AddDays(7),
                Rank = 1,
                TrackId = trackId,
                CreatorId = "creator-1",
                Title = "Persisted winner",
                Artist = "Artist",
                WeeklyQualifiedPlays = 7,
                LifetimePlays = 19,
                Score = 7,
                PlaysInWindow = 7,
                Basis = WeeklyChartService.BasisWeeklyPlays,
                DataThroughUtc = now.AddMinutes(-2),
                ComputedAtUtc = now.AddMinutes(-2),
            },
        };

        var repo = Substitute.For<IWeeklyChartRepository>();
        repo.GetWeekAsync(weekStart, Arg.Any<CancellationToken>()).Returns(existing);
        repo.GetWeekAsync(weekStart.AddDays(-7), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WeeklyChartSnapshot>());
        repo.HasQualifiedPlaysAfterAsync(
                weekStart,
                weekStart.AddDays(7),
                existing[0].DataThroughUtc!.Value,
                now,
                Arg.Any<CancellationToken>())
            .Returns(false);
        repo.GetEligibleCandidatesAsync(
                weekStart,
                weekStart.AddDays(7),
                now,
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<WeeklyChartCandidate>>(
                new InvalidOperationException("simulated aggregation query failure")));

        using var provider = BuildProvider(repo);
        var service = CreateService(provider, new FixedTimeProvider(now));

        var response = await service.GetCurrentAsync();

        Assert.True(response.IsStale);
        var entry = Assert.Single(response.Entries);
        Assert.Equal(trackId.ToString(), entry.TrackId);
        Assert.Equal(7, entry.WeeklyQualifiedPlays);
        Assert.Equal(19, entry.LifetimePlays);
        await repo.DidNotReceiveWithAnyArgs()
            .ReplaceWeekAsync(default, default!, default);
    }

    [Fact]
    public async Task Missing_current_week_never_falls_back_to_latest_prior_week()
    {
        var now = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var weekStart = StartOfIsoWeekUtc(now);
        var repo = Substitute.For<IWeeklyChartRepository>();
        repo.GetWeekAsync(weekStart, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WeeklyChartSnapshot>());
        repo.GetWeekAsync(weekStart.AddDays(-7), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WeeklyChartSnapshot>());
        repo.GetEligibleCandidatesAsync(
                weekStart,
                weekStart.AddDays(7),
                now,
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<WeeklyChartCandidate>>(
                new InvalidOperationException("current-week aggregation unavailable")));

        using var provider = BuildProvider(repo);
        var service = CreateService(provider, new FixedTimeProvider(now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCurrentAsync());
        await repo.DidNotReceiveWithAnyArgs().GetLatestWeekAsync(default);
    }

    [Theory]
    [InlineData(2026, 3, 8, 7, 2026, 3, 2)]
    [InlineData(2026, 11, 1, 7, 2026, 10, 26)]
    public async Task Weekly_window_remains_monday_utc_across_us_daylight_saving_transitions(
        int year, int month, int day, int hour,
        int startYear, int startMonth, int startDay)
    {
        var now = new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc);
        var expectedStart = new DateTime(startYear, startMonth, startDay, 0, 0, 0, DateTimeKind.Utc);
        var repo = Substitute.For<IWeeklyChartRepository>();
        repo.GetWeekAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WeeklyChartSnapshot>());
        repo.GetEligibleCandidatesAsync(
                expectedStart,
                expectedStart.AddDays(7),
                now,
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WeeklyChartCandidate>());

        using var provider = BuildProvider(repo);
        var service = CreateService(provider, new FixedTimeProvider(now));

        var response = await service.AggregateAsync();

        Assert.Equal(expectedStart, response.ChartWindowStart);
        Assert.Equal(expectedStart.AddDays(7), response.ChartWindowEnd);
        await repo.Received(1).GetEligibleCandidatesAsync(
            expectedStart,
            expectedStart.AddDays(7),
            now,
            Arg.Any<CancellationToken>());
    }

    private static ServiceProvider BuildProvider(IWeeklyChartRepository repo)
    {
        return new ServiceCollection()
            .AddScoped(_ => repo)
            .BuildServiceProvider();
    }

    private static WeeklyChartService CreateService(ServiceProvider provider, TimeProvider clock)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Charts:Weekly:StaleAfterSeconds"] = "60",
            })
            .Build();

        return new WeeklyChartService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clock,
            configuration,
            NullLogger<WeeklyChartService>.Instance);
    }

    private static DateTime StartOfIsoWeekUtc(DateTime utc)
    {
        var date = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        var diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = new(utcNow);

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
