namespace Cambrian.Application.Interfaces;

/// <summary>
/// Abstraction for object/file storage (R2, S3, local disk, etc.).
/// Implementations live in the Infrastructure or Api layer.
/// </summary>
public interface IObjectStorage
{
    Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg");

    string GenerateSignedUrl(string key);

    /// <summary>Lifetime of generated signed URLs, or null when URLs do not expire.</summary>
    TimeSpan? SignedUrlLifetime => null;

    /// <summary>
    /// Generate a pre-signed URL that forces the browser to download (save)
    /// the file with the specified filename via Content-Disposition: attachment.
    /// </summary>
    string GenerateDownloadUrl(string key, string filename) => GenerateSignedUrl(key);

    /// <summary>
    /// Generate a pre-signed PUT URL for direct client uploads.
    /// Returns null when direct uploads aren't supported (e.g. local storage).
    /// </summary>
    string? GenerateUploadUrl(string key, string contentType) => null;

    /// <summary>
    /// Get a public (unsigned) URL for browsable assets like cover art.
    /// </summary>
    string GetPublicUrl(string key);

    /// <summary>
    /// Open a readable stream for the stored object.
    /// Returns null when the object does not exist (local) or cannot be opened.
    /// </summary>
    Task<StorageFile?> OpenReadAsync(string key);

    /// <summary>
    /// Open a readable stream for the stored object, forwarding an HTTP Range header
    /// when supplied. Implementations that talk to a remote store should issue a
    /// ranged GET to the origin so the returned <see cref="StorageFile"/> carries
    /// the partial-content metadata (status, Content-Range, Content-Length) needed
    /// for Safari/iOS &lt;audio&gt; playback. The default implementation falls back
    /// to the full-object read and is safe for implementations whose stream is
    /// already seekable (e.g. local disk).
    /// </summary>
    Task<StorageFile?> OpenReadAsync(string key, string? rangeHeader)
        => OpenReadAsync(key);

    Task DeleteAsync(string key);

    /// <summary>
    /// Runtime diagnostic probe. Implementations that talk to a remote store
    /// should attempt HeadBucket (and optional HeadObject if <paramref name="sampleKey"/>
    /// is provided) and return a structured result describing success or failure.
    /// Local implementations return a trivial success.
    /// Never throws — failures are reported in the returned object.
    /// </summary>
    Task<StorageProbeResult> ProbeAsync(string? sampleKey = null)
        => Task.FromResult(new StorageProbeResult
        {
            HeadBucketOk = true,
            Bucket = "(local)",
            Endpoint = "(local)",
        });
}

/// <summary>
/// Structured result of a storage probe — surfaces the exact failure layer
/// (credentials, endpoint, bucket, object) without throwing.
/// </summary>
public sealed class StorageProbeResult
{
    public string? Bucket { get; set; }
    public string? Endpoint { get; set; }
    public string? Region { get; set; }
    public bool UsePathStyle { get; set; }

    public bool HeadBucketOk { get; set; }
    public string? HeadBucketError { get; set; }
    public string? BucketLocation { get; set; }

    public string? SampleKey { get; set; }
    public bool? HeadObjectOk { get; set; }
    public string? HeadObjectError { get; set; }
    public long? SampleLength { get; set; }
    public string? SampleContentType { get; set; }
}

/// <summary>
/// Wrapper for a file retrieved from object storage.
/// Callers must dispose the <see cref="Stream"/> when finished.
/// </summary>
public sealed class StorageFile : IDisposable
{
    public Stream Stream { get; init; } = null!;
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>
    /// Length in bytes of the stream being returned. For a full-object read this
    /// is the total object size; for a ranged read this is the length of the
    /// partial byte range (matches HTTP Content-Length for the response body).
    /// </summary>
    public long? Length { get; init; }

    /// <summary>
    /// Total size of the underlying object in bytes, regardless of whether
    /// the stream is partial. Parsed from the "bytes X-Y/TOTAL" suffix of the
    /// origin Content-Range header for ranged responses, or from Content-Length
    /// for full-object responses.
    /// </summary>
    public long? TotalLength { get; init; }

    /// <summary>
    /// True when the stream represents an HTTP 206 Partial Content response.
    /// Consumers should emit 206 status and forward <see cref="ContentRange"/>.
    /// </summary>
    public bool IsPartialContent { get; init; }

    /// <summary>
    /// Raw HTTP Content-Range header value (e.g. "bytes 0-1023/5242880") when
    /// the origin returned a 206. Null for full-object reads.
    /// </summary>
    public string? ContentRange { get; init; }

    /// <summary>
    /// Optional extra resource whose lifetime is bound to this StorageFile
    /// (e.g. the HttpResponseMessage backing the Stream). Disposed together
    /// with the stream so callers don't need to know about the transport.
    /// </summary>
    public IDisposable? OwnedResource { get; init; }

    public void Dispose()
    {
        Stream.Dispose();
        OwnedResource?.Dispose();
    }
}
