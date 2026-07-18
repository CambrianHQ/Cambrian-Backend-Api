using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Infrastructure.Validation;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

public sealed class MediaValidationServiceTests : IDisposable
{
    private static readonly Guid TrackId = Guid.Parse("7f1e0a54-3c65-4e11-9a3b-2f8d1c6b4a90");
    private const string ObjectKey = "tracks/validation-target.mp3";
    private const string MissingFfmpegBinary = "definitely-not-a-real-binary-xyz";
    private const string ProductionBaseUrl = "https://production-playback.test";

    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"cambrian-media-validation-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_stubDir)) Directory.Delete(_stubDir, recursive: true); } catch { }
    }

    // -- Storage metadata gates ------------------------------------------------

    [Fact]
    public async Task MissingObjectMetadata_FailsAsMediaObjectMissing_WithoutDependencyFlag()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StorageObjectMetadata?>(null));
        var service = CreateService(storage, BuildOptions());

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("media_object_missing", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
    }

    [Fact]
    public async Task StorageMetadataOutage_FailsAsStorageUnavailable_WithDependencyFlag()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("storage endpoint unreachable"));
        var service = CreateService(storage, BuildOptions());

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("storage_unavailable", result.FailureCode);
        Assert.True(result.DependencyUnavailable);
    }

    [Fact]
    public async Task MetadataKeyDifferentFromRequestedKey_FailsAsObjectKeyMismatch()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(Metadata(2_048, key: "tracks/some-other-object.mp3"));
        var service = CreateService(storage, BuildOptions());

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("object_key_mismatch", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
    }

    [Fact]
    public async Task ZeroByteObject_FailsAsZeroByteObject()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns(Metadata(0));
        var service = CreateService(storage, BuildOptions());

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("zero_byte_object", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
        Assert.Equal(0, result.SizeBytes);
    }

    [Fact]
    public async Task ExpectedSizeDifferentFromActualSize_FailsAsSizeMismatch()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns(Metadata(2_048));
        var service = CreateService(storage, BuildOptions());

        var result = await service.ValidateAsync(Request(expectedSizeBytes: 4_096));

        Assert.False(result.IsValid);
        Assert.Equal("size_mismatch", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
    }

    [Fact]
    public async Task UnsupportedContentType_FailsAsUnsupportedContentType()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(Metadata(2_048, contentType: "application/octet-stream"));
        var service = CreateService(storage, BuildOptions());

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("unsupported_content_type", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
        Assert.Equal("application/octet-stream", result.ContentType);
    }

    [Fact]
    public async Task ExpectedContentTypeDifferentFromActual_FailsAsContentTypeMismatch()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns(Metadata(2_048));
        var service = CreateService(storage, BuildOptions());

        var result = await service.ValidateAsync(Request(expectedContentType: "audio/wav"));

        Assert.False(result.IsValid);
        Assert.Equal("content_type_mismatch", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
    }

    // -- Object content gates --------------------------------------------------

    [Fact]
    public async Task ObjectThatCannotBeOpened_FailsAsMediaObjectMissing()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns(Metadata(2_048));
        storage.OpenReadAsync(ObjectKey).Returns(Task.FromResult<StorageFile?>(null));
        var service = CreateService(storage, BuildOptions());

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("media_object_missing", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
    }

    [Fact]
    public async Task ExpectedChecksumDifferentFromStreamedContent_FailsAsChecksumMismatch()
    {
        // The checksum gate runs before the TagLib parse, so non-audio bytes still
        // classify as checksum_mismatch when an expectation is supplied.
        var payload = Encoding.ASCII.GetBytes("not-audio-but-the-checksum-gate-runs-first");
        var storage = HealthyStorage(payload);
        var service = CreateService(storage, BuildOptions());

        var result = await service.ValidateAsync(Request(expectedChecksumSha256: new string('0', 64)));

        Assert.False(result.IsValid);
        Assert.Equal("checksum_mismatch", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
    }

    [Fact]
    public async Task UnparseableAudioBytes_FailAsMediaParseFailed()
    {
        var payload = Encoding.ASCII.GetBytes("this is definitely not an audio file payload at all");
        var storage = HealthyStorage(payload);
        var service = CreateService(storage, BuildOptions());

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("media_parse_failed", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
    }

    // -- Decode probe gate -----------------------------------------------------

    [Fact]
    public async Task MissingFfmpegBinaryWithOtherwiseValidAudio_FailsAsFfmpegUnavailable_WithDependencyFlag()
    {
        var payload = BuildValidMp3();
        var storage = HealthyStorage(payload);
        var service = CreateService(storage, BuildOptions(), ffmpegPath: MissingFfmpegBinary);

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("ffmpeg_unavailable", result.FailureCode);
        Assert.True(result.DependencyUnavailable);
    }

    // -- Storage range probe gate ----------------------------------------------

    [Fact]
    public async Task StorageRangeProbeReturningRangeNotSatisfiable_FailsAsRangeProbeFailed()
    {
        var payload = BuildValidMp3();
        var storage = HealthyStorage(payload);
        storage.OpenReadAsync(ObjectKey, Arg.Any<string?>()).Returns(_ => new StorageFile
        {
            Stream = new MemoryStream(),
            IsRangeNotSatisfiable = true,
            StatusCode = 416,
        });
        var service = CreateService(storage, BuildOptions(), ffmpegPath: CreateExitZeroFfmpegStub());

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("range_probe_failed", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
    }

    // -- Production path probe gate --------------------------------------------

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(302)]
    public async Task ProductionProbeOutageStatuses_FailAsProductionProbeUnavailable_WithDependencyFlag(int statusCode)
    {
        var payload = BuildValidMp3();
        var storage = HealthyStorage(payload);
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)statusCode));
        var service = CreateService(storage, BuildOptions(ProductionBaseUrl), CreateExitZeroFfmpegStub(), handler);

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("production_probe_unavailable", result.FailureCode);
        Assert.True(result.DependencyUnavailable);
    }

    [Fact]
    public async Task ProductionProbeNotFound_FailsAsProductionPathProbeFailed_WithoutDependencyFlag()
    {
        var payload = BuildValidMp3();
        var storage = HealthyStorage(payload);
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = CreateService(storage, BuildOptions(ProductionBaseUrl), CreateExitZeroFfmpegStub(), handler);

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("production_path_probe_failed", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
    }

    [Fact]
    public async Task ProductionProbeIgnoringRangeOnLargeObject_FailsAsProductionPathProbeFailed()
    {
        var payload = BuildValidMp3();
        var storage = HealthyStorage(payload);
        var handler = new TestHttpMessageHandler(_ =>
        {
            // 200 with the full object length for a ranged request on a >1 KiB
            // object means Range was ignored — Safari/AVPlayer require true 206.
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            response.Content.Headers.ContentLength = payload.Length;
            return response;
        });
        var service = CreateService(storage, BuildOptions(ProductionBaseUrl), CreateExitZeroFfmpegStub(), handler);

        var result = await service.ValidateAsync(Request());

        Assert.False(result.IsValid);
        Assert.Equal("production_path_probe_failed", result.FailureCode);
        Assert.False(result.DependencyUnavailable);
    }

    [Fact]
    public async Task ProductionProbeWithCorrectPartialContent_ValidatesEndToEnd()
    {
        var payload = BuildValidMp3();
        var storage = HealthyStorage(payload);
        var handler = new TestHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[1024]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 1023, payload.Length);
            return response;
        });
        var options = BuildOptions(ProductionBaseUrl);
        var service = CreateService(storage, options, CreateExitZeroFfmpegStub(), handler);

        var result = await service.ValidateAsync(Request());

        Assert.True(result.IsValid);
        Assert.Null(result.FailureCode);
        Assert.False(result.DependencyUnavailable);
        Assert.Equal(payload.Length, result.SizeBytes);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), result.ChecksumSha256);
        Assert.NotNull(result.DurationMilliseconds);
        Assert.InRange(result.DurationMilliseconds!.Value, 7_000, 9_000);
        Assert.Equal("media-test-v1", result.ValidationVersion);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal($"/stream/{TrackId:D}/audio", handler.LastRequestUri!.AbsolutePath);
        Assert.Equal("bytes=0-1023", handler.LastRangeHeader);
        var probeSignature = new MediaProbeSignatureService(Options.Create(options), TimeProvider.System);
        Assert.True(probeSignature.Validate(handler.LastProbeSignature, TrackId));
    }

    [Fact]
    public async Task UnsetProductionBaseUrl_SkipsTheProbeAndValidationSucceeds()
    {
        var payload = BuildValidMp3();
        var storage = HealthyStorage(payload);
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = CreateService(storage, BuildOptions(productionBaseUrl: null), CreateExitZeroFfmpegStub(), handler);

        var result = await service.ValidateAsync(Request());

        Assert.True(result.IsValid);
        Assert.Null(result.FailureCode);
        Assert.False(result.DependencyUnavailable);
        Assert.Equal(0, handler.CallCount);
    }

    // -- Helpers ---------------------------------------------------------------

    private static PlaybackMediaOptions BuildOptions(string? productionBaseUrl = null) => new()
    {
        MinimumDurationSeconds = 1,
        MaximumDurationSeconds = 900,
        ValidationTimeoutSeconds = 20,
        ValidationVersion = "media-test-v1",
        ProductionPlaybackBaseUrl = productionBaseUrl,
        ProductionProbeSigningKey = "media-probe-signing-key-32-bytes-minimum",
    };

    private static MediaValidationService CreateService(
        IObjectStorage storage,
        PlaybackMediaOptions options,
        string ffmpegPath = MissingFfmpegBinary,
        TestHttpMessageHandler? handler = null)
    {
        handler ??= new TestHttpMessageHandler(_ =>
            throw new InvalidOperationException("The production probe should not be reached by this test."));
        return new MediaValidationService(
            storage,
            new MediaProbeSignatureService(Options.Create(options), TimeProvider.System),
            new FakeHttpClientFactory(handler),
            Options.Create(options),
            Options.Create(new MasteringOptions { Ffmpeg = new FfmpegOptions { FfmpegPath = ffmpegPath } }));
    }

    private static MediaValidationRequest Request(
        long? expectedSizeBytes = null,
        string? expectedContentType = null,
        string? expectedChecksumSha256 = null) =>
        new(TrackId, ObjectKey, expectedSizeBytes, expectedContentType, expectedChecksumSha256);

    private static StorageObjectMetadata Metadata(long sizeBytes, string? contentType = "audio/mpeg", string key = ObjectKey) =>
        new(key, sizeBytes, contentType, null, DateTime.UtcNow);

    /// <summary>
    /// Roughly eight seconds of valid CBR MPEG-1 Layer III silence. TagLib's
    /// size/bitrate duration estimate for this payload is ~7.9 s, comfortably
    /// inside the 1–900 s window configured by <see cref="BuildOptions"/>.
    /// </summary>
    private static byte[] BuildValidMp3()
    {
        using var second = Cambrian.Infrastructure.Storage.SilentMp3Generator.Generate();
        var frames = second.ToArray();
        using var ms = new MemoryStream(frames.Length * 8);
        for (var i = 0; i < 8; i++)
            ms.Write(frames, 0, frames.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// Storage substitute whose metadata, full read, and ranged read all agree
    /// with the supplied payload. Individual tests re-configure members to break
    /// one specific gate.
    /// </summary>
    private static IObjectStorage HealthyStorage(byte[] payload)
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns(Metadata(payload.Length));
        storage.OpenReadAsync(ObjectKey).Returns(_ => new StorageFile
        {
            Stream = new MemoryStream(payload),
            ContentType = "audio/mpeg",
            Length = payload.Length,
            TotalLength = payload.Length,
        });
        var rangeLength = Math.Min(1024, payload.Length);
        storage.OpenReadAsync(ObjectKey, Arg.Any<string?>()).Returns(_ => new StorageFile
        {
            Stream = new MemoryStream(payload, 0, rangeLength),
            ContentType = "audio/mpeg",
            IsPartialContent = true,
            ContentRange = $"bytes 0-{rangeLength - 1}/{payload.Length}",
            Length = rangeLength,
            TotalLength = payload.Length,
            StatusCode = 206,
        });
        return storage;
    }

    /// <summary>
    /// The decode probe launches the configured ffmpeg binary with fixed
    /// arguments, so tests that must get past it use a stub that exits 0 for
    /// any input: a .cmd batch on Windows (CreateProcess runs .cmd/.bat files
    /// via cmd.exe even with UseShellExecute=false) or an executable /bin/sh
    /// script elsewhere.
    /// </summary>
    private string CreateExitZeroFfmpegStub()
    {
        Directory.CreateDirectory(_stubDir);
        if (OperatingSystem.IsWindows())
        {
            var cmdPath = Path.Combine(_stubDir, "ffmpeg-stub.cmd");
            File.WriteAllText(cmdPath, "@echo off\r\nexit /b 0\r\n");
            return cmdPath;
        }

        var shPath = Path.Combine(_stubDir, "ffmpeg-stub.sh");
        File.WriteAllText(shPath, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(shPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return shPath;
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastRangeHeader { get; private set; }
        public string? LastProbeSignature { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Capture at send time — the service disposes the request after use.
            CallCount++;
            LastRequestUri = request.RequestUri;
            LastRangeHeader = request.Headers.TryGetValues("Range", out var range) ? string.Join(",", range) : null;
            LastProbeSignature = request.Headers.TryGetValues("X-Cambrian-Media-Probe", out var sig) ? string.Join(",", sig) : null;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
