using Cambrian.Application.DTOs.Authorship;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Paid authorship records: evidence intake → Stripe checkout → webhook-issued
/// signed attestation (SHA-256 evidence manifest + canonical JSON + platform
/// signature) → owner fetch + public verification.
/// </summary>
public interface IAuthorshipRecordService : IAuthorshipRecordIssuer
{
    /// <summary>
    /// Intake evidence for the caller's track and return a checkout URL. Throws
    /// <see cref="KeyNotFoundException"/> when the track is absent or not owned.
    /// </summary>
    Task<CreateAuthorshipRecordResponse> CreateAsync(
        Guid trackId, string userId, CreateAuthorshipRecordRequest request, CancellationToken ct = default);

    /// <summary>Owner-scoped fetch. Null when absent or not owned.</summary>
    Task<AuthorshipRecordResponse?> GetAsync(Guid recordId, string userId, CancellationToken ct = default);

    /// <summary>Public verification view — issued records only, no PII beyond artist name.</summary>
    Task<AuthorshipCertificate?> GetPublicCertificateAsync(Guid recordId, CancellationToken ct = default);

    /// <summary>Owner-scoped certificate PDF data. Null when absent, not owned, or not issued.</summary>
    Task<AuthorshipCertificateDocument?> GetCertificateDocumentForOwnerAsync(
        Guid recordId, string userId, CancellationToken ct = default);

    /// <summary>Public hash verification view for the frontend /verify page.</summary>
    Task<AuthorshipHashVerificationResponse?> VerifyByHashAsync(string hash, CancellationToken ct = default);
}

/// <summary>
/// Narrow issue hook for the Stripe webhook (kept separate so the webhook service
/// depends on the smallest possible surface).
/// </summary>
public interface IAuthorshipRecordIssuer
{
    /// <summary>
    /// Issue the record after payment: hash all evidence files into a manifest,
    /// freeze the canonical JSON, sign it, persist. Idempotent — an already-issued
    /// record (or a replayed session) is a no-op.
    /// </summary>
    Task IssueForSessionAsync(Guid recordId, string stripeSessionId, CancellationToken ct = default);
}

/// <summary>Data access for <see cref="AuthorshipRecord"/> (repository-pattern governance).</summary>
public interface IAuthorshipRecordRepository
{
    Task<AuthorshipRecord?> GetAsync(Guid id, CancellationToken ct = default);
    Task<AuthorshipRecord?> GetForOwnerAsync(Guid id, string creatorId, CancellationToken ct = default);
    Task<AuthorshipRecord?> GetByHashAsync(string recordHash, CancellationToken ct = default);
    Task AddAsync(AuthorshipRecord record, CancellationToken ct = default);
    Task UpdateAsync(AuthorshipRecord record, CancellationToken ct = default);
}
