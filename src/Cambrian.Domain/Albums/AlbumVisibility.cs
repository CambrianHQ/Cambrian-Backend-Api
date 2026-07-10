namespace Cambrian.Domain.Albums;

/// <summary>
/// Canonical album (TrackCollection) visibility vocabulary and the single
/// source of truth for what an album's visibility <em>means</em>.
///
/// Four states, stored lowercase:
/// <list type="bullet">
///   <item><description><c>draft</c> — owner-only work in progress. Never public.</description></item>
///   <item><description><c>public</c> — visible to everyone and listed on the creator's page.</description></item>
///   <item><description><c>unlisted</c> — reachable by a direct slug/id link, but never listed.</description></item>
///   <item><description><c>private</c> — owner-only. Never public.</description></item>
/// </list>
///
/// The legacy 2-state model used <c>hidden</c>; it maps to <c>private</c>.
/// All checks are fail-closed: any unknown or legacy value is treated as
/// NOT publicly visible, so a bad value can never leak an album.
/// </summary>
public static class AlbumVisibility
{
    public const string Draft = "draft";
    public const string Public = "public";
    public const string Unlisted = "unlisted";
    public const string Private = "private";

    /// <summary>The default applied when a creator does not specify a visibility.</summary>
    public const string Default = Public;

    /// <summary>
    /// Normalize caller input to a canonical value, or return null when the
    /// value is not a recognized visibility. Legacy <c>hidden</c> becomes
    /// <c>private</c>. Callers treat null as "invalid" (400) or "keep stored".
    /// </summary>
    public static string? Normalize(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant();
        return value switch
        {
            Draft or Public or Unlisted or Private => value,
            "hidden" => Private, // backward-compat with the retired 2-state model
            _ => null,
        };
    }

    /// <summary>True when <paramref name="raw"/> is a recognized visibility (incl. the <c>hidden</c> alias).</summary>
    public static bool IsValid(string? raw) => Normalize(raw) is not null;

    /// <summary>
    /// Whether an album with this visibility may be shown to anyone via a direct
    /// link (slug or id). Only <c>public</c> and <c>unlisted</c> qualify; every
    /// other value — including <c>draft</c>, <c>private</c>, <c>hidden</c>, and
    /// anything unrecognized — is owner-only. Fail-closed.
    /// </summary>
    public static bool IsPubliclyVisible(string? visibility)
    {
        var value = Normalize(visibility);
        return value is Public or Unlisted;
    }

    /// <summary>
    /// Whether an album with this visibility appears in public listings (the
    /// creator's album grid, discovery, etc.). Only <c>public</c> qualifies —
    /// <c>unlisted</c> is reachable by link but never listed. Fail-closed.
    /// </summary>
    public static bool IsPubliclyListed(string? visibility) => Normalize(visibility) == Public;
}
