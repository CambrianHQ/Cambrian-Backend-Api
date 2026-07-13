using System.Diagnostics.Metrics;

namespace Cambrian.Application.Observability;

/// <summary>
/// Central definition of Cambrian's custom business metrics.
///
/// A single static <see cref="Meter"/> ("Cambrian") lets emission sites across the
/// Application, Infrastructure and Api layers increment a counter with one line and zero
/// constructor churn — <see cref="Meter"/> and <see cref="Counter{T}"/> are thread-safe.
/// The OpenTelemetry SDK in the API project subscribes by name (<c>.AddMeter("Cambrian")</c>);
/// the Prometheus exporter renders each counter with a <c>_total</c> suffix — e.g. the
/// instrument <c>cambrian_checkout_started</c> is scraped as <c>cambrian_checkout_started_total</c>,
/// matching the queries in <c>grafana/dashboards/checkout-webhook-revenue.json</c>.
/// </summary>
public static class CambrianMetrics
{
    /// <summary>Meter name the OpenTelemetry SDK subscribes to (see Program.cs <c>.AddMeter</c>).</summary>
    public const string MeterName = "Cambrian";

    private static readonly Meter Meter = new(MeterName);

    // ── Subscription checkout funnel ──
    public static readonly Counter<long> CheckoutStarted =
        Meter.CreateCounter<long>("cambrian_checkout_started", description: "Subscription checkout sessions initiated.");
    public static readonly Counter<long> CheckoutCompleted =
        Meter.CreateCounter<long>("cambrian_checkout_completed", description: "Subscription checkouts fulfilled via webhook.");
    public static readonly Counter<long> CheckoutFailed =
        Meter.CreateCounter<long>("cambrian_checkout_failed", description: "Subscription checkout initiations that failed.");

    // ── Stripe webhook processing ──
    public static readonly Counter<long> WebhookProcessed =
        Meter.CreateCounter<long>("cambrian_webhook_processed", description: "Stripe webhook events processed successfully.");
    public static readonly Counter<long> WebhookDuplicate =
        Meter.CreateCounter<long>("cambrian_webhook_duplicate", description: "Stripe webhook events skipped as duplicates.");
    public static readonly Counter<long> WebhookFailed =
        Meter.CreateCounter<long>("cambrian_webhook_failed", description: "Stripe webhook events that failed processing.");

    // ── Library / uploads / streaming ──
    public static readonly Counter<long> LibraryGrant =
        Meter.CreateCounter<long>("cambrian_library_grant", description: "Tracks saved/granted to a user's library.");
    public static readonly Counter<long> UploadCompleted =
        Meter.CreateCounter<long>("cambrian_upload_completed", description: "Track uploads completed.");
    public static readonly Counter<long> UploadStarted =
        Meter.CreateCounter<long>("cambrian_upload_started", description: "Track upload attempts started.");
    public static readonly Counter<long> UploadFailed =
        Meter.CreateCounter<long>("cambrian_upload_failed", description: "Track uploads rejected or blocked.");
    public static readonly Counter<long> BatchUploadCompleted =
        Meter.CreateCounter<long>("cambrian_batch_upload_completed", description: "Batch upload requests completed with per-track results.");
    public static readonly Counter<long> StreamSignedUrlIssued =
        Meter.CreateCounter<long>("cambrian_stream_signed_url_issued", description: "Signed audio stream URLs issued.");
    public static readonly Counter<long> PlaybackUrlFailed =
        Meter.CreateCounter<long>("cambrian_playback_url_failed", description: "Playback URL or object resolution failures.");
    public static readonly Counter<long> VerificationEmailFailed =
        Meter.CreateCounter<long>("cambrian_verification_email_failed", description: "Verification emails rejected by their provider.");
    public static readonly Counter<long> ProfileSaveFailed =
        Meter.CreateCounter<long>("cambrian_profile_save_failed", description: "Creator profile save operations that failed.");
    public static readonly Counter<long> EntitlementChanged =
        Meter.CreateCounter<long>("cambrian_entitlement_changed", description: "Authoritative backend entitlement changes.");
    public static readonly Counter<long> ReleaseReadyJobFailed =
        Meter.CreateCounter<long>("cambrian_release_ready_job_failed", description: "Release Ready background jobs that failed.");

    // ── Payouts ──
    public static readonly Counter<long> PayoutCreated =
        Meter.CreateCounter<long>("cambrian_payout_created", description: "Payouts requested and committed.");
    public static readonly Counter<long> PayoutApproved =
        Meter.CreateCounter<long>("cambrian_payout_approved", description: "Payouts transferred and approved.");
}
