using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.Tests;

/// <summary>
/// End-to-end coverage of GET/HEAD /stream/{id}/audio with playback enforcement ON
/// (PlaybackMedia:ReadinessEnforcementEnabled=true, LegacyPublicStreamEnabled=false),
/// which is the hardened production posture:
///   - ticketless requests are rejected 401 "unauthorized" BEFORE any DB lookup, with
///     no 401/404 differential between existing and nonexistent track ids;
///   - tampered / cross-track / expired tickets fail with ticket_* codes;
///   - valid tickets stream (200 full / 206 ranged / HEAD metadata) through a fake
///     S3-like storage that honors RFC 9110 range semantics against a 32-byte payload;
///   - media readiness is re-checked per request (demotion after issuance -> 409),
///     while a previously validated row parked in Validating keeps streaming.
///
/// Deliberately pinned fake-storage behaviors (documented so nobody "fixes" them into
/// flakes): a malformed ("bytes=abc"), reversed ("bytes=5-2", invalid per RFC 9110
/// §14.1.1), or multi-range ("bytes=0-1,4-5", which S3/R2 also ignore) Range header is
/// IGNORED and served as a 200 full body — only a syntactically valid, out-of-bounds
/// range yields 416 with "bytes */TOTAL".
/// </summary>
public sealed class StreamEnforcementIntegrationTests
    : IClassFixture<StreamEnforcementIntegrationTests.StreamEnforcementFixture>
{
    private readonly StreamEnforcementFixture _fixture;

    public StreamEnforcementIntegrationTests(StreamEnforcementFixture fixture) => _fixture = fixture;

    // ───────────────────────── enforcement gate (pre-lookup) ─────────────────────────

    [Fact]
    public async Task TicketlessGetAndHeadReturn401UnauthorizedWithNoExistenceDifferential()
    {
        var trackId = await SeedPublicReadyTrackAsync();
        var missingId = Guid.NewGuid();
        using var client = _fixture.CreateClient();

        using var existingGet = await client.GetAsync($"/stream/{trackId:D}/audio");
        using var missingGet = await client.GetAsync($"/stream/{missingId:D}/audio");

        // The gate rejects before any DB lookup, so a real id and a random GUID are
        // indistinguishable — no 401/404 oracle for enumerating track ids.
        Assert.Equal(HttpStatusCode.Unauthorized, existingGet.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missingGet.StatusCode);
        Assert.Equal("unauthorized", await ReadErrorCodeAsync(existingGet));
        Assert.Equal("unauthorized", await ReadErrorCodeAsync(missingGet));

        // HEAD hits the same pre-lookup gate. (HEAD responses carry no body, so only
        // the status can be asserted.)
        using var existingHeadRequest = new HttpRequestMessage(HttpMethod.Head, $"/stream/{trackId:D}/audio");
        using var existingHead = await client.SendAsync(existingHeadRequest);
        using var missingHeadRequest = new HttpRequestMessage(HttpMethod.Head, $"/stream/{missingId:D}/audio");
        using var missingHead = await client.SendAsync(missingHeadRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, existingHead.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missingHead.StatusCode);
    }

    // ───────────────────────── ticket validation failures ─────────────────────────

    [Fact]
    public async Task GarbageTamperedAndCrossTrackTicketsAreRejected401WithTicketCodes()
    {
        var trackId = await SeedPublicReadyTrackAsync();
        using var client = _fixture.CreateClient();

        // Structurally not a ticket at all (no payload.signature shape).
        using var garbage = await client.GetAsync($"/stream/{trackId:D}/audio?ticket=this-is-not-a-ticket");
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        Assert.Equal("ticket_invalid", await ReadErrorCodeAsync(garbage));

        // Correctly shaped but with one signature character flipped.
        var tampered = TamperSignature(_fixture.MintTicket(trackId));
        using var tamperedResponse = await client.GetAsync(TicketedPath(trackId, tampered));
        Assert.Equal(HttpStatusCode.Unauthorized, tamperedResponse.StatusCode);
        Assert.Equal("ticket_invalid", await ReadErrorCodeAsync(tamperedResponse));

        // Genuinely signed — but for a DIFFERENT track. The ticket is bound to its
        // track id, so it must not open this one.
        var crossTrack = _fixture.MintTicket(Guid.NewGuid());
        using var crossResponse = await client.GetAsync(TicketedPath(trackId, crossTrack));
        Assert.Equal(HttpStatusCode.Unauthorized, crossResponse.StatusCode);
        Assert.Equal("ticket_track_mismatch", await ReadErrorCodeAsync(crossResponse));
    }

    [Fact]
    public async Task ExpiredTicketReturns401TicketExpired()
    {
        var trackId = await SeedPublicReadyTrackAsync();
        // Signed with the server's real key but on a clock two hours in the past, so
        // the 15-minute lifetime elapsed long ago while the signature stays valid.
        var expired = _fixture.MintTicket(trackId, issuedAtUtc: DateTimeOffset.UtcNow.AddHours(-2));

        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync(TicketedPath(trackId, expired));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("ticket_expired", await ReadErrorCodeAsync(response));
    }

    // ───────────────────────── happy paths with a valid ticket ─────────────────────────

    [Fact]
    public async Task ValidTicketOnPublicReadyTrackServesFull200AndRanged206()
    {
        var trackId = await SeedPublicReadyTrackAsync();
        using var client = _fixture.CreateClient();
        // Real issuance path: anonymous GET /api/v1/tracks/{id}/playback on a public track.
        var path = await _fixture.GetTicketedLocationAsync(client, trackId);

        using (var full = await client.GetAsync(path))
        {
            Assert.True(full.StatusCode == HttpStatusCode.OK,
                $"Expected 200, got {(int)full.StatusCode}: {await full.Content.ReadAsStringAsync()}");
            Assert.Contains("bytes", full.Headers.AcceptRanges);
            AssertPrivateNoStore(full);
            Assert.Equal(StreamEnforcementFixture.AudioPayload.Length, full.Content.Headers.ContentLength);
            Assert.Equal(StreamEnforcementFixture.AudioPayload, await full.Content.ReadAsByteArrayAsync());
        }

        using var rangedRequest = RangedGet(path, "bytes=0-9");
        using var ranged = await client.SendAsync(rangedRequest);

        Assert.True(ranged.StatusCode == HttpStatusCode.PartialContent,
            $"Expected 206, got {(int)ranged.StatusCode}: {await ranged.Content.ReadAsStringAsync()}");
        Assert.Contains("bytes", ranged.Headers.AcceptRanges);
        AssertPrivateNoStore(ranged);
        var contentRange = ranged.Content.Headers.ContentRange;
        Assert.NotNull(contentRange);
        Assert.Equal("bytes", contentRange!.Unit);
        Assert.Equal(0, contentRange.From);
        Assert.Equal(9, contentRange.To);
        Assert.Equal(StreamEnforcementFixture.AudioPayload.Length, contentRange.Length);
        Assert.Equal(10, ranged.Content.Headers.ContentLength);
        Assert.Equal(StreamEnforcementFixture.AudioPayload[..10], await ranged.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ValidTicketHeadReturnsSameMetadataWithEmptyBody()
    {
        var trackId = await SeedPublicReadyTrackAsync();
        using var client = _fixture.CreateClient();
        var path = await _fixture.GetTicketedLocationAsync(client, trackId);

        using var request = new HttpRequestMessage(HttpMethod.Head, path);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        AssertPrivateNoStore(response);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(StreamEnforcementFixture.AudioPayload.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task HiddenTrackTicketIssuedToOwnerStreamsAnonymously()
    {
        var ownerEmail = $"hidden-owner-{Guid.NewGuid():N}@cambrian.test";
        using var ownerClient = await _fixture.CreateAuthenticatedClientAsync(ownerEmail);
        var ownerId = await _fixture.GetUserIdAsync(ownerEmail);
        var trackId = await _fixture.SeedTrackAsync(ownerId, visibility: "hidden");
        await _fixture.SeedMediaAsync(trackId, TrackMediaStates.Ready, DateTime.UtcNow);

        // The owner is entitled to a ticket; the ticket itself then carries the grant.
        // The subsequent audio fetch is anonymous — exactly how an <audio> element
        // (which cannot attach Authorization headers) plays a hidden track.
        var path = await _fixture.GetTicketedLocationAsync(ownerClient, trackId);

        using var anonymous = _fixture.CreateClient();
        using var request = RangedGet(path, "bytes=0-9");
        using var response = await anonymous.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.PartialContent,
            $"Expected 206, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(StreamEnforcementFixture.AudioPayload[..10], await response.Content.ReadAsByteArrayAsync());
    }

    // ───────────────────────── media state re-checks at stream time ─────────────────────────

    [Fact]
    public async Task TicketIssuedBeforeMediaDemotionReturns409TrackNotReady()
    {
        var trackId = await SeedPublicReadyTrackAsync();
        using var client = _fixture.CreateClient();
        var path = await _fixture.GetTicketedLocationAsync(client, trackId);

        // Readiness is re-checked on every audio request, not frozen into the ticket.
        await _fixture.SetMediaStateAsync(trackId, TrackMediaStates.Failed);

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("track_not_ready", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ValidatingMediaWithPriorValidationKeepsStreamingThroughRevalidationWindow()
    {
        var email = $"revalidating-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var trackId = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        // Parked in Validating mid-recheck, but it HAS passed validation before —
        // ticket holders keep streaming for the duration of the revalidation window.
        await _fixture.SeedMediaAsync(trackId, TrackMediaStates.Validating, DateTime.UtcNow.AddMinutes(-5));
        var ticket = _fixture.MintTicket(trackId);

        using var client = _fixture.CreateClient();

        using (var full = await client.GetAsync(TicketedPath(trackId, ticket)))
        {
            Assert.True(full.StatusCode == HttpStatusCode.OK,
                $"Expected 200, got {(int)full.StatusCode}: {await full.Content.ReadAsStringAsync()}");
            Assert.Equal(StreamEnforcementFixture.AudioPayload, await full.Content.ReadAsByteArrayAsync());
        }

        using var request = RangedGet(TicketedPath(trackId, ticket), "bytes=0-9");
        using var ranged = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(StreamEnforcementFixture.AudioPayload[..10], await ranged.Content.ReadAsByteArrayAsync());
    }

    // ───────────────────────── range edge cases through the ticketed path ─────────────────────────

    [Theory]
    [InlineData("bytes=abc")]     // malformed range-spec — ignored per RFC 9110, NOT an error
    [InlineData("bytes=5-2")]     // last-byte-pos < first-byte-pos — invalid spec (RFC 9110 §14.1.1), ignored, NOT 416
    [InlineData("bytes=0-1,4-5")] // multi-range — the fake mirrors S3/R2 and serves the full object
    public async Task UnusableRangeHeadersAreIgnoredAndServeFull200(string rawRange)
    {
        var trackId = await SeedPublicReadyTrackAsync();
        var ticket = _fixture.MintTicket(trackId);
        using var client = _fixture.CreateClient();

        using var request = RangedGet(TicketedPath(trackId, ticket), rawRange);
        using var response = await client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Range '{rawRange}': expected 200 full, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(StreamEnforcementFixture.AudioPayload.Length, response.Content.Headers.ContentLength);
        Assert.Equal(StreamEnforcementFixture.AudioPayload, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SuffixRangeServesLastBytesAs206()
    {
        var trackId = await SeedPublicReadyTrackAsync();
        var ticket = _fixture.MintTicket(trackId);
        using var client = _fixture.CreateClient();

        using var request = RangedGet(TicketedPath(trackId, ticket), "bytes=-4");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var contentRange = response.Content.Headers.ContentRange;
        Assert.NotNull(contentRange);
        Assert.Equal("bytes", contentRange!.Unit);
        Assert.Equal(28, contentRange.From); // 32-byte payload: last 4 are indices 28..31
        Assert.Equal(31, contentRange.To);
        Assert.Equal(StreamEnforcementFixture.AudioPayload.Length, contentRange.Length);
        Assert.Equal(4, response.Content.Headers.ContentLength);
        Assert.Equal(StreamEnforcementFixture.AudioPayload[^4..], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task OpenEndedRangeServesThroughEndAs206()
    {
        var trackId = await SeedPublicReadyTrackAsync();
        var ticket = _fixture.MintTicket(trackId);
        using var client = _fixture.CreateClient();

        using var request = RangedGet(TicketedPath(trackId, ticket), "bytes=2-");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var contentRange = response.Content.Headers.ContentRange;
        Assert.NotNull(contentRange);
        Assert.Equal("bytes", contentRange!.Unit);
        Assert.Equal(2, contentRange.From);
        Assert.Equal(31, contentRange.To);
        Assert.Equal(StreamEnforcementFixture.AudioPayload.Length, contentRange.Length);
        Assert.Equal(30, response.Content.Headers.ContentLength);
        Assert.Equal(StreamEnforcementFixture.AudioPayload[2..], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task BeyondEndRangeReturns416WithUnsatisfiedContentRange()
    {
        var trackId = await SeedPublicReadyTrackAsync();
        var ticket = _fixture.MintTicket(trackId);
        using var client = _fixture.CreateClient();

        using var request = RangedGet(TicketedPath(trackId, ticket), "bytes=999999-");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        // RFC 9110 §14.4: 416 carries "bytes */TOTAL" — no from/to, only the length.
        var contentRange = response.Content.Headers.ContentRange;
        Assert.NotNull(contentRange);
        Assert.Equal("bytes", contentRange!.Unit);
        Assert.Null(contentRange.From);
        Assert.Null(contentRange.To);
        Assert.Equal(StreamEnforcementFixture.AudioPayload.Length, contentRange.Length);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    // ───────────────────────── helpers ─────────────────────────

    private async Task<Guid> SeedPublicReadyTrackAsync()
    {
        var email = $"stream-enforce-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var trackId = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        await _fixture.SeedMediaAsync(trackId, TrackMediaStates.Ready, DateTime.UtcNow);
        return trackId;
    }

    private static string TicketedPath(Guid trackId, string ticket) =>
        $"/stream/{trackId:D}/audio?ticket={Uri.EscapeDataString(ticket)}";

    private static HttpRequestMessage RangedGet(string path, string rawRange)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        // TryAddWithoutValidation lets deliberately malformed values reach the server
        // exactly as a hostile or buggy client would send them.
        Assert.True(request.Headers.TryAddWithoutValidation("Range", rawRange));
        return request;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("error").GetProperty("code").GetString();
    }

    private static void AssertPrivateNoStore(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl!.Private, "Expected Cache-Control: private");
        Assert.True(cacheControl.NoStore, "Expected Cache-Control: no-store");
    }

    private static string TamperSignature(string ticket)
    {
        var dot = ticket.LastIndexOf('.');
        Assert.True(dot > 0, "Ticket must have a payload.signature shape.");
        var signature = ticket[(dot + 1)..].ToCharArray();
        // Flip a MIDDLE base64url character: every bit of a middle char participates in
        // the decoded bytes, so the change is guaranteed to alter the signature (the
        // final char's trailing bits can be ignored by lenient decoders).
        var middle = signature.Length / 2;
        signature[middle] = signature[middle] == 'A' ? 'B' : 'A';
        return ticket[..(dot + 1)] + new string(signature);
    }

    // ───────────────────────── fixture ─────────────────────────

    public sealed class StreamEnforcementFixture : CambrianApiFixture
    {
        // PlaybackTicketService.GetSigningKey requires at least 32 bytes.
        public const string TicketSigningKey = "stream-enforcement-ticket-key-at-least-32-bytes";
        public const string AudioObjectKey = "tracks/enforced-test-beat.mp3";

        // 32 ASCII bytes — every range assertion in this file derives from this payload.
        public static readonly byte[] AudioPayload = Encoding.ASCII.GetBytes("0123456789ABCDEFGHIJKLMNOPQRSTUV");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // UseSetting overrides appsettings.json in this harness (the PlaybackV1
            // fixture proves it: appsettings.json ships BackendRelease "unknown" and
            // its overridden "test-release" is asserted on the wire).
            builder.UseSetting("PlaybackMedia:TicketSigningKey", TicketSigningKey);
            builder.UseSetting("PlaybackMedia:ReadinessEnforcementEnabled", "true");
            builder.UseSetting("PlaybackMedia:LegacyPublicStreamEnabled", "false");
            builder.UseSetting("PlaybackMedia:BackendRelease", "test-release");
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMediaValidationService>();
                services.AddSingleton<IMediaValidationService, EnforcementValidationStub>();
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage, RangeAwareS3LikeStorage>();
            });
        }

        /// <summary>
        /// Seed a TrackMedia row. Unlike the PlaybackV1 fixture, the caller controls
        /// ValidatedAtUtc for ANY state so tests can seed "Validating with a prior
        /// successful validation" directly.
        /// </summary>
        public async Task SeedMediaAsync(Guid trackId, string state, DateTime? validatedAtUtc = null)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.TrackMedia.Add(new TrackMedia
            {
                TrackId = trackId,
                ObjectKey = AudioObjectKey,
                State = state,
                StateChangedAtUtc = DateTime.UtcNow,
                ValidatedAtUtc = validatedAtUtc,
                SizeBytes = AudioPayload.Length,
                ContentType = "audio/mpeg",
                ChecksumSha256 = new string('a', 64),
                DurationMilliseconds = 30_000,
                ValidationVersion = "media-v1",
                ConcurrencyToken = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }

        /// <summary>Flip an existing media row's state (e.g. demote Ready → Failed).</summary>
        public async Task SetMediaStateAsync(Guid trackId, string state)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var media = await db.TrackMedia.SingleAsync(x => x.TrackId == trackId);
            media.State = state;
            media.StateChangedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Mint a ticket with a PlaybackTicketService constructed on the SAME signing
        /// key the server booted with, so it is indistinguishable from a server-issued
        /// one. A fixed TimeProvider lets tests mint already-expired tickets (the test
        /// project does not reference Microsoft.Extensions.Time.Testing).
        /// </summary>
        public string MintTicket(Guid trackId, string? authorizedUserId = null, DateTimeOffset? issuedAtUtc = null)
        {
            var service = new PlaybackTicketService(
                Options.Create(new PlaybackMediaOptions { TicketSigningKey = TicketSigningKey }),
                issuedAtUtc is { } issued ? new FixedTimeProvider(issued) : TimeProvider.System);
            return service.Issue(trackId, authorizedUserId).Ticket;
        }

        /// <summary>
        /// Run the real issuance flow (GET /api/v1/tracks/{id}/playback) with the given
        /// client and return the ticketed /stream/{id}/audio path from "location".
        /// </summary>
        public async Task<string> GetTicketedLocationAsync(HttpClient client, Guid trackId)
        {
            var info = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId:D}/playback");
            var location = new Uri(info.GetProperty("data").GetProperty("location").GetString()!);
            return location.PathAndQuery;
        }
    }

    // ───────────────────────── stubs and fakes ─────────────────────────

    /// <summary>
    /// Deterministic always-valid media validation so revalidation (if a test ever
    /// leaves stale metadata) can never reach out to storage or flake.
    /// </summary>
    private sealed class EnforcementValidationStub : IMediaValidationService
    {
        public Task<MediaValidationResult> ValidateAsync(MediaValidationRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new MediaValidationResult(
                true, null, null, false,
                StreamEnforcementFixture.AudioPayload.Length, "audio/mpeg", new string('a', 64), 30_000, "test-v1"));
        }
    }

    /// <summary>
    /// S3-like fake storage: serves exactly one object (the fixture's authoritative
    /// media key) from a NON-seekable stream so the controller exercises its manual
    /// 206/416 branch — the same branch production uses when proxying from R2/S3.
    ///
    /// Range handling follows RFC 9110, pinned for determinism:
    ///   - missing / non-"bytes=" / malformed / reversed (last &lt; first, §14.1.1) /
    ///     multi-range headers are IGNORED → full 200 body (S3/R2 behave the same
    ///     for multi-range);
    ///   - "bytes=-N" serves the final N bytes; "bytes=S-" serves S through the end;
    ///   - a syntactically valid range whose start is past the end is unsatisfiable
    ///     → 416 with TotalLength so the controller emits "bytes */TOTAL".
    /// </summary>
    private sealed class RangeAwareS3LikeStorage : IObjectStorage
    {
        public Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
            => Task.FromResult(key);

        public string GenerateSignedUrl(string key) => $"https://fake-storage.cambrian.test/{key}?signed=true";

        public string GetPublicUrl(string key) => $"https://fake-storage.cambrian.test/{key}";

        public Task DeleteAsync(string key) => Task.CompletedTask;

        public Task<StorageFile?> OpenReadAsync(string key) => OpenReadAsync(key, null);

        public Task<StorageFile?> OpenReadAsync(string key, string? rangeHeader)
        {
            if (!string.Equals(key, StreamEnforcementFixture.AudioObjectKey, StringComparison.Ordinal))
                return Task.FromResult<StorageFile?>(null);

            var payload = StreamEnforcementFixture.AudioPayload;
            long total = payload.Length;

            if (string.IsNullOrWhiteSpace(rangeHeader)
                || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<StorageFile?>(Full(payload));

            var spec = rangeHeader["bytes=".Length..].Trim();
            if (spec.Length == 0 || spec.Contains(','))
                return Task.FromResult<StorageFile?>(Full(payload)); // multi-range → full object

            var dash = spec.IndexOf('-');
            if (dash < 0)
                return Task.FromResult<StorageFile?>(Full(payload)); // malformed → ignore

            var firstRaw = spec[..dash].Trim();
            var lastRaw = spec[(dash + 1)..].Trim();

            long start, end;
            if (firstRaw.Length == 0)
            {
                // suffix-range "bytes=-N": the final N bytes.
                if (!long.TryParse(lastRaw, out var suffixLength))
                    return Task.FromResult<StorageFile?>(Full(payload));
                if (suffixLength <= 0)
                    return Task.FromResult<StorageFile?>(Unsatisfiable(total));
                var count = Math.Min(suffixLength, total);
                start = total - count;
                end = total - 1;
            }
            else
            {
                if (!long.TryParse(firstRaw, out start))
                    return Task.FromResult<StorageFile?>(Full(payload));
                if (lastRaw.Length == 0)
                {
                    end = total - 1; // open-ended "bytes=S-"
                }
                else if (!long.TryParse(lastRaw, out end))
                {
                    return Task.FromResult<StorageFile?>(Full(payload));
                }
                else if (end < start)
                {
                    // RFC 9110 §14.1.1: last-byte-pos < first-byte-pos is an INVALID
                    // spec — the header is ignored (200 full), not answered with 416.
                    return Task.FromResult<StorageFile?>(Full(payload));
                }

                if (start >= total)
                    return Task.FromResult<StorageFile?>(Unsatisfiable(total));
                end = Math.Min(end, total - 1);
            }

            var slice = payload[(int)start..(int)(end + 1)];
            return Task.FromResult<StorageFile?>(new StorageFile
            {
                Stream = new NonSeekableStream(slice),
                ContentType = "audio/mpeg",
                Length = slice.Length,
                TotalLength = total,
                IsPartialContent = true,
                ContentRange = $"bytes {start}-{end}/{total}",
            });
        }

        public Task<StorageObjectMetadata?> GetMetadataAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(key, StreamEnforcementFixture.AudioObjectKey, StringComparison.Ordinal))
                return Task.FromResult<StorageObjectMetadata?>(null);
            return Task.FromResult<StorageObjectMetadata?>(new StorageObjectMetadata(
                key, StreamEnforcementFixture.AudioPayload.Length, "audio/mpeg", null, DateTime.UtcNow));
        }

        private static StorageFile Full(byte[] payload) => new()
        {
            Stream = new NonSeekableStream(payload),
            ContentType = "audio/mpeg",
            Length = payload.Length,
            TotalLength = payload.Length,
            IsPartialContent = false,
        };

        private static StorageFile Unsatisfiable(long total) => new()
        {
            Stream = Stream.Null,
            ContentType = "audio/mpeg",
            Length = 0,
            TotalLength = total,
            IsRangeNotSatisfiable = true,
        };
    }

    /// <summary>
    /// Read-only, forward-only stream so the controller takes the S3 (manual-206)
    /// branch rather than the seekable ASP.NET File() branch.
    /// </summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;
        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.ReadAsync(buffer, offset, count, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Frozen clock for minting tickets in the past (expiry tests).</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
