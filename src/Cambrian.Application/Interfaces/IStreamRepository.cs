using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IStreamRepository
{
    Task<StreamSession> StartAsync(Guid trackId, string? userId);

    Task StopAsync(Guid sessionId);

    Task<StreamSession?> GetByIdAsync(Guid id);
}
