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
        // Strip leading /uploads/ if the caller passed the full relative URL
        if (key.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            return key;
        return $"/uploads/{key}";
    }

    public Task<StorageFile?> OpenReadAsync(string key)
    {
        // Strip leading /uploads/ if the caller passed the full relative URL
        var normalised = key;
        if (normalised.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            normalised = normalised["/uploads/".Length..];

        var filePath = Path.Combine(_basePath, normalised.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(filePath))
            return Task.FromResult<StorageFile?>(null);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

        var fs = File.OpenRead(filePath);
        return Task.FromResult<StorageFile?>(new StorageFile
        {
            Stream = fs,
            ContentType = contentType,
            Length = fs.Length,
        });
    }

    public Task DeleteAsync(string key)
    {
        var filePath = Path.Combine(_basePath, key.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }
}
