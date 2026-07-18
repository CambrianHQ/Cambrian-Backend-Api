using Cambrian.Application.DTOs.Catalog;
using Microsoft.AspNetCore.Http;

namespace Cambrian.Application.Interfaces;

public interface IUploadService
{
    /// <summary>
    /// <paramref name="idempotencyKey"/> is a stable, client-generated id for one
    /// logical upload attempt (preserved across retries of the same attempt, new
    /// for a genuinely new upload). When supplied, a replay of a completed upload
    /// returns the original result instead of creating a second track; a replay
    /// while the first attempt is still processing throws a distinct
    /// "processing" failure; reusing the key with a different payload throws a
    /// distinct "reused" failure. Optional — omitting it leaves the call
    /// unprotected against duplicate submission.
    /// </summary>
    Task<UploadTrackResponse> Upload(UploadTrackRequest request, string? idempotencyKey = null);

    Task<string> UploadCoverArtAsync(string creatorId, IFormFile coverArt);
}
