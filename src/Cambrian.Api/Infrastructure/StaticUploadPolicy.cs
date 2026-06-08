namespace Cambrian.Api.Infrastructure;

/// <summary>
/// Decides which files under the static <c>uploads</c> directory may be served directly.
///
/// Uploaded <b>audio</b> (under <c>uploads/tracks</c>) must never be served as a static file —
/// it is gated behind the authenticated <c>/stream/{id}/audio</c> endpoint. Public creator
/// <b>images</b> (cover art, avatars, banners) are served directly and must remain reachable.
///
/// This previously lived as an inline lambda in <c>Program.cs</c> that allow-listed only
/// <c>covers</c>, which silently 403'd every uploaded avatar and banner (bug B1).
/// </summary>
public static class StaticUploadPolicy
{
    // Sub-folders under uploads/ that hold publicly servable images.
    private static readonly string[] PublicImageFolders =
    {
        "/covers/",
        "/avatars/",
        "/banners/",
    };

    /// <summary>
    /// Returns true when the given resolved physical path is an uploaded file that must NOT be
    /// served directly (i.e. uploaded audio). Returns false for public image uploads and for any
    /// path that is not under an uploads directory (those are served normally).
    /// </summary>
    public static bool ShouldBlock(string? physicalPath)
    {
        if (string.IsNullOrEmpty(physicalPath))
            return false;

        // Normalise Windows separators so substring checks are platform-independent.
        var normalized = physicalPath.Replace('\\', '/');

        // Only files under an uploads directory are subject to this policy.
        if (normalized.IndexOf("/uploads/", StringComparison.OrdinalIgnoreCase) < 0
            && !normalized.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
            return false;

        // Public creator images are served; everything else under uploads (audio) is blocked.
        foreach (var folder in PublicImageFolders)
        {
            if (normalized.Contains(folder, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
