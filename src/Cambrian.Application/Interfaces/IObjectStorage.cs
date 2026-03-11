namespace Cambrian.Application.Interfaces;

/// <summary>
/// Abstraction for object/file storage (R2, S3, local disk, etc.).
/// Implementations live in the Infrastructure or Api layer.
/// </summary>
public interface IObjectStorage
{
    Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg");

    string GenerateSignedUrl(string key);

    /// <summary>
    /// Open a readable stream for the stored object.
    /// Returns null when the object does not exist (local) or cannot be opened.
    /// </summary>
    Task<StorageFile?> OpenReadAsync(string key);

    Task DeleteAsync(string key);
}

/// <summary>
/// Wrapper for a file retrieved from object storage.
/// Callers must dispose the <see cref="Stream"/> when finished.
/// </summary>
public sealed class StorageFile : IDisposable
{
    public Stream Stream { get; init; } = null!;
    public string ContentType { get; init; } = "application/octet-stream";
    public long? Length { get; init; }

    public void Dispose() => Stream.Dispose();
}
