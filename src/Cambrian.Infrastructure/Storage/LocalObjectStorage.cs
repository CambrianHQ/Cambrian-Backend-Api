using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.Storage;

/// <summary>
/// Local disk storage for development. Stores files under wwwroot/uploads.
/// </summary>
public sealed class LocalObjectStorage : IObjectStorage
{
    private readonly string _basePath;

    public LocalObjectStorage(IOptions<StorageOptions> options)
    {
        _basePath = options.Value.LocalPath ?? "wwwroot/uploads";
        Directory.CreateDirectory(_basePath);
    }

    public async Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
    {
        var filePath = Path.Combine(_basePath, key.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var fs = File.Create(filePath);
        await file.CopyToAsync(fs);

        // Return a relative URL the API can serve via UseStaticFiles
        return $"/uploads/{key}";
    }

    public string GenerateSignedUrl(string key)
    {
        // Local dev — no signing needed
        return $"/uploads/{key}";
    }

    public Task DeleteAsync(string key)
    {
        var filePath = Path.Combine(_basePath, key.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }
}
