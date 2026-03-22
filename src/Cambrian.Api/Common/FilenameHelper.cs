namespace Cambrian.Api.Common;

/// <summary>
/// Shared filename utilities used by controllers that serve file downloads.
/// </summary>
internal static class FilenameHelper
{
    /// <summary>
    /// Strips characters that are invalid in filenames, returning a safe string
    /// suitable for Content-Disposition headers. Falls back to "track" if the
    /// result is empty or whitespace-only.
    /// </summary>
    internal static string SanitizeFilename(string raw)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var result = new char[raw.Length];
        var count = 0;
        foreach (var c in raw)
        {
            if (!invalid.Contains(c))
                result[count++] = c;
        }
        var sanitized = new string(result, 0, count);
        return string.IsNullOrWhiteSpace(sanitized) ? "track" : sanitized;
    }
}
