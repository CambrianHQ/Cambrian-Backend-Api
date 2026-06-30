using Microsoft.Extensions.Configuration;

namespace Cambrian.Application.Services;

/// <summary>
/// Builds the absolute, public URLs returned by the public API. Canonical (SEO) URLs are
/// built from the public site base (<c>App:FrontendUrl</c>); media URLs are built from the
/// API base (<c>App:ApiBaseUrl</c>, falling back to the site base) and routed through the
/// existing image / stream proxies so a raw storage key never leaves the backend.
/// All bases are configured, never derived from the inbound request host, so production
/// output can never contain a localhost URL.
/// </summary>
public interface IPublicUrlResolver
{
    string TrackUrl(string cambrianTrackId);
    string CreatorUrl(string slug);
    string GenreUrl(string genreSlug);
    string SiteUrl(string relativePath);
    string AudioStreamUrl(string trackId);
    string? ImageUrl(string? rawImage);
    string Slugify(string value);
}

/// <inheritdoc />
public sealed class PublicUrlResolver : IPublicUrlResolver
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private const string DefaultSiteBase = "https://cambrianmusic.com";

    private readonly string _siteBase;
    private readonly string _apiBase;

    public PublicUrlResolver(IConfiguration config)
    {
        var site = config["App:FrontendUrl"];
        _siteBase = NormalizeBase(string.IsNullOrWhiteSpace(site) ? DefaultSiteBase : site);

        var api = config["App:ApiBaseUrl"];
        _apiBase = NormalizeBase(string.IsNullOrWhiteSpace(api) ? _siteBase : api);
    }

    public string TrackUrl(string cambrianTrackId) => SiteUrl($"track/{Encode(cambrianTrackId)}");
    public string CreatorUrl(string slug) => SiteUrl($"creator/{Encode(slug)}");
    public string GenreUrl(string genreSlug) => SiteUrl($"genres/{Encode(genreSlug)}");

    public string SiteUrl(string relativePath) => Combine(_siteBase, relativePath);

    public string AudioStreamUrl(string trackId) => Combine(_apiBase, $"stream/{Encode(trackId)}/audio");

    public string? ImageUrl(string? rawImage)
    {
        if (string.IsNullOrWhiteSpace(rawImage))
            return null;

        // Local uploads or an already-proxied image path — serve from the API base.
        if (rawImage.StartsWith("/uploads/", OIC) || rawImage.StartsWith("/images/", OIC))
            return Combine(_apiBase, rawImage);

        if (rawImage.StartsWith("http://", OIC) || rawImage.StartsWith("https://", OIC))
        {
            if (Uri.TryCreate(rawImage, UriKind.Absolute, out var uri))
            {
                if (uri.AbsolutePath.StartsWith("/uploads/", OIC) || uri.AbsolutePath.StartsWith("/images/", OIC))
                {
                    var pathWithQuery = string.IsNullOrEmpty(uri.Query)
                        ? uri.AbsolutePath
                        : $"{uri.AbsolutePath}{uri.Query}";
                    return Combine(_apiBase, pathWithQuery);
                }

                var key = StripBucketPrefix(uri.AbsolutePath.TrimStart('/'));
                if (!string.IsNullOrEmpty(key))
                    return Combine(_apiBase, $"images/{key}");
            }
            return rawImage; // unrecognized absolute URL — already public, pass through
        }

        // Bare object key (e.g. covers/abc/img.jpg) — proxy through /images/.
        return Combine(_apiBase, $"images/{StripBucketPrefix(rawImage)}");
    }

    public string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Encode(string value) => Uri.EscapeDataString(value ?? string.Empty);

    private static string NormalizeBase(string baseUrl) => baseUrl.TrimEnd('/');

    private static string Combine(string baseUrl, string relativePath)
    {
        var path = (relativePath ?? string.Empty).TrimStart('/');
        return $"{baseUrl}/{path}";
    }

    // Known first-segment prefixes for image object keys (mirrors BaseController).
    private static readonly HashSet<string> KnownImagePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "covers", "avatars", "banners", "creator-profiles", "creator-covers", "images"
    };

    private static string StripBucketPrefix(string key)
    {
        var slash = key.IndexOf('/');
        if (slash > 0)
        {
            var first = key[..slash];
            if (!KnownImagePrefixes.Contains(first))
                return key[(slash + 1)..];
        }
        return key;
    }
}
