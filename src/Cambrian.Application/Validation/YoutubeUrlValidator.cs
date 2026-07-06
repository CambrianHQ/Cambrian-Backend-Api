using System.Text.RegularExpressions;

namespace Cambrian.Application.Validation;

/// <summary>
/// Strict YouTube URL parsing for <c>TrackVideoProof</c> entries (VideoType == "YouTube").
/// Unlike the permissive host-only check used for the legacy Behind The Track process
/// video field, this extracts and validates the actual 11-character video id so
/// malformed or non-video YouTube URLs (channel pages, playlists, etc.) are rejected.
/// </summary>
public static class YoutubeUrlValidator
{
    private static readonly Regex VideoIdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    private static readonly HashSet<string> StandardHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com",
    };

    /// <summary>
    /// Returns true and the extracted 11-character video id when <paramref name="url"/> is a
    /// well-formed https(s) YouTube watch/embed/shorts/short-link URL for a single video.
    /// </summary>
    public static bool TryExtractVideoId(string? url, out string videoId)
    {
        videoId = "";
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // Reject embedded credentials (https://user:pass@host/...) — never a legitimate share link.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        string? candidate = null;
        var path = uri.AbsolutePath.Trim('/');

        if (string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            candidate = path.Split('/')[0];
        }
        else if (StandardHosts.Contains(uri.Host))
        {
            if (path.StartsWith("embed/", StringComparison.OrdinalIgnoreCase))
                candidate = path["embed/".Length..].Split('/')[0];
            else if (path.StartsWith("shorts/", StringComparison.OrdinalIgnoreCase))
                candidate = path["shorts/".Length..].Split('/')[0];
            else if (path.Equals("watch", StringComparison.OrdinalIgnoreCase))
                candidate = ExtractQueryValue(uri.Query, "v");
        }

        if (string.IsNullOrEmpty(candidate) || !VideoIdPattern.IsMatch(candidate))
            return false;

        videoId = candidate;
        return true;
    }

    public static bool IsValid(string? url) => TryExtractVideoId(url, out _);

    private static string? ExtractQueryValue(string query, string key)
    {
        query = query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var name = idx >= 0 ? pair[..idx] : pair;
            if (!string.Equals(name, key, StringComparison.Ordinal))
                continue;
            return idx >= 0 ? Uri.UnescapeDataString(pair[(idx + 1)..]) : "";
        }
        return null;
    }
}
