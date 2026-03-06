namespace Cambrian.Infrastructure.Storage;

public interface IObjectStorage
{
    Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg");

    string GenerateSignedUrl(string key);

    Task DeleteAsync(string key);
}
