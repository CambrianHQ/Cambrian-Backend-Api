using Cambrian.Application.DTOs.Authorship;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Api.Services;
using Cambrian.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Paid authorship records (contract: docs/api-contracts.md): evidence intake →
/// checkout, owner fetch, and the public no-auth verification endpoint.
/// </summary>
[Authorize]
public sealed class AuthorshipRecordsController : BaseController
{
    private readonly IAuthorshipRecordService _service;
    private readonly IAuthorshipCertificatePdfService _certificatePdfs;

    public AuthorshipRecordsController(
        IAuthorshipRecordService service,
        IAuthorshipCertificatePdfService certificatePdfs)
    {
        _service = service;
        _certificatePdfs = certificatePdfs;
    }

    /// <summary>Submit evidence for a release and get a checkout URL for the record fee.</summary>
    [RequireCheckoutEnabled]
    [HttpPost("/api/releases/{id:guid}/authorship-record")]
    public async Task<IActionResult> Create(Guid id, [FromBody] CreateAuthorshipRecordRequest request, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        try
        {
            var result = await _service.CreateAsync(id, userId, request ?? new CreateAuthorshipRecordRequest(), ct);
            return OkResponse(result);
        }
        catch (KeyNotFoundException) { return NotFoundResponse("Release not found."); }
        catch (InvalidOperationException ex) { return ErrorResponse(ex.Message); }
    }

    /// <summary>Owner view of a record: status, and the signed certificate once issued.</summary>
    [HttpGet("/api/authorship-records/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        var record = await _service.GetAsync(id, userId, ct);
        return record is null ? NotFoundResponse("Authorship record not found.") : OkResponse(record);
    }

    /// <summary>Owner-only PDF certificate for issued authorship records. Creator/Pro only.</summary>
    [HttpGet("/api/authorship-records/{id:guid}/certificate.pdf")]
    public async Task<IActionResult> CertificatePdf(Guid id, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        try
        {
            var result = await _certificatePdfs.GetAsync(id, userId, ct);
            if (result is null)
                return NotFoundResponse("Issued authorship record not found.");

            if (result.Length > 0)
                Response.ContentLength = result.Length;

            Response.Headers.ContentDisposition = $"inline; filename=\"cambrian-authorship-{id}.pdf\"";
            Response.Headers["X-Cambrian-Certificate-Cache"] = result.ServedFromStorage ? "hit" : "miss";
            return File(result.Stream, "application/pdf");
        }
        catch (UpgradeRequiredException ex)
        {
            return UpgradeRequiredResponse(ex.Message);
        }
    }

    /// <summary>Public verification — record, signature, public key, and instructions. No auth,
    /// no PII beyond the artist name.</summary>
    [AllowAnonymous]
    [HttpGet("/verify/{recordId:guid}")]
    public async Task<IActionResult> Verify(Guid recordId, CancellationToken ct)
    {
        var certificate = await _service.GetPublicCertificateAsync(recordId, ct);
        return certificate is null
            ? NotFoundResponse("No issued authorship record with this id.")
            : OkResponse(certificate);
    }

    /// <summary>Public verification by record hash for the frontend /verify page.</summary>
    [AllowAnonymous]
    [HttpGet("/api/verify/{hash}")]
    public async Task<IActionResult> VerifyHash(string hash, CancellationToken ct)
    {
        var result = await _service.VerifyByHashAsync(hash, ct);
        return result is null
            ? Ok(new { found = false })
            : Ok(result);
    }
}
