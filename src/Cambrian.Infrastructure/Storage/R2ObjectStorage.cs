using Cambrian.Application.Interfaces;

namespace Cambrian.Infrastructure.Storage;

/// <summary>
/// Placeholder for Cloudflare R2 storage. Will be implemented when R2 credentials are available.
/// </summary>
public class R2ObjectStorage : IObjectStorage
{
    public Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
    {
        // TODO: Implement R2 upload via S3-compatible API
        var url = $"https://storage.cambrian.app/{key}";
        return Task.FromResult(url);
    }

    public string GenerateSignedUrl(string key)
    {
        // TODO: Implement signed URL generation
        return $"https://storage.cambrian.app/{key}?signed=dev";
    }

    public Task<StorageFile?> OpenReadAsync(string key)
    {
        // TODO: Implement R2 download
        return Task.FromResult<StorageFile?>(null);
    }

    public Task DeleteAsync(string key)
    {
        // TODO: Implement R2 delete
        return Task.CompletedTask;
    }
}
