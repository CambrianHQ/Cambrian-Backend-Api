namespace Cambrian.Api.Services.Interfaces;

public interface IObjectStorage
{
    Task<string> UploadAsync(Stream file, string fileName);

    string GenerateSignedUrl(string key, TimeSpan expiry);
}
