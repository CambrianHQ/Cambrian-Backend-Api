using Cambrian.Application.DTOs.Health;

namespace Cambrian.Application.Interfaces;

public interface IHealthService
{
    Task<HealthStatusResponse> GetStatusAsync();
}
