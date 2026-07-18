using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cambrian.Application.Configuration;
using Cambrian.Application.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.Tests;

public sealed class PlaybackTicketServiceTests
{
    private const string SigningKey = "test-playback-signing-key-32-bytes-minimum";

    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 7, 16, 20, 0, 0, TimeSpan.Zero));
    private readonly PlaybackTicketService _service;

    public PlaybackTicketServiceTests()
    {
        _service = CreateService(SigningKey);
    }

    [Fact]
    public void Ticket_IsSignedScopedTrackBoundAndCarriesPrivateGrant()
    {
        var trackId = Guid.NewGuid();
        var issued = _service.Issue(trackId, "owner-1");

        var valid = _service.Validate(issued.Ticket, trackId);

        Assert.True(valid.IsValid);
        Assert.Equal(trackId, valid.TrackId);
        Assert.Equal("owner-1", valid.AuthorizedUserId);
        Assert.Equal(issued.TicketId, valid.TicketId);
        Assert.Equal(issued.ExpiresAtUtc, valid.ExpiresAtUtc);
        Assert.DoesNotContain("owner-1", issued.Ticket);
    }

    [Fact]
    public void TamperedAndWrongTrackTicketsFailClosed()
    {
        var issued = _service.Issue(Guid.NewGuid());
        var tampered = issued.Ticket[..^1] + (issued.Ticket[^1] == 'A' ? "B" : "A");

        Assert.Equal("ticket_invalid", _service.Validate(tampered, Guid.NewGuid()).FailureCode);
        Assert.Equal("ticket_track_mismatch", _service.Validate(issued.Ticket, Guid.NewGuid()).FailureCode);
    }

    [Fact]
    public void ExpiredTicketFailsButTicketSupportsRepeatedValidationBeforeExpiry()
    {
        var trackId = Guid.NewGuid();
        var issued = _service.Issue(trackId);
        Assert.True(_service.Validate(issued.Ticket, trackId).IsValid);
        Assert.True(_service.Validate(issued.Ticket, trackId).IsValid);

        _clock.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal("ticket_expired", _service.Validate(issued.Ticket, trackId).FailureCode);
    }

    [Fact]
    public void TicketSignedWithDifferentKeyFailsClosed()
    {
        var trackId = Guid.NewGuid();
        var foreign = CreateService("another-playback-signing-key-32-bytes-min");
        var issued = foreign.Issue(trackId);

        Assert.Equal("ticket_invalid", _service.Validate(issued.Ticket, trackId).FailureCode);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("a.b")]
    public void StructurallyInvalidTicketsFailClosed(string ticket) =>
        Assert.Equal("ticket_invalid", _service.Validate(ticket, Guid.NewGuid()).FailureCode);

    [Fact]
    public void CorrectlySignedNonJsonPayloadFailsClosed()
    {
        // The signature is genuine, so validation reaches the payload parse —
        // a non-JSON payload must still fail closed instead of throwing.
        var encodedPayload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("not-a-json-payload"));
        var ticket = $"{encodedPayload}.{SignPayload(encodedPayload, SigningKey)}";

        Assert.Equal("ticket_invalid", _service.Validate(ticket, Guid.NewGuid()).FailureCode);
    }

    [Fact]
    public void DownloadScopedTicketIsRejectedForPlayback()
    {
        var trackId = Guid.NewGuid();
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var ticket = CraftTicket(new CraftedTicketPayload(
            trackId, "download", now, now + 900, "ticket-1", null), SigningKey);

        Assert.Equal("ticket_scope_invalid", _service.Validate(ticket, trackId).FailureCode);
    }

    [Fact]
    public void FutureIssuedTicketIsRejected()
    {
        var trackId = Guid.NewGuid();
        var issued = _service.Issue(trackId);

        // Roll the validator's clock backwards so the ticket's IssuedAt sits more
        // than 60 seconds in its future — a drifted or forged issuance timestamp.
        _clock.Advance(TimeSpan.FromMinutes(-2));

        Assert.Equal("ticket_invalid", _service.Validate(issued.Ticket, trackId).FailureCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short-key")]
    public void MisconfiguredSigningKeyThrowsOnIssueButFailsClosedOnValidate(string? signingKey)
    {
        var trackId = Guid.NewGuid();
        var wellFormed = _service.Issue(trackId).Ticket;
        var misconfigured = CreateService(signingKey);

        Assert.Throws<InvalidOperationException>(() => misconfigured.Issue(trackId));
        Assert.Equal("ticket_invalid", misconfigured.Validate(wellFormed, trackId).FailureCode);
    }

    private PlaybackTicketService CreateService(string? signingKey) =>
        new(Options.Create(new PlaybackMediaOptions
        {
            TicketSigningKey = signingKey,
            TicketLifetimeMinutes = 15,
        }), _clock);

    // Mirrors PlaybackTicketService's issuance: JSON payload, base64url encoding,
    // and HMACSHA256 over the ASCII bytes of the encoded payload.
    private static string CraftTicket(CraftedTicketPayload payload, string signingKey)
    {
        var encodedPayload = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{encodedPayload}.{SignPayload(encodedPayload, signingKey)}";
    }

    private static string SignPayload(string encodedPayload, string signingKey) =>
        WebEncoders.Base64UrlEncode(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingKey), Encoding.ASCII.GetBytes(encodedPayload)));

    // Property names must match the service's private TicketPayload record exactly —
    // the service deserializes with default (case-sensitive) JSON options.
    private sealed record CraftedTicketPayload(
        Guid TrackId,
        string Scope,
        long IssuedAt,
        long ExpiresAt,
        string TicketId,
        string? AuthorizedUserId);

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
