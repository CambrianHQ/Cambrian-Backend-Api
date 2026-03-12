using Cambrian.Application.DTOs.Licenses;

namespace Cambrian.Application.Interfaces;

public interface ILicenseService
{
    /// <summary>
    /// Issue a license certificate for a completed purchase.
    /// </summary>
    Task<LicenseCertificateDto> IssueCertificateAsync(
        Guid purchaseId,
        string cambrianTrackId,
        string buyerId,
        string creatorId,
        string licenseType,
        string? usageType);

    /// <summary>
    /// Retrieve a license certificate by its ID.
    /// </summary>
    Task<LicenseCertificateDto?> GetByIdAsync(string licenseId);

    /// <summary>
    /// Retrieve all licenses owned by a buyer.
    /// </summary>
    Task<IReadOnlyCollection<LicenseCertificateDto>> GetByBuyerAsync(string userId);
}
