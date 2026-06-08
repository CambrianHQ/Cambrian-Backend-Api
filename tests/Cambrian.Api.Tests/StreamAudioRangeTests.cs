using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// End-to-end verification that the audio streaming proxy (GET /stream/{id}/audio) honors
/// HTTP Range against a NON-SEEKABLE origin stream — the exact branch used in production
/// when bytes are proxied from Supabase/S3. HttpClient response streams are not seekable,
/// so the controller must emit 206 / Content-Range / Content-Length itself (Safari & iOS
/// AVPlayer refuse to play without them). The default test storage returns a seekable
/// MemoryStream and exercises a different branch, so this uses a purpose-built S3-like fake.
/// </summary>
public sealed class StreamAudioRangeTests : IClassFixture<StreamAudioRangeTests.S3LikeStorageFixture>
{
    private readonly S3LikeStorageFixture _fixture;

    public StreamAudioRangeTests(S3LikeStorageFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RangeRequest_Returns206_WithCorrectSlice_AndHeaders()
    {
        var creatorId = await _fixture.SeedCreatorUserAsync();
        var trackId = await _fixture.SeedTrackAsync(creatorId);

        using var client = _fixture.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/stream/{trackId}/audio");
        req.Headers.Range = new RangeHeaderValue(2, 5); // inclusive: 4 bytes (indices 2..5)

        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode); // 206
        Assert.Contains("bytes", res.Headers.AcceptRanges);

        var contentRange = res.Content.Headers.ContentRange;
        Assert.NotNull(contentRange);
        Assert.Equal("bytes", contentRange!.Unit);
        Assert.Equal(2, contentRange.From);
        Assert.Equal(5, contentRange.To);
        Assert.Equal(S3LikeStorage.Payload.Length, contentRange.Length);

        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(S3LikeStorage.Payload[2..6], bytes);
        Assert.Equal(4, res.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task FullRequest_Returns200_WithWholePayload_AndAcceptRanges()
    {
        var creatorId = await _fixture.SeedCreatorUserAsync();
        var trackId = await _fixture.SeedTrackAsync(creatorId);

        using var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/stream/{trackId}/audio");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("bytes", res.Headers.AcceptRanges);

        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(S3LikeStorage.Payload, bytes);
    }

    // ── Fixture that swaps storage for a non-seekable, range-honoring S3-like fake ──
    public sealed class S3LikeStorageFixture : CambrianApiFixture
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage, S3LikeStorage>();
            });
        }

        public async Task<string> SeedCreatorUserAsync()
        {
            var email = $"range-creator-{Guid.NewGuid():N}@cambrian.com";
            await RegisterUserAsync(email);
            return await GetUserIdAsync(email);
        }
    }

    // Mimics S3ObjectStorage: returns a NON-seekable stream + partial-content metadata.
    private sealed class S3LikeStorage : IObjectStorage
    {
        public static readonly byte[] Payload = Encoding.ASCII.GetBytes("ABCDEFGHIJ"); // 10 bytes

        public Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
            => Task.FromResult(key);
        public string GenerateSignedUrl(string key) => $"https://fake/{key}";
        public string GetPublicUrl(string key) => $"https://fake/{key}";
        public Task DeleteAsync(string key) => Task.CompletedTask;

        public Task<StorageFile?> OpenReadAsync(string key) => OpenReadAsync(key, null);

        public Task<StorageFile?> OpenReadAsync(string key, string? rangeHeader)
        {
            if (string.IsNullOrWhiteSpace(rangeHeader))
            {
                return Task.FromResult<StorageFile?>(new StorageFile
                {
                    Stream = new NonSeekableStream(Payload),
                    ContentType = "audio/mpeg",
                    Length = Payload.Length,
                    TotalLength = Payload.Length,
                    IsPartialContent = false,
                });
            }

            // Parse "bytes=start-end" (end optional).
            var spec = rangeHeader.Replace("bytes=", "", StringComparison.OrdinalIgnoreCase).Trim();
            var parts = spec.Split('-', 2);
            var start = int.Parse(parts[0]);
            var end = parts.Length > 1 && !string.IsNullOrEmpty(parts[1])
                ? int.Parse(parts[1])
                : Payload.Length - 1;

            var slice = Payload[start..(end + 1)];
            return Task.FromResult<StorageFile?>(new StorageFile
            {
                Stream = new NonSeekableStream(slice),
                ContentType = "audio/mpeg",
                Length = slice.Length,
                TotalLength = Payload.Length,
                IsPartialContent = true,
                ContentRange = $"bytes {start}-{end}/{Payload.Length}",
            });
        }
    }

    // Read-only, forward-only stream so the controller takes the S3 (manual-206) branch
    // rather than the seekable ASP.NET File() branch.
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;
        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.ReadAsync(buffer, offset, count, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
