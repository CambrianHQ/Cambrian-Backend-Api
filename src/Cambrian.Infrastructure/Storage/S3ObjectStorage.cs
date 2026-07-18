using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.Storage;

/// <summary>
/// S3-compatible object storage (works with AWS S3, Cloudflare R2, and
/// Supabase Storage's S3 gateway).
///
/// <para>
/// <b>Why every verb goes through HttpClient instead of the SDK directly:</b>
/// AWSSDK.S3 3.7.305 mis-signs direct HTTP requests against path-prefixed S3
/// endpoints such as Supabase's <c>/storage/v1/s3</c> gateway. Every
/// GET/HEAD/DELETE/PUT issued by <c>PutObjectAsync</c>, <c>GetObjectAsync</c>,
/// etc. comes back with <c>SignatureDoesNotMatch</c> regardless of credentials,
/// <c>AuthenticationRegion</c>, or <c>DisablePayloadSigning</c>. The SDK's
/// presigned-URL generator, however, produces correct SigV4 query signatures
/// for the exact same endpoint — this was verified end-to-end by fetching a
/// presigned URL with <c>HttpClient</c> and getting 200 OK with the expected
/// content length, and again by PUTting a file via presigned URL.
/// </para>
/// <para>
/// The fix: keep the SDK for signing only. Generate a short-lived presigned URL
/// for every read/write/delete and run the actual HTTP request through an
/// injected <see cref="IHttpClientFactory"/>.
/// </para>
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage
{
    public TimeSpan? SignedUrlLifetime => TimeSpan.FromMinutes(15);
    private const string HttpClientName = "SupabaseStorage";

    /// <summary>Total attempts (including the first) for a single UploadAsync call.</summary>
    private const int UploadMaxAttempts = 3;

    private readonly StorageOptions _options;
    private readonly AmazonS3Client _client;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<S3ObjectStorage> _logger;

    public S3ObjectStorage(
        IOptions<StorageOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<S3ObjectStorage> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // R2 and modern S3 require Signature Version 4
        AWSConfigsS3.UseSignatureVersion4 = true;

        var config = new AmazonS3Config
        {
            ServiceURL = _options.Endpoint,
            ForcePathStyle = _options.UsePathStyle,
        };
        if (!string.IsNullOrWhiteSpace(_options.Region))
            config.AuthenticationRegion = _options.Region;

        _client = new AmazonS3Client(
            _options.AccessKey,
            _options.SecretKey,
            config);
    }

    public async Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
    {
        var normalised = NormaliseKey(key);
        _logger.LogDebug("S3 upload: key={Key}, contentType={ContentType}", normalised, contentType);

        // See class-level remarks: the SDK's PutObjectAsync mis-signs requests
        // against Supabase's path-prefixed S3 gateway (SignatureDoesNotMatch).
        // Generate a presigned PUT URL and upload via HttpClient instead —
        // the presigned-URL generator produces correct SigV4 query signatures.
        string presignedUrl;
        try
        {
            presignedUrl = _client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = _options.Bucket,
                Key = normalised,
                Expires = DateTime.UtcNow.AddMinutes(15),
                Verb = HttpVerb.PUT,
                ContentType = contentType,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[STORAGE-DIAG] S3 Upload presign failed: bucket={Bucket} key={Key} message={Message}",
                _options.Bucket, normalised, ex.Message);
            throw;
        }

        // A blip on the storage origin (dropped connection, transient 5xx/429) shouldn't
        // sacrifice an entire upload — the caller has already paid the cost of getting the
        // file to this API. Retry those quick-failing cases; a genuine timeout is not
        // retried since the larger HttpClient timeout above is the fix for that case and
        // stacking more multi-minute attempts on top of it risks tripping an upstream
        // gateway timeout instead of helping.
        for (var attempt = 1; attempt <= UploadMaxAttempts; attempt++)
        {
            var isLastAttempt = attempt == UploadMaxAttempts;
            if (file.CanSeek)
                file.Seek(0, SeekOrigin.Begin);

            try
            {
                var http = _httpClientFactory.CreateClient(HttpClientName);
                using var req = new HttpRequestMessage(HttpMethod.Put, presignedUrl)
                {
                    // StreamContent.Dispose() disposes the stream it wraps, and req is
                    // disposed at the end of every attempt (including ones we retry after).
                    // Wrap file so that doesn't tear down the caller's stream out from
                    // under the next attempt's Seek(0).
                    Content = new StreamContent(new NonDisposingStream(file)),
                };
                // Content-Type MUST match the value used when the URL was signed,
                // otherwise Supabase/S3 will reject with SignatureDoesNotMatch.
                req.Content.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

                using var resp = await http.SendAsync(req);

                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("S3 upload complete: key={Key} attempt={Attempt}", normalised, attempt);
                    return normalised;
                }

                string body = string.Empty;
                try { body = await resp.Content.ReadAsStringAsync(); } catch { /* best-effort */ }
                _logger.LogError(
                    "[STORAGE-DIAG] S3 Upload non-success: status={Status} bucket={Bucket} endpoint={Endpoint} region={Region} usePathStyle={UsePathStyle} key={Key} attempt={Attempt}/{MaxAttempts} body={Body}",
                    (int)resp.StatusCode, _options.Bucket, _options.Endpoint, _options.Region, _options.UsePathStyle,
                    normalised, attempt, UploadMaxAttempts, body.Length > 500 ? body[..500] : body);

                if (!isLastAttempt && IsTransientStatus(resp.StatusCode))
                {
                    await Task.Delay(RetryDelay(attempt));
                    continue;
                }

                throw new InvalidOperationException(
                    $"S3 upload failed: {(int)resp.StatusCode} {resp.StatusCode}");
            }
            catch (HttpRequestException ex) when (!isLastAttempt)
            {
                _logger.LogWarning(ex,
                    "[STORAGE-DIAG] S3 Upload HttpRequestException, retrying: bucket={Bucket} key={Key} attempt={Attempt}/{MaxAttempts} message={Message} innerType={InnerType} innerMessage={InnerMessage}",
                    _options.Bucket, normalised, attempt, UploadMaxAttempts, ex.Message,
                    ex.InnerException?.GetType().FullName, ex.InnerException?.Message);
                await Task.Delay(RetryDelay(attempt));
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex,
                    "[STORAGE-DIAG] S3 Upload HttpRequestException: bucket={Bucket} key={Key} message={Message} innerType={InnerType} innerMessage={InnerMessage}",
                    _options.Bucket, normalised, ex.Message,
                    ex.InnerException?.GetType().FullName, ex.InnerException?.Message);
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex,
                    "[STORAGE-DIAG] S3 Upload timeout/canceled: bucket={Bucket} key={Key} attempt={Attempt}/{MaxAttempts} message={Message}",
                    _options.Bucket, normalised, attempt, UploadMaxAttempts, ex.Message);
                throw;
            }
        }

        // Unreachable: the loop above always either returns on success or throws on the
        // last attempt.
        throw new InvalidOperationException($"S3 upload failed: retries exhausted for key={normalised}");
    }

    private static bool IsTransientStatus(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.InternalServerError
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(300d * attempt);

    /// <summary>
    /// Delegates everything to the wrapped stream except Dispose/DisposeAsync, which are
    /// no-ops — the caller (not StreamContent) owns the wrapped stream's lifetime. Needed
    /// because UploadAsync's retry loop re-seeks and resends the same stream across
    /// attempts, but each attempt's HttpRequestMessage/StreamContent gets disposed at the
    /// end of that attempt.
    /// </summary>
    private sealed class NonDisposingStream : Stream
    {
        private readonly Stream _inner;
        public NonDisposingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            _inner.CopyToAsync(destination, bufferSize, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing) { /* caller owns _inner's lifetime */ }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public string GenerateSignedUrl(string key)
    {
        var normalised = NormaliseKey(key);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = normalised,
            Expires = DateTime.UtcNow.AddMinutes(15), // H6: reduced from 1h
            Verb = HttpVerb.GET,
        };
        return _client.GetPreSignedURL(request);
    }

    /// <summary>
    /// Pre-signed URL that sets Content-Disposition: attachment so the
    /// browser triggers a "Save As" download instead of playing inline.
    /// </summary>
    public string GenerateDownloadUrl(string key, string filename)
    {
        var normalised = NormaliseKey(key);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = normalised,
            Expires = DateTime.UtcNow.AddMinutes(30), // H6: reduced from 1h
            Verb = HttpVerb.GET,
            ResponseHeaderOverrides =
            {
                ContentDisposition = $"attachment; filename=\"{filename}\"",
                ContentType = "application/octet-stream",
            },
        };
        return _client.GetPreSignedURL(request);
    }

    public string GetPublicUrl(string key)
    {
        var normalised = NormaliseKey(key);
        if (!string.IsNullOrWhiteSpace(_options.PublicUrl))
            return $"{_options.PublicUrl.TrimEnd('/')}/{normalised}";
        // No public URL configured — return bare key.
        // The API layer resolves bare keys to /images/{key} proxy URLs,
        // so storing just the key is safe and avoids expiring signed URLs.
        return normalised;
    }

    public string? GenerateUploadUrl(string key, string contentType)
    {
        var normalised = NormaliseKey(key);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = normalised,
            Expires = DateTime.UtcNow.AddMinutes(15),
            Verb = HttpVerb.PUT,
            ContentType = contentType,
        };
        return _client.GetPreSignedURL(request);
    }

    public Task<StorageFile?> OpenReadAsync(string key) => OpenReadAsync(key, null);

    public async Task<StorageFile?> OpenReadAsync(string key, string? rangeHeader)
    {
        var normalised = NormaliseKey(key);
        string presignedUrl;
        try
        {
            presignedUrl = _client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = _options.Bucket,
                Key = normalised,
                Expires = DateTime.UtcNow.AddMinutes(5),
                Verb = HttpVerb.GET,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[STORAGE-DIAG] S3 OpenRead presign failed: type={ExceptionType} bucket={Bucket} key={Key} message={Message}",
                ex.GetType().FullName, _options.Bucket, normalised, ex.Message);
            return null;
        }

        HttpResponseMessage? resp = null;
        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, presignedUrl);

            // Forward the client's Range header to the origin so S3/R2 returns a
            // 206 Partial Content response with Content-Range + partial Content-Length.
            // This is essential for Safari and iOS AVPlayer, both of which probe for
            // range support with a small initial Range request and refuse to play
            // when the response lacks Content-Length / Content-Range.
            if (!string.IsNullOrWhiteSpace(rangeHeader) &&
                !req.Headers.TryAddWithoutValidation("Range", rangeHeader))
            {
                _logger.LogDebug(
                    "[STORAGE-DIAG] S3 OpenRead ignoring malformed Range header: {Range}",
                    rangeHeader);
            }

            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "[STORAGE-DIAG] S3 OpenRead NotFound: bucket={Bucket} key={Key}",
                    _options.Bucket, normalised);
                resp.Dispose();
                return null;
            }

            // Preserve 416 so the API can return RFC-compliant Content-Range:
            // bytes */TOTAL rather than incorrectly converting it to a 404.
            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                _logger.LogInformation(
                    "[STORAGE-DIAG] S3 OpenRead RangeNotSatisfiable: bucket={Bucket} key={Key} range={Range}",
                    _options.Bucket, normalised, rangeHeader);
                var total = resp.Content.Headers.ContentRange?.Length;
                var rangeErrorResponse = resp;
                resp = null;
                return new StorageFile
                {
                    Stream = Stream.Null,
                    ContentType = rangeErrorResponse.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
                    Length = 0,
                    TotalLength = total,
                    ContentRange = total.HasValue ? $"bytes */{total.Value}" : null,
                    IsRangeNotSatisfiable = true,
                    StatusCode = 416,
                    OwnedResource = rangeErrorResponse,
                };
            }

            if (!resp.IsSuccessStatusCode)
            {
                string body = string.Empty;
                try { body = await resp.Content.ReadAsStringAsync(); } catch { /* best-effort */ }
                _logger.LogError(
                    "[STORAGE-DIAG] S3 OpenRead non-success: status={Status} bucket={Bucket} endpoint={Endpoint} region={Region} usePathStyle={UsePathStyle} key={Key} body={Body}",
                    (int)resp.StatusCode, _options.Bucket, _options.Endpoint, _options.Region, _options.UsePathStyle,
                    normalised, body.Length > 500 ? body[..500] : body);
                resp.Dispose();
                return null;
            }

            var stream = await resp.Content.ReadAsStreamAsync();
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var length = resp.Content.Headers.ContentLength;

            var isPartial = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
            string? contentRangeHeader = null;
            long? totalLength = length;
            if (resp.Content.Headers.ContentRange is { } cr)
            {
                contentRangeHeader = cr.ToString();
                if (cr.Length.HasValue)
                    totalLength = cr.Length;
            }

            // Transfer ownership of the HttpResponseMessage to the StorageFile —
            // disposing the StorageFile will dispose both the stream and the response.
            var owned = resp;
            resp = null;
            return new StorageFile
            {
                Stream = stream,
                ContentType = contentType,
                Length = length,
                TotalLength = totalLength,
                IsPartialContent = isPartial,
                ContentRange = contentRangeHeader,
                StatusCode = (int)owned.StatusCode,
                OwnedResource = owned,
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "[STORAGE-DIAG] S3 OpenRead HttpRequestException: bucket={Bucket} key={Key} message={Message} innerType={InnerType} innerMessage={InnerMessage}",
                _options.Bucket, normalised, ex.Message,
                ex.InnerException?.GetType().FullName, ex.InnerException?.Message);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex,
                "[STORAGE-DIAG] S3 OpenRead timeout/canceled: bucket={Bucket} key={Key} message={Message}",
                _options.Bucket, normalised, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[STORAGE-DIAG] S3 OpenRead unexpected exception: type={ExceptionType} bucket={Bucket} key={Key} message={Message}",
                ex.GetType().FullName, _options.Bucket, normalised, ex.Message);
            return null;
        }
        finally
        {
            // If we bailed out before transferring ownership, dispose.
            resp?.Dispose();
        }
    }

    public async Task<StorageObjectMetadata?> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        var normalised = NormaliseKey(key);
        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = normalised,
            Expires = DateTime.UtcNow.AddMinutes(2),
            Verb = HttpVerb.HEAD,
        });
        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var etag = response.Headers.ETag?.Tag?.Trim('"');
        var lastModified = response.Content.Headers.LastModified?.UtcDateTime;
        return new StorageObjectMetadata(
            normalised,
            response.Content.Headers.ContentLength ?? 0,
            response.Content.Headers.ContentType?.MediaType,
            etag,
            lastModified);
    }

    public async IAsyncEnumerable<StorageObjectMetadata> ListAsync(
        string? prefix = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? continuation = null;
        do
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _options.Bucket,
                Prefix = string.IsNullOrWhiteSpace(prefix) ? null : NormaliseKey(prefix),
                ContinuationToken = continuation,
                MaxKeys = 500,
            }, ct);
            foreach (var item in response.S3Objects)
            {
                ct.ThrowIfCancellationRequested();
                yield return new StorageObjectMetadata(
                    item.Key,
                    item.Size,
                    null,
                    item.ETag?.Trim('"'),
                    item.LastModified.ToUniversalTime());
            }
            continuation = response.IsTruncated ? response.NextContinuationToken : null;
        } while (!string.IsNullOrEmpty(continuation));
    }

    /// <summary>
    /// Runtime storage probe. Attempts to fetch the configured sample object
    /// via the presigned URL + HttpClient path — this is the same code path
    /// used by image/audio reads, so a successful probe proves the real read
    /// path works (not just some unrelated SDK call).
    ///
    /// Returns a structured result instead of throwing.
    /// </summary>
    public async Task<StorageProbeResult> ProbeAsync(string? sampleKey = null)
    {
        var result = new StorageProbeResult
        {
            Bucket = _options.Bucket,
            Endpoint = _options.Endpoint,
            Region = _options.Region,
            UsePathStyle = _options.UsePathStyle,
        };

        // Always exercise the real network read path. A presigned URL that merely
        // *generates* locally proves nothing about credentials, region, or endpoint —
        // it is pure client-side crypto and succeeds even when every real read 403s.
        // Without a caller-supplied key we HEAD a sentinel: a healthy bucket answers
        // 404 (signing OK, object simply absent), broken credentials/region answer
        // 403 SignatureDoesNotMatch, and a wrong endpoint fails to connect. This is
        // what lets the startup probe and /qa-preflight catch a misconfigured store
        // instead of reporting a false green while audio playback is dead.
        var normalised = NormaliseKey(
            string.IsNullOrWhiteSpace(sampleKey) ? "probe/__storage_health__" : sampleKey);
        result.SampleKey = normalised;

        string presignedUrl;
        try
        {
            presignedUrl = _client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = _options.Bucket,
                Key = normalised,
                Expires = DateTime.UtcNow.AddMinutes(1),
                Verb = HttpVerb.HEAD,
            });
        }
        catch (Exception ex)
        {
            result.HeadBucketOk = false;
            result.HeadBucketError = $"presign failed: {ex.GetType().Name}: {ex.Message}";
            return result;
        }

        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Head, presignedUrl);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            // HEAD 200 means credentials, endpoint, signing, and key all work end-to-end.
            // HEAD 404 means credentials + signing work but the sample object is missing.
            // Anything else is a real config failure.
            if (resp.IsSuccessStatusCode)
            {
                result.HeadBucketOk = true;
                result.HeadObjectOk = true;
                result.SampleLength = resp.Content.Headers.ContentLength;
                result.SampleContentType = resp.Content.Headers.ContentType?.MediaType;
            }
            else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Signing works — object is just absent.
                result.HeadBucketOk = true;
                result.HeadObjectOk = false;
                result.HeadObjectError = "404 NotFound";
            }
            else
            {
                string body = string.Empty;
                try { body = await resp.Content.ReadAsStringAsync(); } catch { /* best-effort */ }
                var err = $"{(int)resp.StatusCode} {resp.StatusCode}: {(body.Length > 300 ? body[..300] : body)}";
                result.HeadBucketOk = false;
                result.HeadBucketError = err;
                result.HeadObjectOk = false;
                result.HeadObjectError = err;
            }
        }
        catch (Exception ex)
        {
            var err = $"{ex.GetType().Name}: {ex.Message}";
            result.HeadBucketOk = false;
            result.HeadBucketError = err;
            result.HeadObjectOk = false;
            result.HeadObjectError = err;
        }

        return result;
    }

    public async Task DeleteAsync(string key)
    {
        var normalised = NormaliseKey(key);
        string presignedUrl;
        try
        {
            presignedUrl = _client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = _options.Bucket,
                Key = normalised,
                Expires = DateTime.UtcNow.AddMinutes(5),
                Verb = HttpVerb.DELETE,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[STORAGE-DIAG] S3 Delete presign failed: bucket={Bucket} key={Key} message={Message}",
                _options.Bucket, normalised, ex.Message);
            throw;
        }

        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Delete, presignedUrl);
            using var resp = await http.SendAsync(req);

            // S3 DELETE returns 204 on success, and 204 even when the key didn't exist.
            // Treat 404 as success so repeat deletes are idempotent.
            if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return;

            string body = string.Empty;
            try { body = await resp.Content.ReadAsStringAsync(); } catch { /* best-effort */ }
            _logger.LogError(
                "[STORAGE-DIAG] S3 Delete non-success: status={Status} bucket={Bucket} key={Key} body={Body}",
                (int)resp.StatusCode, _options.Bucket, normalised, body.Length > 500 ? body[..500] : body);
            throw new InvalidOperationException(
                $"S3 delete failed: {(int)resp.StatusCode} {resp.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "[STORAGE-DIAG] S3 Delete HttpRequestException: bucket={Bucket} key={Key} message={Message}",
                _options.Bucket, normalised, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Strip path prefixes so callers can pass keys stored in various formats:
    /// "/uploads/tracks/..." → "tracks/...", "/audio/demo1.mp3" → "audio/demo1.mp3",
    /// "https://endpoint/bucket/tracks/..." → "tracks/...".
    /// </summary>
    private string NormaliseKey(string key)
    {
        // Strip full URL prefix (S3 endpoint + bucket)
        if (key.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var bucketPath = $"/{_options.Bucket}/";
            var idx = key.IndexOf(bucketPath, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return key[(idx + bucketPath.Length)..];
            // Bucket not in path (e.g. r2.dev public URLs): extract the path component
            return new Uri(key).AbsolutePath.TrimStart('/');
        }
        // Strip /uploads/ prefix (local storage format)
        if (key.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            return key["/uploads/".Length..];
        // Strip leading slash
        if (key.StartsWith("/", StringComparison.Ordinal))
            return key[1..];
        return key;
    }
}
