using Cambrian.Application.DTOs.Provenance;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Provenance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Public provenance verification surface. Anonymous so third parties can independently verify a
/// track's signed stamp without an account: fetch the platform public key, then verify any
/// (contentHash, signedAt, signature) stamp against it.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/provenance")]
public sealed class ProvenanceController : BaseController
{
    private readonly IProvenanceSigner _signer;

    public ProvenanceController(IProvenanceSigner signer) => _signer = signer;

    // ───── GET /api/provenance/public-key ─────
    [HttpGet("public-key")]
    public IActionResult PublicKey() => OkResponse(new
    {
        algorithm = _signer.Algorithm,
        keyId = _signer.KeyId,
        publicKeyPem = _signer.GetPublicKeyPem(),
    });

    // ───── POST /api/provenance/verify ─────
    [HttpPost("verify")]
    public IActionResult Verify([FromBody] ProvenanceVerifyRequest request)
    {
        var valid = _signer.Verify(request.ContentHash, request.SignedAt, request.Signature);
        return OkResponse(new { valid });
    }

    // ───── POST /api/provenance/verify-inclusion ─────
    // Verify a content hash is included in a batch Merkle root via its proof. Combine with the
    // on-chain root (read from the anchor's rootTxRef) for fully independent verification.
    [HttpPost("verify-inclusion")]
    public IActionResult VerifyInclusion([FromBody] ProvenanceInclusionRequest request)
    {
        var valid = MerkleTree.VerifyProof(request.ContentHash, request.MerkleProof, request.MerkleRoot);
        return OkResponse(new { valid });
    }
}
