using Cambrian.Api.Services.Interfaces;

namespace Cambrian.Api.Services.Implementations;

public class R2ObjectStorage : IObjectStorage
{
    public async Task<string> UploadAsync(Stream file, string fileName)
    {
        // Placeholder until Cloudflare R2 SDK integration
        var key = Guid.NewGuid() + "_" + fileName;
        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        Directory.CreateDirectory(uploadsDir);

        using var fs = File.Create(Path.Combine(uploadsDir, key));
        await file.CopyToAsync(fs);

        return key;
    }

    public string GenerateSignedUrl(string key, TimeSpan expiry)
    {
        // Placeholder until R2 presigned URL generation is wired up
        return $"https://cdn.cambrianmusic.com/{key}";
    }
}
