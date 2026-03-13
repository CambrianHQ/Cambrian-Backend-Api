using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.Storage;

/// <summary>
/// S3-compatible object storage (works with AWS S3 and Cloudflare R2).
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage
{
    private readonly StorageOptions _options;
    private readonly AmazonS3Client _client;

    public S3ObjectStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;

        // R2 and modern S3 require Signature Version 4
        AWSConfigsS3.UseSignatureVersion4 = true;

        var config = new AmazonS3Config
        {
            ServiceURL = _options.Endpoint,
            ForcePathStyle = _options.UsePathStyle,
        };
        if (!string.IsNullOrWhiteSpace(_options.Region) && _options.Region != "auto")
            config.AuthenticationRegion = _options.Region;

        _client = new AmazonS3Client(
            _options.AccessKey,
            _options.SecretKey,
            config);
    }

    public async Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
    {
        var normalised = NormaliseKey(key);
        Console.WriteLine($"[S3] Uploading: bucket={_options.Bucket}, key={normalised}, contentType={contentType}, size={file.Length}");
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
        Console.WriteLine($"[S3] Upload complete: {normalised}");

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
            Expires = DateTime.UtcNow.AddHours(1),
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
            Expires = DateTime.UtcNow.AddHours(1),
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
        // Fallback: generate a short-lived signed URL
        return GenerateSignedUrl(normalised);
    }

    public async Task<StorageFile?> OpenReadAsync(string key)
    {
        try
        {
            var normalised = NormaliseKey(key);
            Console.WriteLine($"[S3] OpenRead: bucket={_options.Bucket}, key={normalised} (raw={key})");
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
            Console.WriteLine($"[S3] Not found: bucket={_options.Bucket}, key={NormaliseKey(key)}");
            return null;
        }
        catch (AmazonS3Exception ex)
        {
            Console.WriteLine($"[S3] ERROR: {ex.StatusCode} {ex.ErrorCode} — bucket={_options.Bucket}, key={NormaliseKey(key)}, message={ex.Message}");
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

