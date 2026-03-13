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
            Restrictions = restrictions,
            CopyrightOwner = licenseType == "copyright_buyout" ? buyerId : creatorId
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
        LicenseType = cert.LicenseType,
        BuyerId = cert.BuyerId,
        CreatorId = cert.CreatorId,
        UsageType = cert.UsageType,
        IssuedAt = cert.IssuedAt,
        AllowedUses = cert.AllowedUses,
        Restrictions = cert.Restrictions,
        CopyrightOwner = cert.CopyrightOwner
    };

    /// <summary>
    /// Derive default allowed uses and restrictions based on license type.
    /// </summary>
    private static (List<string>? AllowedUses, List<string>? Restrictions) ResolveTerms(string licenseType)
    {
        return licenseType switch
        {
            "standard" => (
                new List<string>
                {
                    "Personal listening",
                    "Non-commercial projects",
                    "Private use only"
                },
                new List<string>
                {
                    "No redistribution",
                    "No commercial use",
                    "No public performance"
                }
            ),
            "non-exclusive" => (
                new List<string>
                {
                    "Commercial use in media projects",
                    "YouTube / video content",
                    "Podcasts and streaming",
                    "Advertising and social media",
                    "Games and interactive media",
                    "Film and TV"
                },
                new List<string>
                {
                    "Credit to original creator required",
                    "No resale of license",
                    "No redistribution of raw audio",
                    "Track remains available on marketplace"
                }
            ),
            "exclusive" => (
                new List<string>
                {
                    "Exclusive commercial use",
                    "Perpetual license",
                    "Unlimited projects",
                    "Global distribution",
                    "Editing and modification",
                    "Monetization rights"
                },
                new List<string>
                {
                    "No resale of standalone track",
                    "No sublicensing",
                    "No redistribution of raw audio"
                }
            ),
            "copyright_buyout" => (
                new List<string>
                {
                    "Full copyright ownership transfer",
                    "Perpetual and irrevocable rights",
                    "Unlimited commercial use",
                    "Global distribution",
                    "Editing, remixing, and modification",
                    "Monetization and sublicensing rights",
                    "Right to register with PROs and content ID systems"
                },
                new List<string>
                {
                    "Original creator relinquishes all ownership rights",
                    "Track permanently removed from Cambrian marketplace",
                    "No further licensing by original creator permitted"
                }
            ),
            _ => (null, null)
        };
    }
}
