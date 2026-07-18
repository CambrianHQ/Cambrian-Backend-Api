using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Cambrian.Application.Services;

public sealed class MediaProbeSignatureService : IMediaProbeSignatureService
{
    private readonly PlaybackMediaOptions _options;
    private readonly TimeProvider _clock;

    public MediaProbeSignatureService(IOptions<PlaybackMediaOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public string Create(Guid trackId)
    {
        var timestamp = _clock.GetUtcNow().ToUnixTimeSeconds();
        var payload = $"{trackId:D}:{timestamp}";
        var signature = HMACSHA256.HashData(GetKey(), Encoding.ASCII.GetBytes(payload));
        return $"{timestamp}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    public bool Validate(string? signature, Guid trackId)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return false;
        var parts = signature.Split('.', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var timestamp))
            return false;
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > 60)
            return false;
        try
        {
            var payload = $"{trackId:D}:{timestamp}";
            var expected = HMACSHA256.HashData(GetKey(), Encoding.ASCII.GetBytes(payload));
            return CryptographicOperations.FixedTimeEquals(expected, WebEncoders.Base64UrlDecode(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }

    private byte[] GetKey()
    {
        if (string.IsNullOrWhiteSpace(_options.ProductionProbeSigningKey))
            throw new InvalidOperationException("Production media probe signing is not configured.");
        var key = Encoding.UTF8.GetBytes(_options.ProductionProbeSigningKey);
        if (key.Length < 32)
            throw new InvalidOperationException("Production media probe signing key must be at least 32 bytes.");
        return key;
    }
}
