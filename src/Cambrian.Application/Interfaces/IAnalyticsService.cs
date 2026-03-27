using System.Security.Claims;
using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

public interface IAnalyticsService
{
    Task RecordEventAsync(AnalyticsEventRequest request, ClaimsPrincipal user, CancellationToken ct);
}
