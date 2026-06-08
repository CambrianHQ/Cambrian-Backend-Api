using System.Security.Cryptography;

namespace Cambrian.Application.Provenance;

/// <summary>
/// Stable content hashing for provenance. SHA-256 over the raw audio bytes,
/// rendered as lowercase hex (64 chars). Shared by the upload pipeline and the
/// one-off backfill so both produce identical digests for identical bytes.
/// </summary>
public static class ContentHashing
{
    /// <summary>Algorithm label surfaced alongside the digest.</summary>
    public const string Algorithm = "SHA-256";

    /// <summary>
    /// Compute the SHA-256 hex digest of a stream's full contents. Seeks to the
    /// start before hashing and resets to the start afterwards (when the stream is
    /// seekable) so the caller can immediately re-read the bytes (e.g. to upload them).
    /// </summary>
    public static string ComputeSha256Hex(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);

        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
