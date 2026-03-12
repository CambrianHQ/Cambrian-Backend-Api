using Cambrian.Application.DTOs.Licenses;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class LicenseService : ILicenseService
{
    private readonly ILicenseCertificateRepository _repo;

    public LicenseService(ILicenseCertificateRepository repo)
    {
        _repo = repo;
    }

    public async Task<LicenseCertificateDto> IssueCertificateAsync(
        Guid purchaseId,
        string cambrianTrackId,
        string buyerId,
        string creatorId,
        string licenseType,
        string? usageType)
    {
        // Prevent duplicate certificates for the same purchase
        var existing = await _repo.GetByPurchaseIdAsync(purchaseId);
        if (existing is not null)
            return MapToDto(existing);

        // Derive allowed uses & restrictions from license type
        var (allowedUses, restrictions) = ResolveTerms(licenseType);

        var cert = new LicenseCertificate
        {
            Id = Guid.NewGuid(),
            TrackId = cambrianTrackId,
            BuyerId = buyerId,
            CreatorId = creatorId,
            PurchaseId = purchaseId,
            LicenseType = licenseType,
            UsageType = usageType ?? "personal",
            IssuedAt = DateTime.UtcNow,
            AllowedUses = allowedUses,
            Restrictions = restrictions
        };

        await _repo.AddAsync(cert);
        return MapToDto(cert);
    }

    public async Task<LicenseCertificateDto?> GetByIdAsync(string licenseId)
    {
        if (!Guid.TryParse(licenseId, out var id))
            return null;

        var cert = await _repo.GetByIdAsync(id);
        return cert is null ? null : MapToDto(cert);
    }

    public async Task<IReadOnlyCollection<LicenseCertificateDto>> GetByBuyerAsync(string userId)
    {
        var certs = await _repo.GetByBuyerIdAsync(userId);
        return certs.Select(MapToDto).ToList();
    }

    private static LicenseCertificateDto MapToDto(LicenseCertificate cert) => new()
    {
        LicenseId = cert.Id.ToString(),
        TrackId = cert.TrackId,
        BuyerId = cert.BuyerId,
        CreatorId = cert.CreatorId,
        UsageType = cert.UsageType,
        IssuedAt = cert.IssuedAt,
        AllowedUses = cert.AllowedUses,
        Restrictions = cert.Restrictions
    };

    /// <summary>
    /// Derive default allowed uses and restrictions based on license type.
    /// </summary>
    private static (List<string>? AllowedUses, List<string>? Restrictions) ResolveTerms(string licenseType)
    {
        return licenseType switch
        {
            "standard" => (
                new List<string> { "personal listening", "non-commercial projects" },
                new List<string> { "no redistribution", "no commercial use" }
            ),
            "non-exclusive" => (
                new List<string> { "youtube", "ads", "podcast", "game", "film", "social", "streaming" },
                new List<string> { "credit required", "no resale of license" }
            ),
            "exclusive" => (
                null, // unrestricted
                new List<string> { "single-owner license" }
            ),
            _ => (null, null)
        };
    }
}
