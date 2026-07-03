using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <summary>
/// Weekly creator digest (creator-audit fix 10) — plays / followers / top track
/// / unused Release Ready credits for the past week, plus upload + share CTAs.
///
/// Skip policy (never spam):
///  - unverified email → skipped (they never proved they own the inbox);
///  - WeeklyDigestOptOut → skipped (one-click unsubscribe endpoint sets it);
///  - LastWeeklyDigestAtUtc inside the current week → skipped (idempotent).
///
/// Dry-run computes and logs everything but sends nothing and stamps nothing.
/// </summary>
public sealed class WeeklyDigestService : IWeeklyDigestService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<WeeklyDigestService> _logger;

    public WeeklyDigestService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<WeeklyDigestService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<WeeklyDigestRunResult> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWeeklyDigestRepository>();
        var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();

        var now = DateTime.UtcNow;
        var weekStart = StartOfIsoWeekUtc(now);
        // The digest reports the COMPLETED week before the current one.
        var reportFrom = weekStart.AddDays(-7);
        var reportTo = weekStart;

        var result = new WeeklyDigestRunResult { DryRun = dryRun, WeekStartUtc = weekStart };
        var recipients = new List<string>();

        var candidates = await repo.GetCreatorCandidatesAsync(ct);
        result.Eligible = candidates.Count;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!candidate.EmailVerified)
            {
                result.SkippedUnverified++;
                continue;
            }
            if (candidate.WeeklyDigestOptOut)
            {
                result.SkippedOptedOut++;
                continue;
            }
            if (candidate.LastWeeklyDigestAtUtc is DateTime last && last >= weekStart)
            {
                result.SkippedAlreadySent++;
                continue;
            }

            try
            {
                var numbers = await repo.GetWeeklyNumbersAsync(candidate.UserId, reportFrom, reportTo, ct);

                var unusedCredits = 0;
                try
                {
                    var status = await credits.GetStatusAsync(candidate.UserId, ct);
                    unusedCredits = Math.Max(0, status.Remaining);
                }
                catch
                {
                    // Credits are a nice-to-have in the digest — never block on them.
                }

                var stats = new CreatorDigestStats
                {
                    UserId = candidate.UserId,
                    Email = candidate.Email,
                    DisplayName = candidate.DisplayName,
                    PlaysThisWeek = numbers.Plays,
                    NewFollowers = numbers.NewFollowers,
                    TopTrackTitle = numbers.TopTrackTitle,
                    TopTrackPlays = numbers.TopTrackPlays,
                    UnusedReleaseCredits = unusedCredits,
                };

                if (dryRun)
                {
                    _logger.LogInformation(
                        "EVENT: WeeklyDigestDryRun to:{Email} plays:{Plays} follows:{Follows} credits:{Credits}",
                        stats.Email, stats.PlaysThisWeek, stats.NewFollowers, stats.UnusedReleaseCredits);
                }
                else
                {
                    var html = BuildDigestHtml(stats, BuildUnsubscribeUrl(candidate.UserId));
                    await email.SendAsync(candidate.Email, "Your week on Cambrian", html);
                    await repo.MarkDigestSentAsync(candidate.UserId, now, ct);
                }

                recipients.Add(candidate.Email);
                result.Sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Failed++;
                _logger.LogError(ex, "EVENT: WeeklyDigestSendFailed userId:{UserId}", candidate.UserId);
            }
        }

        result.Recipients = recipients;
        _logger.LogInformation(
            "EVENT: WeeklyDigestRunCompleted dryRun:{DryRun} eligible:{Eligible} sent:{Sent} skippedUnverified:{U} skippedOptedOut:{O} skippedAlreadySent:{A} failed:{F}",
            result.DryRun, result.Eligible, result.Sent, result.SkippedUnverified, result.SkippedOptedOut, result.SkippedAlreadySent, result.Failed);
        return result;
    }

    /// <summary>
    /// One-click unsubscribe link: HMAC-SHA256 over "digest-unsubscribe:{userId}"
    /// keyed with Jwt:Key — verifiable without a token table, no login needed.
    /// </summary>
    public string BuildUnsubscribeUrl(string userId)
    {
        var apiBase = (_config["App:ApiPublicUrl"] ?? _config["App:FrontendUrl"] ?? string.Empty).TrimEnd('/');
        var token = ComputeUnsubscribeToken(userId, _config["Jwt:Key"] ?? string.Empty);
        return $"{apiBase}/email/unsubscribe?uid={Uri.EscapeDataString(userId)}&token={token}";
    }

    public static string ComputeUnsubscribeToken(string userId, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"digest-unsubscribe:{userId}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Renders the digest body. Public + static so tests can assert the zero-stats case.</summary>
    public static string BuildDigestHtml(CreatorDigestStats stats, string unsubscribeUrl)
    {
        var name = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(stats.DisplayName) ? "there" : stats.DisplayName);
        var topTrack = stats.TopTrackTitle is null
            ? "<p>No plays this week — a fresh upload is the best way to change that.</p>"
            : $"<p><strong>Top track:</strong> {System.Net.WebUtility.HtmlEncode(stats.TopTrackTitle)} ({stats.TopTrackPlays} plays)</p>";
        var creditsLine = stats.UnusedReleaseCredits > 0
            ? $"<p>You have <strong>{stats.UnusedReleaseCredits}</strong> unused Release Ready credit{(stats.UnusedReleaseCredits == 1 ? "" : "s")} — master a track before they reset.</p>"
            : string.Empty;

        return $@"<div style=""font-family:Arial,sans-serif;max-width:560px;margin:0 auto;color:#111"">
  <h2 style=""margin-bottom:4px"">Your week on Cambrian</h2>
  <p>Hi {name} — here's what happened with your music last week:</p>
  <ul>
    <li><strong>{stats.PlaysThisWeek}</strong> play{(stats.PlaysThisWeek == 1 ? "" : "s")}</li>
    <li><strong>{stats.NewFollowers}</strong> new follower{(stats.NewFollowers == 1 ? "" : "s")}</li>
  </ul>
  {topTrack}
  {creditsLine}
  <p style=""margin-top:24px"">
    <a href=""https://cambrianmusic.com/upload"" style=""background:#00c896;color:#000;padding:10px 18px;border-radius:8px;text-decoration:none;font-weight:bold"">Upload another track</a>
    &nbsp;&nbsp;
    <a href=""https://cambrianmusic.com/studio"" style=""color:#00c896"">Share your artist page</a>
  </p>
  <p style=""margin-top:32px;font-size:12px;color:#888"">
    You're getting this because you release music on Cambrian.
    <a href=""{unsubscribeUrl}"" style=""color:#888"">Unsubscribe from the weekly digest</a>.
  </p>
</div>";
    }

    private static DateTime StartOfIsoWeekUtc(DateTime utc)
    {
        var date = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        int diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff);
    }
}
