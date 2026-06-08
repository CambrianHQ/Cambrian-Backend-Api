using System.Net;
using Cambrian.Infrastructure.Options;
using Cambrian.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.Tests;

/// <summary>
/// The storage health probe backs both the boot-time [STORAGE-DIAG] check and the
/// /qa-preflight storage gate. It MUST exercise the real network read path: a presigned
/// URL that only *generates* locally is pure client-side crypto and succeeds even when
/// every real read 403s — which would report a misconfigured Supabase store as healthy
/// and mask the true cause of dead audio playback (bad credentials/region →
/// SignatureDoesNotMatch on every GET).
/// </summary>
public sealed class S3ObjectStorageProbeTests
{
    private static S3ObjectStorage BuildStorage(HttpStatusCode status, string body = "")
    {
        var options = Options.Create(new StorageOptions
        {
            Provider = "s3",
            Endpoint = "https://project.supabase.co/storage/v1/s3",
            Bucket = "cambrian-audio",
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key",
            Region = "us-east-1",
            UsePathStyle = true,
        });
        var factory = new StubHttpClientFactory(new StubHandler(status, body));
        return new S3ObjectStorage(options, factory, NullLogger<S3ObjectStorage>.Instance);
    }

    [Fact]
    public async Task Probe_Reports_Healthy_When_Origin_Returns_404()
    {
        // 404 on the sentinel key = credentials/region/endpoint valid, object simply absent.
        var storage = BuildStorage(HttpStatusCode.NotFound);

        var result = await storage.ProbeAsync();

        Assert.True(result.HeadBucketOk);
        Assert.Null(result.HeadBucketError);
    }

    [Fact]
    public async Task Probe_Reports_Healthy_When_Sentinel_Object_Exists()
    {
        var storage = BuildStorage(HttpStatusCode.OK);

        var result = await storage.ProbeAsync();

        Assert.True(result.HeadBucketOk);
    }

    [Fact]
    public async Task Probe_Reports_Unhealthy_When_Credentials_Are_Rejected()
    {
        // The real-world failure mode: wrong region/keys → SignatureDoesNotMatch on every
        // read. The probe must surface this so /qa-preflight returns 503 (not a false green).
        var storage = BuildStorage(
            HttpStatusCode.Forbidden,
            "<Error><Code>SignatureDoesNotMatch</Code><Message>...</Message></Error>");

        var result = await storage.ProbeAsync();

        Assert.False(result.HeadBucketOk);
        Assert.NotNull(result.HeadBucketError);
        Assert.Contains("SignatureDoesNotMatch", result.HeadBucketError!);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
                RequestMessage = request,
            });
    }
}
