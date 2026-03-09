using Cambrian.Application.DTOs.Admin;

namespace Cambrian.Application.Interfaces;

public interface IMarketplaceIntegrityService
{
    Task<IntegrityReport> RunAuditAsync();
}
