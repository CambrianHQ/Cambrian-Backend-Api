namespace Cambrian.Application.Validation;

/// <summary>
/// Validation for "External" proof-video URLs (VideoType != "YouTube"): must be a
/// well-formed absolute http(s) URL with no embedded credentials. This is metadata
/// storage only — the backend never fetches these URLs server-side.
/// </summary>
public static class ExternalVideoUrlValidator
{
    public static bool IsValid(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        return !string.IsNullOrEmpty(uri.Host);
    }
}
