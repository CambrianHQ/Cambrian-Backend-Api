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

    /// <summary>
    /// Resolves a user-supplied key to an absolute path under <see cref="_basePath"/>,
    /// preventing path-traversal attacks (e.g. keys containing "../").
    /// </summary>
    private string SafeResolvePath(string key)
    {
        var combined = Path.Combine(_basePath, key.Replace('/', Path.DirectorySeparatorChar));
        var full = Path.GetFullPath(combined);
        if (!full.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path traversal denied for key: {key}");
        return full;
    }

    public async Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
    {
        var filePath = SafeResolvePath(key);
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

        var filePath = SafeResolvePath(normalised);

        // Fallback: try CWD-relative path if the absolute path doesn't exist
        // (handles cases where files were deployed alongside the app)
        if (!File.Exists(filePath))
        {
            var cwdBase = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads"));
            var cwdFallback = Path.GetFullPath(Path.Combine(cwdBase, normalised.Replace('/', Path.DirectorySeparatorChar)));
            if (cwdFallback.StartsWith(cwdBase, StringComparison.OrdinalIgnoreCase) && File.Exists(cwdFallback))
                filePath = cwdFallback;
            else
            {
                var wwwBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
                var wwwrootDirect = Path.GetFullPath(Path.Combine(wwwBase, normalised.Replace('/', Path.DirectorySeparatorChar)));
                if (wwwrootDirect.StartsWith(wwwBase, StringComparison.OrdinalIgnoreCase) && File.Exists(wwwrootDirect))
                    filePath = wwwrootDirect;
                else
                {
                    var cwdWwwroot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", normalised.Replace('/', Path.DirectorySeparatorChar)));
                    if (cwdWwwroot.StartsWith(cwdBase, StringComparison.OrdinalIgnoreCase) && File.Exists(cwdWwwroot))
                        filePath = cwdWwwroot;
                    else
                    {
                        Console.WriteLine($"[LocalObjectStorage] File not found: key={key}, tried primary and fallback paths");
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
        var filePath = SafeResolvePath(key);
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }
}
