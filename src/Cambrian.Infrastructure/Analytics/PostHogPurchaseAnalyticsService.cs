using System.Net.Http.Json;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Infrastructure.Analytics;

public sealed class PostHogPurchaseAnalyticsService : IPurchaseAnalyticsService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<PostHogPurchaseAnalyticsService> _logger;

    public PostHogPurchaseAnalyticsService(
        HttpClient http,
        IConfiguration config,
        ILogger<PostHogPurchaseAnalyticsService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task CaptureAsync(PurchaseAnalyticsEvent purchaseEvent, CancellationToken ct = default)
    {
        var apiKey = _config["POSTHOG_API_KEY"]
            ?? _config["PostHog:ApiKey"]
            ?? Environment.GetEnvironmentVariable("POSTHOG_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "PostHog purchase analytics skipped for {EventName}: POSTHOG_API_KEY is not configured.",
                purchaseEvent.EventName);
            return;
        }

        var host = (_config["POSTHOG_HOST"]
            ?? _config["PostHog:Host"]
            ?? Environment.GetEnvironmentVariable("POSTHOG_HOST")
            ?? "https://app.posthog.com").TrimEnd('/');

        var properties = new Dictionary<string, object?>(purchaseEvent.Properties)
        {
            ["stripe_event_id"] = purchaseEvent.StripeEventId,
            ["$insert_id"] = purchaseEvent.StripeEventId
        };

        var payload = new
        {
            api_key = apiKey,
            @event = purchaseEvent.EventName,
            distinct_id = purchaseEvent.DistinctId,
            properties
        };

        try
        {
            using var response = await _http.PostAsJsonAsync($"{host}/capture/", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "PostHog purchase analytics failed for {EventName}: status {StatusCode}.",
                    purchaseEvent.EventName,
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "PostHog purchase analytics failed open for {EventName}.",
                purchaseEvent.EventName);
        }
    }
}
