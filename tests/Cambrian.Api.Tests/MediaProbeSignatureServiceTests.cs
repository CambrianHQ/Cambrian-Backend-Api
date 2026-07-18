using Cambrian.Application.Configuration;
using Cambrian.Application.Services;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.Tests;

public sealed class MediaProbeSignatureServiceTests
{
    private const string ProbeSigningKey = "media-probe-signing-key-32-bytes-minimum";

    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 7, 16, 20, 0, 0, TimeSpan.Zero));
    private readonly MediaProbeSignatureService _service;

    public MediaProbeSignatureServiceTests()
    {
        _service = CreateService(ProbeSigningKey, _clock);
    }

    [Fact]
    public void Signature_RoundTripsForTheSignedTrackAndStaysOpaque()
    {
        var trackId = Guid.NewGuid();

        var signature = _service.Create(trackId);

        Assert.True(_service.Validate(signature, trackId));
        Assert.DoesNotContain(trackId.ToString("D"), signature);
    }

    [Fact]
    public void TamperedSignatureIsRejected()
    {
        var trackId = Guid.NewGuid();
        var signature = _service.Create(trackId);
        var tampered = signature[..^1] + (signature[^1] == 'A' ? 'B' : 'A');

        Assert.False(_service.Validate(tampered, trackId));
    }

    [Fact]
    public void SignatureForOneTrackDoesNotValidateForAnotherTrack()
    {
        var signature = _service.Create(Guid.NewGuid());

        Assert.False(_service.Validate(signature, Guid.NewGuid()));
    }

    [Fact]
    public void SignatureExpiresOutsideTheSixtySecondWindowButValidatesRepeatedlyInsideIt()
    {
        var trackId = Guid.NewGuid();
        var signature = _service.Create(trackId);

        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(_service.Validate(signature, trackId));
        Assert.True(_service.Validate(signature, trackId));

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(_service.Validate(signature, trackId));
    }

    [Fact]
    public void SignatureTimestampedBeyondClockSkewInTheFutureIsRejected()
    {
        var trackId = Guid.NewGuid();
        var signature = _service.Create(trackId);

        _clock.Advance(TimeSpan.FromSeconds(-61));

        Assert.False(_service.Validate(signature, trackId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-dot-separator")]
    [InlineData("notanumber.c2lnbmF0dXJl")]
    public void MissingOrMalformedSignaturesAreRejected(string? signature)
    {
        Assert.False(_service.Validate(signature, Guid.NewGuid()));
    }

    [Fact]
    public void CreateThrowsWhenTheSigningKeyIsMissing()
    {
        var unconfigured = CreateService(signingKey: null, _clock);

        Assert.Throws<InvalidOperationException>(() => unconfigured.Create(Guid.NewGuid()));
    }

    [Fact]
    public void CreateThrowsWhenTheSigningKeyIsShorterThanThirtyTwoBytes()
    {
        var weak = CreateService("short-key", _clock);

        Assert.Throws<InvalidOperationException>(() => weak.Create(Guid.NewGuid()));
    }

    [Fact]
    public void ValidateFailsClosedWhenTheSigningKeyIsMissingOrTooShort()
    {
        var trackId = Guid.NewGuid();
        var signature = _service.Create(trackId);

        Assert.False(CreateService(signingKey: null, _clock).Validate(signature, trackId));
        Assert.False(CreateService("short-key", _clock).Validate(signature, trackId));
    }

    [Fact]
    public void SignatureSignedWithTheTicketKeyDoesNotValidateAsAProbeSignature()
    {
        // The probe signing key and the playback ticket signing key are separate
        // secrets; material signed under one must never verify under the other.
        var trackId = Guid.NewGuid();
        var ticketKeyedSigner = CreateService("test-playback-signing-key-32-bytes-minimum", _clock);

        var crossSigned = ticketKeyedSigner.Create(trackId);

        Assert.True(ticketKeyedSigner.Validate(crossSigned, trackId));
        Assert.False(_service.Validate(crossSigned, trackId));
    }

    private static MediaProbeSignatureService CreateService(string? signingKey, TimeProvider clock) =>
        new(Options.Create(new PlaybackMediaOptions { ProductionProbeSigningKey = signingKey }), clock);

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
