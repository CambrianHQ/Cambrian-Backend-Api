using Cambrian.Application.DTOs.Provenance;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// §9 legitimacy surface for a single track: provenance, authorship documentation,
/// and a compliance score. Plan-gated through <see cref="IPlanEntitlementService"/>
/// (under-entitled → 402 <c>UPGRADE_REQUIRED</c>):
/// <list type="bullet">
///   <item>provenance + compliance-score <b>read</b> — Free+ (<c>provenanceStamp</c> / <c>complianceScoreRead</c>).</item>
///   <item>authorship workflow + creator-detail — Creator+ (<c>fullProvenanceSuite</c>).</item>
/// </list>
/// Track resolution, ownership, and 404/403 mapping live in
/// <see cref="ITrackLegitimacyService"/>; this controller only gates and shapes responses.
/// The route id accepts a track GUID or a <c>CAMB-TRK-*</c> human id.
/// </summary>
[ApiController]
[Authorize]
[Route("api/tracks/{id}")]
public sealed class TrackProvenanceController : BaseController
{
    private const string FlagProvenanceRead = "provenanceStamp";
    private const string FlagComplianceRead = "complianceScoreRead";
    private const string FlagAuthorshipSuite = "fullProvenanceSuite";

    private readonly IPlanEntitlementService _plans;
    private readonly ITrackLegitimacyService _legitimacy;

    public TrackProvenanceController(IPlanEntitlementService plans, ITrackLegitimacyService legitimacy)
    {
        _plans = plans;
        _legitimacy = legitimacy;
    }

    // ───── GET /api/tracks/{id}/provenance (Free+) ─────
    [HttpGet("provenance")]
    public async Task<IActionResult> GetProvenance(string id, CancellationToken ct)
    {
        await _plans.RequireFeatureAsync(GetRequiredUserId()!, FlagProvenanceRead, ct);
        return OkResponse(await _legitimacy.GetProvenanceAsync(id, ct));
    }

    // ───── GET /api/tracks/{id}/compliance-score (Free+) ─────
    [HttpGet("compliance-score")]
    public async Task<IActionResult> GetComplianceScore(string id, CancellationToken ct)
    {
        await _plans.RequireFeatureAsync(GetRequiredUserId()!, FlagComplianceRead, ct);
        return OkResponse(await _legitimacy.GetComplianceScoreAsync(id, ct));
    }

    // ───── GET /api/tracks/{id}/authorship (Creator+, owner) ─────
    [HttpGet("authorship")]
    public async Task<IActionResult> GetAuthorship(string id, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        await _plans.RequireFeatureAsync(userId, FlagAuthorshipSuite, ct);
        return OkResponse(await _legitimacy.GetAuthorshipAsync(id, userId, User.IsInRole("Admin"), ct));
    }

    // ───── POST /api/tracks/{id}/authorship (Creator+, owner, upsert) ─────
    [HttpPost("authorship")]
    public async Task<IActionResult> UpsertAuthorship(string id, [FromBody] TrackAuthorshipRequest request, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        await _plans.RequireFeatureAsync(userId, FlagAuthorshipSuite, ct);
        var result = await _legitimacy.UpsertAuthorshipAsync(id, userId, User.IsInRole("Admin"), request, ct);
        return OkResponse(result, "Authorship saved.");
    }

    // ───── GET /api/tracks/{id}/creator-detail (Creator+, owner) ─────
    [HttpGet("creator-detail")]
    public async Task<IActionResult> GetCreatorDetail(string id, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        await _plans.RequireFeatureAsync(userId, FlagAuthorshipSuite, ct);
        return OkResponse(await _legitimacy.GetCreatorDetailAsync(id, userId, User.IsInRole("Admin"), ct));
    }
}
