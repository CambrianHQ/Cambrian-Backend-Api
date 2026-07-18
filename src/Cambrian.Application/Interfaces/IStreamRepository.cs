namespace Cambrian.Application.Interfaces;

/// <summary>
/// Read-only lifetime play projection used by creator/catalog surfaces. Qualified-play
/// mutation is exclusively owned by <see cref="IPlaybackTrackingService"/>.
/// </summary>
public interface IStreamRepository
{
    Task<Dictionary<Guid, long>> GetPlayCountsByTrackIdsAsync(IEnumerable<Guid> trackIds);
}
