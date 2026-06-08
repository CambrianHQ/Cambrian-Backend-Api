using System.Net;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Observability;
using FluentAssertions;
using Xunit;

namespace Cambrian.Api.Tests.Observability;

/// <summary>
/// Verifies the Prometheus scraping endpoint is wired and that custom business
/// counters are exported under the exact names the Grafana dashboards query
/// (grafana/dashboards/). Uses the SQLite WebApplicationFactory fixture — NOT the
/// relational Testcontainers fixture — so it never requires Docker.
/// </summary>
public sealed class MetricsEndpointTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public MetricsEndpointTests(CambrianApiFixture factory) => _factory = factory;

    [Fact]
    public async Task Metrics_endpoint_is_anonymous_and_returns_ok()
    {
        // No Authorization header — the scrape target must be reachable internally
        // without a token (it sits outside the auth pipeline, like /sse and /mcp).
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/metrics");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Metrics_endpoint_exposes_aspnetcore_http_server_histogram()
    {
        var client = _factory.CreateClient();

        // Any request through the pipeline causes the ASP.NET Core instrumentation
        // to record the http.server.request.duration histogram (status code is
        // irrelevant to whether the measurement is taken).
        await client.GetAsync("/health");

        var body = await ScrapeUntilAsync(client, "http_server_request_duration_seconds");

        body.Should().Contain("http_server_request_duration_seconds");
    }

    [Fact]
    public async Task Custom_counter_is_exported_with_total_suffix()
    {
        var client = _factory.CreateClient();

        // The exporter renders the Counter "cambrian_checkout_started" as
        // "cambrian_checkout_started_total" — the exact series the checkout/revenue
        // dashboard queries. Increment directly to lock in that naming contract.
        CambrianMetrics.CheckoutStarted.Add(1);

        var body = await ScrapeUntilAsync(client, "cambrian_checkout_started_total");

        body.Should().Contain("cambrian_checkout_started_total");
    }

    /// <summary>
    /// Scrape /metrics, retrying briefly to tolerate the exporter's scrape-response
    /// cache and the async measurement flush. Returns as soon as <paramref name="expected"/>
    /// appears, otherwise the last body after the timeout (so the assertion shows a useful diff).
    /// </summary>
    private static async Task<string> ScrapeUntilAsync(HttpClient client, string expected)
    {
        var body = string.Empty;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var res = await client.GetAsync("/metrics");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            body = await res.Content.ReadAsStringAsync();
            if (body.Contains(expected))
                return body;
            await Task.Delay(100);
        }

        return body;
    }
}
