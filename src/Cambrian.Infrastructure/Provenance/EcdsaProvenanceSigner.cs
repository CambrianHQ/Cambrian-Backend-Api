using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Infrastructure.Provenance;

/// <summary>
/// ECDSA P-256 / SHA-256 implementation of <see cref="IProvenanceSigner"/>. Signatures are
/// IEEE-P1363 (r‖s) — the format .NET's <c>SignData</c> emits and the format the browser
/// Web Crypto API expects — so the frontend can verify stamps with no extra library.
///
/// <para>The private key is loaded from <c>Provenance:Signing:PrivateKeyPem</c> (PKCS#8/SEC1 PEM).
/// When absent, an ephemeral key is generated for the process (dev/test only) and a warning is
/// logged; the Production guard that requires a configured key lives in the API startup.</para>
/// </summary>
public sealed class EcdsaProvenanceSigner : IProvenanceSigner
{
    private const string Preamble = "cambrian-prov-v1";

    private readonly string _privateKeyPem;
    private readonly string _publicKeyPem;

    public string Algorithm => "ECDSA-P256-SHA256";
    public string KeyId { get; }

    public EcdsaProvenanceSigner(IConfiguration config, ILogger<EcdsaProvenanceSigner> logger)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var configuredPem = config["Provenance:Signing:PrivateKeyPem"];
        if (!string.IsNullOrWhiteSpace(configuredPem))
        {
            ecdsa.ImportFromPem(configuredPem);
        }
        else
        {
            logger.LogWarning(
                "Provenance:Signing:PrivateKeyPem is not configured — generating an EPHEMERAL provenance "
                + "signing key for this process. Stamps will not verify across restarts. Configure a stable key.");
        }

        _privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        _publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();
        KeyId = ComputeKeyId(ecdsa.ExportSubjectPublicKeyInfo());

        logger.LogInformation("EVENT: ProvenanceSignerReady algorithm:{Algorithm} keyId:{KeyId}", Algorithm, KeyId);
    }

    public ProvenanceStamp Sign(string contentHash, DateTime signedAtUtc)
    {
        var signedAt = Truncate(signedAtUtc);
        var preimage = Preimage(contentHash, signedAt);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(_privateKeyPem);
        var sig = ecdsa.SignData(preimage, HashAlgorithmName.SHA256);

        return new ProvenanceStamp(Convert.ToBase64String(sig), signedAt, Algorithm, KeyId);
    }

    public bool Verify(string contentHash, DateTime signedAtUtc, string signatureBase64)
    {
        if (string.IsNullOrWhiteSpace(contentHash) || string.IsNullOrWhiteSpace(signatureBase64))
            return false;

        byte[] sig;
        try { sig = Convert.FromBase64String(signatureBase64); }
        catch (FormatException) { return false; }

        var preimage = Preimage(contentHash, Truncate(signedAtUtc));
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(_publicKeyPem);
        return ecdsa.VerifyData(preimage, sig, HashAlgorithmName.SHA256);
    }

    public string GetPublicKeyPem() => _publicKeyPem;

    private static DateTime Truncate(DateTime utc)
    {
        var asUtc = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return new DateTime(asUtc.Ticks - (asUtc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private static byte[] Preimage(string contentHash, DateTime signedAtSeconds)
    {
        var unixSeconds = new DateTimeOffset(signedAtSeconds).ToUnixTimeSeconds();
        return Encoding.UTF8.GetBytes($"{Preamble}|{contentHash}|{unixSeconds}");
    }

    private static string ComputeKeyId(byte[] spki)
    {
        var digest = SHA256.HashData(spki);
        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }
}
