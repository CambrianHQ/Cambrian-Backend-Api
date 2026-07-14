using System.Net;
using Cambrian.Infrastructure.Options;
using Cambrian.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.Tests;

/// <summary>
/// Track/cover-art uploads are proxied through this API to the storage origin, so a
/// transient blip on the far end (dropped connection, 5xx, 429) shouldn't sacrifice an
/// upload the caller already paid to transfer here — this is part of the fix for the
/// "We couldn't store your file" publish failure, alongside the 30s→100s HttpClient
/// timeout bump for large audio files.
/// </summary>
public sealed class S3ObjectStorageUploadRetryTests
{
    private static S3ObjectStorage BuildStorage(SequencedHandler handler)
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
        var factory = new StubHttpClientFactory(handler);
        return new S3ObjectStorage(options, factory, NullLogger<S3ObjectStorage>.Instance);
    }

    [Fact]
    public async Task UploadAsync_RetriesOnce_ThenSucceeds_AfterTransient503()
    {
        var handler = new SequencedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var storage = BuildStorage(handler);

        var key = await storage.UploadAsync(
            new MemoryStream(new byte[] { 1, 2, 3 }), "tracks/creator-1/song.mp3", "audio/mpeg");

        Assert.Equal("tracks/creator-1/song.mp3", key);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task UploadAsync_GivesUpAfterMaxAttempts_OnPersistentTransientFailure()
    {
        var handler = new SequencedHandler(
            HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        var storage = BuildStorage(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.UploadAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "tracks/creator-1/song.mp3", "audio/mpeg"));

        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task UploadAsync_DoesNotRetry_OnNonTransientStatus()
    {
        // 403 (bad signature/credentials) fails identically every time — retrying just
        // wastes time, so it should fail on the first attempt.
        var handler = new SequencedHandler(HttpStatusCode.Forbidden, HttpStatusCode.OK);
        var storage = BuildStorage(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.UploadAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "tracks/creator-1/song.mp3", "audio/mpeg"));

        Assert.Equal(1, handler.CallCount);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler);
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _responses;
        public int CallCount { get; private set; }

        public SequencedHandler(params HttpStatusCode[] responses) => _responses = new Queue<HttpStatusCode>(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException(
                    $"Unexpected extra HTTP call #{CallCount} — test only stubbed {CallCount - 1} responses.");

            var status = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(string.Empty),
                RequestMessage = request,
            });
        }
    }
}
