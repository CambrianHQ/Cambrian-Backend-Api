namespace Cambrian.Application.Exceptions;

public sealed class TrackLyricsConcurrencyException : Exception
{
    public TrackLyricsConcurrencyException(Guid trackId)
        : base($"Lyrics for track {trackId} changed after this editor was loaded.")
    {
        TrackId = trackId;
    }

    public Guid TrackId { get; }
}
