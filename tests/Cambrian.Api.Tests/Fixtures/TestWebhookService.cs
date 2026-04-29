using System.Text.Json;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Tests.Fixtures;

/// <summary>
/// Test-only webhook service that bypasses Stripe signature verification
/// while preserving all business logic by delegating to ProcessEventAsync.
/// Used by <see cref="CambrianApiFixture"/> for E2E tests.
/// </summary>
internal sealed class TestWebhookService : IWebhookService
{
    private readonly StripeWebhookService _inner;

    public TestWebhookService(
        CambrianDbContext db,
        ILicenseService licenseService,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<StripeWebhookService> logger,
        IHostEnvironment env)
    {
        _inner = new StripeWebhookService(db, licenseService, emailService, configuration, logger, env);
    }

    public async Task HandleStripeAsync(string payload, string signature)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var eventType = root.GetProperty("type").GetString()!;
        string? eventId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        string? clientReferenceId = null;
        long? amountTotal = null;
        string? stripeCustomerId = null;
        string? stripeSessionId = null;
        string? stripePaymentIntentId = null;

        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("object", out var obj))
        {
            if (eventType == "checkout.session.completed")
            {
                clientReferenceId = obj.TryGetProperty("client_reference_id", out var r) ? r.GetString() : null;
                amountTotal = obj.TryGetProperty("amount_total", out var a) ? a.GetInt64() : null;
                stripeSessionId = obj.TryGetProperty("id", out var s) ? s.GetString() : null;
                stripeCustomerId = obj.TryGetProperty("customer", out var c) ? c.GetString() : null;
            }
            else if (eventType is "customer.subscription.deleted" or "invoice.paid" or "invoice.payment_failed")
            {
                stripeCustomerId = obj.TryGetProperty("customer", out var c) ? c.GetString() : null;
            }
            else if (eventType is "payment_intent.succeeded" or "charge.refunded" or "charge.dispute.created")
            {
                stripePaymentIntentId = obj.TryGetProperty("payment_intent", out var pi) ? pi.GetString() : null;
                stripePaymentIntentId ??= obj.TryGetProperty("id", out var id) ? id.GetString() : null;
            }
        }

        await _inner.ProcessEventAsync(
            eventId, eventType, clientReferenceId, amountTotal,
            stripeCustomerId, stripeSessionId, stripePaymentIntentId, payload);
    }
}
