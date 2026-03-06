using Cambrian.Api.Services.Interfaces;

namespace Cambrian.Api.Infrastructure;

public class R2ObjectStorage : IObjectStorage
{
    public async Task<string> UploadAsync(Stream file, string fileName)
    {
        var key = Guid.NewGuid() + "_" + fileName;
        Directory.CreateDirectory("uploads");

        using var fs = File.Create($"uploads/{key}");
        await file.CopyToAsync(fs);

        return key;
    }

    public string GenerateSignedUrl(string key, TimeSpan expiry)
    {
        return $"https://cdn.cambrianmusic.com/{key}";
    }
}
