using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IAnalyticsRepository
{
    Task RecordAsync(string eventType, string? userId = null, Guid? trackId = null, string? metadata = null);

    Task<IReadOnlyList<AnalyticsEvent>> QueryAsync(string? eventType = null, DateTime? from = null, DateTime? to = null, int limit = 100);

    Task<Dictionary<string, int>> GetCountsByTypeAsync(DateTime? from = null, DateTime? to = null);
}
