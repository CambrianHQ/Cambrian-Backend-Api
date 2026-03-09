using Cambrian.Application.DTOs.DataIntegrity;

namespace Cambrian.Application.Interfaces;

public interface IDataIntegrityService
{
    Task<DataIntegrityReport> RunFullAuditAsync();

    Task<List<IntegrityViolation>> CheckPurchaseLibraryConsistencyAsync();

    Task<List<IntegrityViolation>> CheckExclusiveLicensingIntegrityAsync();

    Task<List<IntegrityViolation>> CheckPayoutIntegrityAsync();

    Task<List<IntegrityViolation>> CheckOrphanedPayoutsAsync();

    Task<int> RepairMissingLibraryEntriesAsync();

    Task<int> RepairExclusiveFlagsAsync();
}
