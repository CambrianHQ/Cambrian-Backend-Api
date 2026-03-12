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
        var configured = options.Value.LocalPath ?? "wwwroot/uploads";

        // Resolve to an absolute path — on Render the CWD may differ from
        // where the published app files live (e.g. /opt/render/project/src/...).
        _basePath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);

        Directory.CreateDirectory(_basePath);
        Console.WriteLine($"[LocalObjectStorage] basePath = {_basePath}  (exists={Directory.Exists(_basePath)})");
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
        return GetPublicUrl(key);
    }

    public string GetPublicUrl(string key)
    {
        // Strip leading /uploads/ if the caller passed the full relative URL
        if (key.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            return key;
        // Strip leading slash to build a consistent /uploads/ URL
        var clean = key.StartsWith("/", StringComparison.Ordinal) ? key[1..] : key;
        return $"/uploads/{clean}";
    }

    public Task<StorageFile?> OpenReadAsync(string key)
    {
        // Normalise the key to a relative path under _basePath.
        // Callers may pass paths like "/uploads/audio/demo1.mp3", "/audio/demo1.mp3",
        // "audio/demo1.mp3", or "uploads/audio/demo1.mp3".
        var normalised = key;
        if (normalised.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            normalised = normalised["/uploads/".Length..];
        else if (normalised.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
            normalised = normalised["uploads/".Length..];
        else if (normalised.StartsWith("/", StringComparison.Ordinal))
            normalised = normalised[1..]; // strip leading slash

        var filePath = Path.Combine(_basePath, normalised.Replace('/', Path.DirectorySeparatorChar));

        // Fallback: try CWD-relative path if the absolute path doesn't exist
        // (handles cases where files were deployed alongside the app)
        if (!File.Exists(filePath))
        {
            var cwdFallback = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", normalised.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(cwdFallback))
                filePath = cwdFallback;
            else
            {
                // Also try wwwroot directly (e.g. for /audio/demo1.mp3 served as wwwroot/audio/...)
                var wwwrootDirect = Path.Combine(AppContext.BaseDirectory, "wwwroot", normalised.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(wwwrootDirect))
                    filePath = wwwrootDirect;
                else
                {
                    var cwdWwwroot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", normalised.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(cwdWwwroot))
                        filePath = cwdWwwroot;
                    else
                    {
                        Console.WriteLine($"[LocalObjectStorage] File not found: key={key}, tried: {filePath}, {cwdFallback}, {wwwrootDirect}, {cwdWwwroot}");
                        return Task.FromResult<StorageFile?>(null);
                    }
                }
            }
        }

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
