using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cambrian.Api.Tests.Fixtures;

/// <summary>
/// Real application host with a controllable UTC clock. It keeps the relational
/// fixture's PostgreSQL-first behavior while preventing qualified-play tests from
/// sleeping or trusting client timestamps.
/// </summary>
public sealed class QualifiedPlaybackApiFixture : RelationalCambrianApiFixture
{
    public ManualTimeProvider Clock { get; } = new(
        new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            // PostHog is a post-commit projection only. Keep these database tests
            // deterministic and offline while still exercising the real play service.
            services.RemoveAll<IPlaybackAnalyticsService>();
            services.AddSingleton<IPlaybackAnalyticsService, NoOpPlaybackAnalyticsService>();
        });
    }
}

public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow) => SetUtcNow(utcNow);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        if (utcNow.Offset != TimeSpan.Zero)
            throw new ArgumentException("The test clock must be set with a UTC value.", nameof(utcNow));

        lock (_sync)
        {
            _utcNow = utcNow;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_sync)
        {
            _utcNow = _utcNow.Add(by);
        }
    }
}

internal sealed class NoOpPlaybackAnalyticsService : IPlaybackAnalyticsService
{
    public Task CaptureAcceptedAsync(PlaybackAnalyticsEvent playEvent, CancellationToken ct = default) =>
        Task.CompletedTask;
}
