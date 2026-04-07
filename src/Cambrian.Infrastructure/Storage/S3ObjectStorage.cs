using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.Storage;

/// <summary>
/// S3-compatible object storage (works with AWS S3 and Cloudflare R2).
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage
{
    private readonly StorageOptions _options;
    private readonly AmazonS3Client _client;
    private readonly ILogger<S3ObjectStorage> _logger;

    public S3ObjectStorage(IOptions<StorageOptions> options, ILogger<S3ObjectStorage> logger)
    {
        _options = options.Value;
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
        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = normalised,
            InputStream = file,
            ContentType = contentType,
            // Cloudflare R2 does not support STREAMING-AWS4-HMAC-SHA256-PAYLOAD
            // (chunked transfer encoding with signed payloads).  Disabling payload
            // signing makes the SDK send an unsigned payload hash instead, which R2 accepts.
            DisablePayloadSigning = true,
        };
        await _client.PutObjectAsync(request);
        _logger.LogDebug("S3 upload complete: key={Key}", normalised);

        // Return just the key — callers use GetPublicUrl / GenerateSignedUrl to build URLs.
        return normalised;
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

    public async Task<StorageFile?> OpenReadAsync(string key)
    {
        try
        {
            var normalised = NormaliseKey(key);
            _logger.LogDebug("S3 OpenRead: key={Key}", normalised);
            var response = await _client.GetObjectAsync(_options.Bucket, normalised);
            return new StorageFile
            {
                Stream = response.ResponseStream,
                ContentType = response.Headers.ContentType ?? "application/octet-stream",
                Length = response.ContentLength,
            };
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("S3 not found: key={Key}", NormaliseKey(key));
            return null;
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogError(ex, "S3 error: {StatusCode} {ErrorCode} key={Key}", ex.StatusCode, ex.ErrorCode, NormaliseKey(key));
            throw;
        }
    }

    public async Task DeleteAsync(string key)
    {
        await _client.DeleteObjectAsync(_options.Bucket, NormaliseKey(key));
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

