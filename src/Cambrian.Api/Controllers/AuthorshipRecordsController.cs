using Cambrian.Application.DTOs.Authorship;
using Cambrian.Application.Interfaces;
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

    public AuthorshipRecordsController(IAuthorshipRecordService service)
    {
        _service = service;
    }

    /// <summary>Submit evidence for a release and get a checkout URL for the record fee.</summary>
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
}
