using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// /qa-preflight is the hard deployment gate — unlike /health which always
/// returns 200 so Render keeps routing traffic, /qa-preflight must return 503
/// whenever any critical dependency is unhealthy. CI post-deploy scripts block
/// the release on this status code, so these tests lock the contract in place.
/// </summary>
[Trait("Category", "Critical")]
public sealed class QaPreflightTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public QaPreflightTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Returns_200_When_All_Dependencies_Healthy()
    {
        using var client = _fixture.CreateClient();
        var res = await client.GetAsync("/qa-preflight");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("ok", body.GetProperty("db").GetProperty("status").GetString());
        Assert.Equal("ok", body.GetProperty("storage").GetProperty("status").GetString());
        // Stripe is unconfigured in Testing env → reported as "skip" (still Ok).
        Assert.Equal("skip", body.GetProperty("stripe").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Returns_503_When_Storage_Probe_Fails()
    {
        await using var failingFixture = new FailingStorageFixture();
        await ((IAsyncLifetime)failingFixture).InitializeAsync();

        try
        {
            using var client = failingFixture.CreateClient();
            var res = await client.GetAsync("/qa-preflight");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal("degraded", body.GetProperty("status").GetString());
            Assert.Equal("fail", body.GetProperty("storage").GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(
                body.GetProperty("storage").GetProperty("error").GetString()));
        }
        finally
        {
            await ((IAsyncLifetime)failingFixture).DisposeAsync();
        }
    }

    [Fact]
    public async Task Response_Shape_Has_Required_Fields()
    {
        using var client = _fixture.CreateClient();
        var res = await client.GetAsync("/qa-preflight");

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("status", out _));
        Assert.True(body.TryGetProperty("environment", out _));
        Assert.True(body.TryGetProperty("timestamp", out _));

        foreach (var dep in new[] { "db", "storage", "stripe" })
        {
            var obj = body.GetProperty(dep);
            Assert.True(obj.TryGetProperty("status", out _), $"{dep}.status missing");
            Assert.True(obj.TryGetProperty("latencyMs", out _), $"{dep}.latencyMs missing");
        }
    }

    // A fixture variant that swaps in a storage implementation whose probe
    // always fails, so the controller's error path can be exercised end-to-end.
    private sealed class FailingStorageFixture : CambrianApiFixture
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage, FailingProbeStorage>();
            });
        }
    }

    private sealed class FailingProbeStorage : IObjectStorage
    {
        public Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
            => Task.FromResult($"fail://{key}");
        public string GenerateSignedUrl(string key) => $"https://fail/{key}";
        public string GetPublicUrl(string key) => $"https://fail/{key}";
        public Task<StorageFile?> OpenReadAsync(string key) => Task.FromResult<StorageFile?>(null);
        public Task DeleteAsync(string key) => Task.CompletedTask;

        public Task<StorageProbeResult> ProbeAsync(string? sampleKey = null)
            => Task.FromResult(new StorageProbeResult
            {
                HeadBucketOk = false,
                HeadBucketError = "simulated bucket failure for test",
                Bucket = "test",
                Endpoint = "test",
            });
    }
}
