namespace Cambrian.Application.Interfaces;

/// <summary>
/// Issues and verifies the free, instant provenance "stamp": an asymmetric signature over a
/// track's (contentHash, signedAt). Asymmetric so anyone can verify with the public key without
/// the secret. The canonical preimage is
/// <c>cambrian-prov-v1|{contentHash}|{unixSeconds}</c> (signedAt truncated to whole seconds so it
/// reconstructs unambiguously from the returned ISO timestamp).
/// </summary>
public interface IProvenanceSigner
{
    /// <summary>Signature algorithm label (e.g. <c>ECDSA-P256-SHA256</c>).</summary>
    string Algorithm { get; }

    /// <summary>Stable identifier of the current public key (hex fingerprint of the SPKI).</summary>
    string KeyId { get; }

    /// <summary>
    /// Sign the stamp. The returned <see cref="ProvenanceStamp.SignedAt"/> is truncated to whole
    /// seconds and is the exact value the signature covers — persist and return it verbatim.
    /// </summary>
    ProvenanceStamp Sign(string contentHash, DateTime signedAtUtc);

    /// <summary>Verify a stamp against the current public key. Returns false on any malformed input.</summary>
    bool Verify(string contentHash, DateTime signedAtUtc, string signatureBase64);

    /// <summary>The current public key as SubjectPublicKeyInfo PEM, for independent verification.</summary>
    string GetPublicKeyPem();
}

/// <summary>A signed provenance stamp.</summary>
/// <param name="Signature">Base64 signature (IEEE-P1363 for ECDSA, Web Crypto-compatible).</param>
/// <param name="SignedAt">UTC time covered by the signature (truncated to whole seconds).</param>
/// <param name="Algorithm">Signature algorithm label.</param>
/// <param name="KeyId">Fingerprint of the signing public key.</param>
public sealed record ProvenanceStamp(string Signature, DateTime SignedAt, string Algorithm, string KeyId);
