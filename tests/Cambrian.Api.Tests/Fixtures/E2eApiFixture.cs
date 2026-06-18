using System.Collections.Concurrent;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cambrian.Api.Tests.Fixtures;

/// <summary>
/// Fixture for the test-only <c>/__e2e/*</c> support surface. Extends the SQLite fixture to:
/// <list type="bullet">
///   <item>configure the E2E shared secret and a Stripe webhook signing secret;</item>
///   <item>use the REAL <c>StripeWebhookService</c> (<see cref="UseTestWebhookService"/> = false)
///   so synthetic signed events exercise real signature verification and event-id dedup;</item>
///   <item>replace object storage with a faithful in-memory store so a seeded track whose
///   object was never uploaded is genuinely "missing audio" (OpenRead → null → 404). The
///   shared <c>FakeObjectStorage</c> (which always returns bytes) is left untouched for the
///   rest of the suite.</item>
/// </list>
/// </summary>
public sealed class E2eApiFixture : CambrianApiFixture
{
    public const string E2eSecret = "e2e-test-secret-0123456789-abcdef";
    public const string WebhookSecret = "whsec_e2e_test_secret";

    // Exercise the production webhook path (signature + dedup), not the bypass stub.
    protected override bool UseTestWebhookService => false;

    protected override IReadOnlyDictionary<string, string?> CreateTestConfigurationOverrides() =>
        new Dictionary<string, string?>
        {
            ["Cambrian:E2E:Secret"] = E2eSecret,
            ["Stripe:WebhookSecret"] = WebhookSecret,
        };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Runs after the base registration, so this RemoveAll wins.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage, InMemoryObjectStorage>();
        });
    }
}

/// <summary>
/// Faithful in-memory object storage for E2E tests: an upload records the bytes under its key
/// and a read returns them; a read for a key that was never uploaded returns <c>null</c> (the
/// real "object missing" signal the stream endpoint turns into a 404). Unlike the suite-wide
/// <c>FakeObjectStorage</c> stub, this distinguishes present from absent objects, which is what
/// makes the seeded "missing-audio" track verifiable.
/// </summary>
internal sealed class InMemoryObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _objects = new();

    public async Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        _objects[key] = (ms.ToArray(), contentType);
        return $"mem://{key}";
    }

    public string GenerateSignedUrl(string key) => $"https://mem-cdn.cambrian.test/{key}?signed=true";

    public string GetPublicUrl(string key) => $"https://mem-cdn.cambrian.test/{key}";

    public Task<StorageFile?> OpenReadAsync(string key)
    {
        if (!_objects.TryGetValue(key, out var obj))
            return Task.FromResult<StorageFile?>(null);

        return Task.FromResult<StorageFile?>(new StorageFile
        {
            Stream = new MemoryStream(obj.Bytes, writable: false),
            ContentType = obj.ContentType,
            Length = obj.Bytes.Length,
            TotalLength = obj.Bytes.Length,
        });
    }

    public Task DeleteAsync(string key)
    {
        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
