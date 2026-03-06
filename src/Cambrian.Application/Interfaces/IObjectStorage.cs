namespace Cambrian.Application.Interfaces;

/// <summary>
/// Abstraction for object/file storage (R2, S3, local disk, etc.).
/// Implementations live in the Infrastructure or Api layer.
/// </summary>
public interface IObjectStorage
{
    Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg");

    string GenerateSignedUrl(string key);

    Task DeleteAsync(string key);
}
