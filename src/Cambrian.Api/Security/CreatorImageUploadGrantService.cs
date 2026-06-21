using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace Cambrian.Api.Security;

public sealed class CreatorImageUploadGrantService
{
    public const long MaxImageBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(5);

    private readonly IDataProtector _protector;
    private readonly IMemoryCache _cache;
    private readonly object _gate = new();

    public CreatorImageUploadGrantService(
        IDataProtectionProvider dataProtection,
        IMemoryCache cache)
    {
        _protector = dataProtection.CreateProtector("Cambrian.CreatorImageUploadGrant.v1");
        _cache = cache;
    }

    public CreatorImageUploadGrant Issue(
        string userId,
        string purpose,
        string extension,
        string contentType)
    {
        var normalizedPurpose = purpose.Equals("cover", StringComparison.OrdinalIgnoreCase)
            ? "cover"
            : purpose.Equals("profile", StringComparison.OrdinalIgnoreCase)
                ? "profile"
                : throw new ArgumentException("Image purpose must be profile or cover.");

        var nonce = Guid.NewGuid().ToString("N");
        var folder = normalizedPurpose == "cover" ? "creator-covers" : "creator-profiles";
        var key = $"{folder}/{userId}/{nonce}{extension.ToLowerInvariant()}";
        var expiresAt = DateTimeOffset.UtcNow.Add(GrantLifetime);
        var payload = new CreatorImageUploadGrantPayload(
            userId,
            key,
            contentType,
            MaxImageBytes,
            expiresAt,
            normalizedPurpose,
            nonce);

        var token = _protector.Protect(JsonSerializer.Serialize(payload));
        _cache.Set(CacheKey(nonce), true, expiresAt);
        return new CreatorImageUploadGrant(token, payload);
    }

    public bool TryConsume(
        string token,
        string currentUserId,
        string key,
        string contentType,
        out CreatorImageUploadGrantPayload payload)
    {
        payload = default!;
        try
        {
            var json = _protector.Unprotect(token);
            var parsed = JsonSerializer.Deserialize<CreatorImageUploadGrantPayload>(json);
            if (parsed is null
                || parsed.ExpiresAt <= DateTimeOffset.UtcNow
                || !string.Equals(parsed.UserId, currentUserId, StringComparison.Ordinal)
                || !string.Equals(parsed.Key, key, StringComparison.Ordinal)
                || !string.Equals(parsed.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            lock (_gate)
            {
                if (!_cache.TryGetValue(CacheKey(parsed.Nonce), out _))
                    return false;
                _cache.Remove(CacheKey(parsed.Nonce));
            }

            payload = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CacheKey(string nonce) => $"creator-image-grant:{nonce}";
}

public sealed record CreatorImageUploadGrant(
    string Token,
    CreatorImageUploadGrantPayload Payload);

public sealed record CreatorImageUploadGrantPayload(
    string UserId,
    string Key,
    string ContentType,
    long MaxSize,
    DateTimeOffset ExpiresAt,
    string Purpose,
    string Nonce);
