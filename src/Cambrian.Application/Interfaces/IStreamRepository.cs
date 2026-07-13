using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IStreamRepository
{
    /// <summary>
    /// Records a play attempt as a durable, idempotent event and — if it qualifies — updates the
    /// TrackStats/CreatorStats projection in the same transaction. <paramref name="clientKey"/> is
    /// an anonymous-listener identifier (e.g. client IP); it is only used, and only ever hashed,
    /// when <paramref name="userId"/> is null. Calling this twice for the same play attempt (a
    /// retry, a duplicate request, the same request landing on two backend replicas) returns the
    /// same durable row both times with <c>IsNewPlay = false</c> the second time, and never
    /// double-counts.
    /// </summary>
    Task<(StreamSession Session, bool IsNewPlay)> StartAsync(Guid trackId, string? userId, string? clientKey);

    Task StopAsync(Guid sessionId);

    Task<StreamSession?> GetByIdAsync(Guid id);
}
