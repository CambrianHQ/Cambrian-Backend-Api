using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cambrian.Application.DTOs.Authorship;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <inheritdoc cref="IAuthorshipRecordService" />
public sealed class AuthorshipRecordService : IAuthorshipRecordService
{
    private const string RecordVersion = "cambrian-authorship-v1";
    private const int DefaultPriceCents = 2900; // $29 launch price

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IAuthorshipRecordRepository _records;
    private readonly ITrackRepository _tracks;
    private readonly IProvenanceAnchorRepository _anchors;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IPaymentGateway _gateway;
    private readonly IObjectStorage _storage;
    private readonly IProvenanceSigner _signer;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthorshipRecordService> _logger;

    public AuthorshipRecordService(
        IAuthorshipRecordRepository records,
        ITrackRepository tracks,
        IProvenanceAnchorRepository anchors,
        UserManager<ApplicationUser> users,
        IPaymentGateway gateway,
        IObjectStorage storage,
        IProvenanceSigner signer,
        IConfiguration config,
        ILogger<AuthorshipRecordService> logger)
    {
        _records = records;
        _tracks = tracks;
        _anchors = anchors;
        _users = users;
        _gateway = gateway;
        _storage = storage;
        _signer = signer;
        _config = config;
        _logger = logger;
    }

    public async Task<CreateAuthorshipRecordResponse> CreateAsync(
        Guid trackId, string userId, CreateAuthorshipRecordRequest request, CancellationToken ct = default)
    {
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null || !string.Equals(track.CreatorId, userId, StringComparison.Ordinal))
            throw new KeyNotFoundException("Release not found.");

        var user = await _users.FindByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        var record = new AuthorshipRecord
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            CreatorId = userId,
            ArtistName = user.DisplayName ?? user.UserName ?? "Unknown Artist",
            Status = "pending_payment",
            EvidenceJson = JsonSerializer.Serialize(request, JsonOpts),
            CreatedAt = DateTime.UtcNow,
        };
        await _records.AddAsync(record, ct);

        var frontendUrl = (_config["App:FrontendUrl"] ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(frontendUrl))
            throw new InvalidOperationException("App:FrontendUrl must be configured for authorship checkout.");

        var priceCents = _config.GetValue<int?>("AuthorshipRecord:PriceCents") ?? DefaultPriceCents;
        var checkoutUrl = await _gateway.CreateCheckoutSessionAsync(
            priceCents,
            "Cambrian Authorship Record",
            clientReferenceId: $"{userId}:authorship:{record.Id}",
            successUrl: $"{frontendUrl}/authorship-records/{record.Id}?paid=true",
            cancelUrl: $"{frontendUrl}/authorship-records/{record.Id}?cancelled=true",
            customerEmail: user.Email);

        _logger.LogInformation(
            "EVENT: AuthorshipRecordCreated recordId:{RecordId} trackId:{TrackId} userId:{UserId} evidenceFiles:{Files}",
            record.Id, trackId, userId, request.Evidence.Count);

        return new CreateAuthorshipRecordResponse { RecordId = record.Id, CheckoutUrl = checkoutUrl };
    }

    /// <inheritdoc />
    public async Task IssueForSessionAsync(Guid recordId, string stripeSessionId, CancellationToken ct = default)
    {
        var record = await _records.GetAsync(recordId, ct);
        if (record is null)
        {
            _logger.LogError(
                "[DEAD-LETTER] Authorship payment for unknown record {RecordId} (session {SessionId}).",
                recordId, stripeSessionId);
            throw new KeyNotFoundException(
                $"Authorship payment cannot be fulfilled because record {recordId} does not exist.");
        }

        // Idempotent: webhook retries and duplicate sessions are no-ops.
        if (record.Status == "issued")
            return;

        var evidence = ParseEvidence(record.EvidenceJson);

        // SHA-256 manifest over every referenced evidence file (canonical order: by key).
        var manifest = new List<object>();
        var manifestEntries = new List<(string Key, string Sha256)>();
        foreach (var file in evidence.Evidence.OrderBy(e => e.FileKey, StringComparer.Ordinal))
        {
            var hash = await HashEvidenceFileAsync(file.FileKey, ct);
            manifestEntries.Add((file.FileKey, hash));
            manifest.Add(new { key = file.FileKey, sha256 = hash, description = file.Description });
        }

        var manifestJson = JsonSerializer.Serialize(manifest, JsonOpts);
        var manifestSha256 = Sha256Hex(manifestJson);

        var issuedAt = TruncateToSeconds(DateTime.UtcNow);
        var canonical = new
        {
            version = RecordVersion,
            recordId = record.Id,
            trackId = record.TrackId,
            artistName = record.ArtistName,
            declarations = evidence.Declarations,
            narrative = evidence.Narrative,
            generator = evidence.Generator,
            evidenceManifest = manifestEntries.Select(e => new { key = e.Key, sha256 = e.Sha256 }),
            manifestSha256,
            issuedAtUnix = new DateTimeOffset(issuedAt).ToUnixTimeSeconds(),
        };
        var canonicalJson = JsonSerializer.Serialize(canonical, JsonOpts);
        var recordHash = Sha256Hex(canonicalJson);

        // Server-timestamped signed attestation with the platform provenance key.
        var stamp = _signer.Sign(recordHash, issuedAt);

        record.ManifestJson = manifestJson;
        record.CanonicalRecordJson = canonicalJson;
        record.RecordHash = recordHash;
        record.Signature = stamp.Signature;
        record.SignatureAlgorithm = stamp.Algorithm;
        record.KeyId = stamp.KeyId;
        record.StripeSessionId = stripeSessionId;
        record.IssuedAt = stamp.SignedAt;
        record.Status = "issued";
        await _records.UpdateAsync(record, ct);

        _logger.LogInformation(
            "EVENT: AuthorshipRecordIssued recordId:{RecordId} keyId:{KeyId} files:{Files}",
            record.Id, stamp.KeyId, manifestEntries.Count);
    }

    public async Task<AuthorshipRecordResponse?> GetAsync(Guid recordId, string userId, CancellationToken ct = default)
    {
        var record = await _records.GetForOwnerAsync(recordId, userId, ct);
        if (record is null)
            return null;

        return new AuthorshipRecordResponse
        {
            Id = record.Id,
            TrackId = record.TrackId,
            Status = record.Status,
            CreatedAt = record.CreatedAt,
            IssuedAt = record.IssuedAt,
            Certificate = record.Status == "issued" ? BuildCertificate(record) : null,
        };
    }

    public async Task<AuthorshipCertificate?> GetPublicCertificateAsync(Guid recordId, CancellationToken ct = default)
    {
        var record = await _records.GetAsync(recordId, ct);
        return record is { Status: "issued" } ? BuildCertificate(record) : null;
    }

    public async Task<AuthorshipCertificateDocument?> GetCertificateDocumentForOwnerAsync(
        Guid recordId, string userId, CancellationToken ct = default)
    {
        var record = await _records.GetForOwnerAsync(recordId, userId, ct);
        if (record is not { Status: "issued" })
            return null;

        return await BuildCertificateDocumentAsync(record, ct);
    }

    public async Task<AuthorshipHashVerificationResponse?> VerifyByHashAsync(string hash, CancellationToken ct = default)
    {
        var normalizedHash = (hash ?? "").Trim().ToLowerInvariant();
        if (normalizedHash.Length != 64 || normalizedHash.Any(c => !Uri.IsHexDigit(c)))
            return null;

        var record = await _records.GetByHashAsync(normalizedHash, ct);
        if (record is null)
            return null;

        var track = await _tracks.GetByIdAsync(record.TrackId);
        var anchor = await _anchors.GetByTrackIdAsync(record.TrackId, ct);

        return new AuthorshipHashVerificationResponse
        {
            Found = true,
            TrackTitle = track?.Title ?? "Unknown Track",
            CreatorName = record.ArtistName,
            CreatedAt = DateTime.SpecifyKind(record.CreatedAt, DateTimeKind.Utc),
            ChainAnchor = ResolveChainAnchor(record, anchor),
            RecordUrl = BuildVerificationQrUrl(normalizedHash),
        };
    }

    // ── Helpers ──

    private async Task<AuthorshipCertificateDocument> BuildCertificateDocumentAsync(
        AuthorshipRecord record, CancellationToken ct)
    {
        var track = await _tracks.GetByIdAsync(record.TrackId);
        var anchor = await _anchors.GetByTrackIdAsync(record.TrackId, ct);
        var recordHash = record.RecordHash ?? "";

        return new AuthorshipCertificateDocument
        {
            RecordId = record.Id,
            TrackId = record.TrackId,
            TrackTitle = track?.Title ?? "Unknown Track",
            TrackCode = track?.CambrianTrackId ?? record.TrackId.ToString(),
            CreatorName = record.ArtistName,
            RecordHash = recordHash,
            Signature = record.Signature ?? "",
            Algorithm = record.SignatureAlgorithm ?? "",
            KeyId = record.KeyId ?? "",
            ChainAnchor = ResolveChainAnchor(record, anchor),
            AuthorshipSummary = BuildAuthorshipSummary(record),
            CreatedAt = DateTime.SpecifyKind(record.CreatedAt, DateTimeKind.Utc),
            IssuedAt = DateTime.SpecifyKind(record.IssuedAt ?? record.CreatedAt, DateTimeKind.Utc),
            VerificationDisplayUrl = BuildVerificationDisplayUrl(recordHash),
            VerificationQrUrl = BuildVerificationQrUrl(recordHash),
        };
    }

    private AuthorshipCertificate BuildCertificate(AuthorshipRecord record) => new()
    {
        RecordId = record.Id,
        ArtistName = record.ArtistName,
        CanonicalRecord = record.CanonicalRecordJson ?? "",
        RecordHash = record.RecordHash ?? "",
        Signature = record.Signature ?? "",
        Algorithm = record.SignatureAlgorithm ?? "",
        KeyId = record.KeyId ?? "",
        PublicKeyPem = _signer.GetPublicKeyPem(),
        // Stored as UTC; SQLite (tests) loses the DateTimeKind on the round-trip,
        // which would shift the signed timestamp during verification.
        IssuedAt = DateTime.SpecifyKind(record.IssuedAt ?? default, DateTimeKind.Utc),
        VerificationInstructions =
            "1. Compute SHA-256 of the canonicalRecord string; it must equal recordHash. "
            + "2. Build the preimage 'cambrian-prov-v1|{recordHash}|{issuedAtUnixSeconds}' (UTF-8). "
            + $"3. Verify the base64 signature over the preimage with the published public key ({record.SignatureAlgorithm}, IEEE-P1363). "
            + "4. Each evidence file's SHA-256 must match its evidenceManifest entry.",
    };

    private async Task<string> HashEvidenceFileAsync(string fileKey, CancellationToken ct)
    {
        using var file = await _storage.OpenReadAsync(fileKey)
            ?? throw new InvalidOperationException($"Evidence file '{fileKey}' could not be read.");
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(file.Stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static CreateAuthorshipRecordRequest ParseEvidence(string evidenceJson)
    {
        try
        {
            return JsonSerializer.Deserialize<CreateAuthorshipRecordRequest>(evidenceJson, JsonOpts) ?? new();
        }
        catch (JsonException)
        {
            return new CreateAuthorshipRecordRequest();
        }
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static DateTime TruncateToSeconds(DateTime utc) =>
        new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private static string BuildAuthorshipSummary(AuthorshipRecord record)
    {
        var evidence = ParseEvidence(record.EvidenceJson);
        var sections = new List<string>();

        if (evidence.Declarations.Count > 0)
            sections.Add("Declared contribution: " + string.Join(" ", evidence.Declarations));

        if (!string.IsNullOrWhiteSpace(evidence.Narrative))
            sections.Add("Creation narrative: " + evidence.Narrative.Trim());

        if (evidence.Generator is { } generator)
        {
            var tool = string.Join(" ", new[] { generator.Tool, generator.Version }.Where(v => !string.IsNullOrWhiteSpace(v)));
            var prompts = generator.Prompts.Count > 0
                ? " Prompts: " + string.Join(" | ", generator.Prompts)
                : "";
            if (!string.IsNullOrWhiteSpace(tool) || !string.IsNullOrWhiteSpace(prompts))
                sections.Add($"AI/tooling disclosure: {tool}.{prompts}".Trim());
        }

        return sections.Count == 0
            ? "No additional authorship narrative was supplied for this record."
            : string.Join(Environment.NewLine, sections);
    }

    private static string? ResolveChainAnchor(AuthorshipRecord record, ProvenanceAnchor? anchor)
    {
        if (anchor is { Status: "anchored" } && !string.IsNullOrWhiteSpace(anchor.RootTxRef))
        {
            return string.IsNullOrWhiteSpace(anchor.Chain)
                ? anchor.RootTxRef
                : $"{anchor.Chain}:{anchor.RootTxRef}";
        }

        return string.IsNullOrWhiteSpace(record.KeyId) ? null : $"stamp:{record.KeyId}";
    }

    private string BuildVerificationDisplayUrl(string recordHash)
        => $"cambrianmusic.com/verify/{recordHash}";

    private string BuildVerificationQrUrl(string recordHash)
        => $"https://cambrianmusic.com/verify/{recordHash}";
}
