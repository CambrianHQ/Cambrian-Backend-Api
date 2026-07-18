using System.Diagnostics;
using System.Security.Cryptography;
using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.Validation;

public sealed class MediaValidationService : IMediaValidationService
{
    public const string HttpClientName = "MediaProductionProbe";
    private static readonly IReadOnlySet<string> SupportedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mpeg", "audio/mp3", "audio/wav", "audio/x-wav", "audio/flac",
        "audio/aac", "audio/ogg", "audio/mp4", "audio/x-m4a",
    };

    private readonly IObjectStorage _storage;
    private readonly IMediaProbeSignatureService _probeSignature;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PlaybackMediaOptions _options;
    private readonly MasteringOptions _mastering;

    public MediaValidationService(
        IObjectStorage storage,
        IMediaProbeSignatureService probeSignature,
        IHttpClientFactory httpClientFactory,
        IOptions<PlaybackMediaOptions> options,
        IOptions<MasteringOptions> mastering)
    {
        _storage = storage;
        _probeSignature = probeSignature;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _mastering = mastering.Value;
    }

    public async Task<MediaValidationResult> ValidateAsync(MediaValidationRequest request, CancellationToken ct = default)
    {
        StorageObjectMetadata? metadata;
        try
        {
            metadata = await _storage.GetMetadataAsync(request.ObjectKey, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return MediaValidationResult.Failure("storage_unavailable", "Storage metadata could not be read.", _options.ValidationVersion, true);
        }

        if (metadata is null)
            return MediaValidationResult.Failure("media_object_missing", "The configured media object does not exist.", _options.ValidationVersion);
        if (!string.Equals(metadata.Key, request.ObjectKey, StringComparison.Ordinal))
            return MediaValidationResult.Failure("object_key_mismatch", "The validated object key does not match the database key.", _options.ValidationVersion);
        if (metadata.SizeBytes <= 0)
            return MediaValidationResult.Failure("zero_byte_object", "The media object is empty.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);
        if (request.ExpectedSizeBytes.HasValue && request.ExpectedSizeBytes != metadata.SizeBytes)
            return MediaValidationResult.Failure("size_mismatch", "The media object size does not match stored metadata.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);
        if (string.IsNullOrWhiteSpace(metadata.ContentType) || !SupportedContentTypes.Contains(metadata.ContentType))
            return MediaValidationResult.Failure("unsupported_content_type", "The media content type is not supported.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);
        if (!string.IsNullOrWhiteSpace(request.ExpectedContentType)
            && !string.Equals(request.ExpectedContentType, metadata.ContentType, StringComparison.OrdinalIgnoreCase))
            return MediaValidationResult.Failure("content_type_mismatch", "The media content type does not match stored metadata.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);

        var extension = ExtensionFor(metadata.ContentType);
        var tempPath = Path.Combine(Path.GetTempPath(), $"cambrian-media-{Guid.NewGuid():N}{extension}");
        try
        {
            string checksum;
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous))
            using (var source = await _storage.OpenReadAsync(request.ObjectKey))
            {
                if (source is null)
                    return MediaValidationResult.Failure("media_object_missing", "The configured media object could not be opened.", _options.ValidationVersion);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81_920];
                int read;
                while ((read = await source.Stream.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                }
                checksum = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(request.ExpectedChecksumSha256)
                && !string.Equals(checksum, request.ExpectedChecksumSha256, StringComparison.OrdinalIgnoreCase))
                return MediaValidationResult.Failure("checksum_mismatch", "The media checksum does not match stored metadata.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);

            long durationMs;
            try
            {
                using var tagFile = TagLib.File.Create(tempPath);
                durationMs = (long)tagFile.Properties.Duration.TotalMilliseconds;
            }
            catch
            {
                return MediaValidationResult.Failure("media_parse_failed", "The audio container or stream could not be parsed.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);
            }

            if (durationMs < _options.MinimumDurationSeconds * 1_000L
                || durationMs > _options.MaximumDurationSeconds * 1_000L)
                return MediaValidationResult.Failure("duration_out_of_range", "The audio duration is outside configured limits.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);

            var decode = await DecodeProbeAsync(tempPath, ct);
            if (decode == DecodeProbeOutcome.ToolUnavailable)
                return MediaValidationResult.Failure("ffmpeg_unavailable", "The decode probe tool is unavailable on this host.", _options.ValidationVersion, true, metadata.SizeBytes, metadata.ContentType);
            if (decode == DecodeProbeOutcome.Timeout)
                return MediaValidationResult.Failure("decode_probe_timeout", "The decode probe timed out.", _options.ValidationVersion, true, metadata.SizeBytes, metadata.ContentType);
            if (decode == DecodeProbeOutcome.Failed)
                return MediaValidationResult.Failure("decode_probe_failed", "The audio stream failed the decode probe.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);

            using (var ranged = await _storage.OpenReadAsync(request.ObjectKey, "bytes=0-1023"))
            {
                if (ranged is null || ranged.IsRangeNotSatisfiable)
                    return MediaValidationResult.Failure("range_probe_failed", "The storage range probe failed.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);
                if (ranged.IsPartialContent)
                {
                    if (string.IsNullOrWhiteSpace(ranged.ContentRange)
                        || !ranged.Length.HasValue
                        || ranged.Length <= 0
                        || ranged.TotalLength != metadata.SizeBytes)
                        return MediaValidationResult.Failure("range_metadata_invalid", "The storage range metadata is inconsistent.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);
                }
            }

            var probe = await ProductionPathProbeAsync(request.TrackId, metadata.SizeBytes, ct);
            if (probe == ProductionProbeOutcome.Unavailable)
                return MediaValidationResult.Failure("production_probe_unavailable", "The production playback host was unavailable during the probe.", _options.ValidationVersion, true, metadata.SizeBytes, metadata.ContentType);
            if (probe == ProductionProbeOutcome.Failed)
                return MediaValidationResult.Failure("production_path_probe_failed", "The production playback path did not pass its range probe.", _options.ValidationVersion, sizeBytes: metadata.SizeBytes, contentType: metadata.ContentType);

            return new MediaValidationResult(
                true, null, null, false, metadata.SizeBytes, metadata.ContentType,
                checksum, durationMs, _options.ValidationVersion);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MediaValidationResult.Failure("validation_timeout", "Media validation timed out.", _options.ValidationVersion, true, metadata.SizeBytes, metadata.ContentType);
        }
        catch (HttpRequestException)
        {
            return MediaValidationResult.Failure("storage_unavailable", "Storage could not be reached during validation.", _options.ValidationVersion, true, metadata.SizeBytes, metadata.ContentType);
        }
        catch (IOException)
        {
            return MediaValidationResult.Failure("storage_unavailable", "The media stream was interrupted during validation.", _options.ValidationVersion, true, metadata.SizeBytes, metadata.ContentType);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private enum DecodeProbeOutcome { Decoded, Failed, ToolUnavailable, Timeout }

    private enum ProductionProbeOutcome { Passed, Failed, Unavailable }

    private async Task<DecodeProbeOutcome> DecodeProbeAsync(string path, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = _mastering.Ffmpeg.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-v", "error", "-t", "5", "-i", path, "-map", "0:a:0", "-f", "null", "-" })
            info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return DecodeProbeOutcome.ToolUnavailable;
        }
        catch (InvalidOperationException)
        {
            return DecodeProbeOutcome.ToolUnavailable;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.ValidationTimeoutSeconds));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? DecodeProbeOutcome.Decoded : DecodeProbeOutcome.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return DecodeProbeOutcome.Timeout;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return DecodeProbeOutcome.Failed;
        }
    }

    private async Task<ProductionProbeOutcome> ProductionPathProbeAsync(Guid trackId, long expectedTotal, CancellationToken ct)
    {
        // Production startup fails closed when the base URL is missing; in other
        // environments an unset URL means there is no production path to probe.
        if (string.IsNullOrWhiteSpace(_options.ProductionPlaybackBaseUrl))
            return ProductionProbeOutcome.Passed;
        if (!Uri.TryCreate(_options.ProductionPlaybackBaseUrl, UriKind.Absolute, out var baseUri))
            return ProductionProbeOutcome.Unavailable;
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"/stream/{trackId:D}/audio"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");
        request.Headers.TryAddWithoutValidation("X-Cambrian-Media-Probe", _probeSignature.Create(trackId));
        System.Net.Http.HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            return ProductionProbeOutcome.Unavailable;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProductionProbeOutcome.Unavailable;
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is 429 or >= 500 || (status is >= 300 and < 400))
                return ProductionProbeOutcome.Unavailable;
            if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                return response.Content.Headers.ContentRange?.Length == expectedTotal
                        && response.Content.Headers.ContentLength is > 0 and <= 1024
                    ? ProductionProbeOutcome.Passed
                    : ProductionProbeOutcome.Failed;
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
                // Browsers (Safari/AVPlayer) require true 206 semantics; a 200 for a
                // ranged request on a larger-than-window object means Range was ignored.
                return expectedTotal <= 1024 && response.Content.Headers.ContentLength == expectedTotal
                    ? ProductionProbeOutcome.Passed
                    : ProductionProbeOutcome.Failed;
            return ProductionProbeOutcome.Failed;
        }
    }

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/flac" => ".flac",
        "audio/aac" => ".aac",
        "audio/ogg" => ".ogg",
        "audio/mp4" or "audio/x-m4a" => ".m4a",
        _ => ".audio",
    };
}
