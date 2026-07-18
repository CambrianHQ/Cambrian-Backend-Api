using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Cambrian.Application.Services;

public sealed class PlaybackTicketService : IPlaybackTicketService
{
    private const string PlaybackScope = "playback";
    private readonly PlaybackMediaOptions _options;
    private readonly TimeProvider _clock;

    public PlaybackTicketService(IOptions<PlaybackMediaOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public PlaybackTicketIssue Issue(Guid trackId, string? authorizedUserId = null)
    {
        var key = GetSigningKey();
        var issued = _clock.GetUtcNow().UtcDateTime;
        var expires = issued.AddMinutes(_options.TicketLifetimeMinutes);
        var ticketId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var payload = new TicketPayload(
            trackId,
            PlaybackScope,
            new DateTimeOffset(issued).ToUnixTimeSeconds(),
            new DateTimeOffset(expires).ToUnixTimeSeconds(),
            ticketId,
            string.IsNullOrWhiteSpace(authorizedUserId) ? null : authorizedUserId);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var encodedPayload = WebEncoders.Base64UrlEncode(payloadBytes);
        var signature = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(encodedPayload));
        return new PlaybackTicketIssue(
            $"{encodedPayload}.{WebEncoders.Base64UrlEncode(signature)}",
            ticketId,
            issued,
            expires);
    }

    public PlaybackTicketValidation Validate(string? ticket, Guid expectedTrackId)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return Invalid("ticket_missing");

        try
        {
            var parts = ticket.Split('.', 2);
            if (parts.Length != 2)
                return Invalid("ticket_invalid");

            var expectedSignature = HMACSHA256.HashData(
                GetSigningKey(), Encoding.ASCII.GetBytes(parts[0]));
            var suppliedSignature = WebEncoders.Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature))
                return Invalid("ticket_invalid");

            var payload = JsonSerializer.Deserialize<TicketPayload>(WebEncoders.Base64UrlDecode(parts[0]));
            if (payload is null || payload.TrackId != expectedTrackId)
                return Invalid("ticket_track_mismatch");
            if (!string.Equals(payload.Scope, PlaybackScope, StringComparison.Ordinal))
                return Invalid("ticket_scope_invalid");

            var now = _clock.GetUtcNow().ToUnixTimeSeconds();
            if (payload.IssuedAt > now + 60)
                return Invalid("ticket_invalid");
            if (payload.ExpiresAt <= now)
                return Invalid("ticket_expired");
            if (string.IsNullOrWhiteSpace(payload.TicketId))
                return Invalid("ticket_invalid");

            return new PlaybackTicketValidation(
                true,
                null,
                payload.TrackId,
                payload.AuthorizedUserId,
                payload.TicketId,
                DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt).UtcDateTime);
        }
        catch (FormatException)
        {
            return Invalid("ticket_invalid");
        }
        catch (JsonException)
        {
            return Invalid("ticket_invalid");
        }
        catch (CryptographicException)
        {
            return Invalid("ticket_invalid");
        }
        catch (InvalidOperationException)
        {
            // Signing key missing/short — fail closed as an invalid ticket rather
            // than surfacing an unhandled 500 on every ticketed stream request.
            return Invalid("ticket_invalid");
        }
    }

    private byte[] GetSigningKey()
    {
        if (string.IsNullOrWhiteSpace(_options.TicketSigningKey))
            throw new InvalidOperationException("Playback ticket signing is not configured.");
        var key = Encoding.UTF8.GetBytes(_options.TicketSigningKey);
        if (key.Length < 32)
            throw new InvalidOperationException("Playback ticket signing key must be at least 32 bytes.");
        return key;
    }

    private static PlaybackTicketValidation Invalid(string code) =>
        new(false, code, Guid.Empty, null, null, null);

    private sealed record TicketPayload(
        Guid TrackId,
        string Scope,
        long IssuedAt,
        long ExpiresAt,
        string TicketId,
        string? AuthorizedUserId);
}
