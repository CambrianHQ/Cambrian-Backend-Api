using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Security baseline tests — one failing security test + one non-regression test
/// per critical issue (C1–C4). Each security test documents the exploit and proves
/// it stops working after the patch.
/// </summary>
public sealed class SecurityPatchTests
{
    // ──────────────────────────────────────────────────────────────
    // C3 · Download without purchase
    // ──────────────────────────────────────────────────────────────

    private sealed class C3Fixture
    {
        public ITrackRepository Tracks { get; } = Substitute.For<ITrackRepository>();
        public IObjectStorage Storage { get; } = Substitute.For<IObjectStorage>();
        public IEntitlementService Entitlement { get; } = Substitute.For<IEntitlementService>();
        public DownloadController Controller { get; }

        public C3Fixture()
        {
            var logger = Substitute.For<ILogger<DownloadController>>();
            Controller = new DownloadController(Tracks, Storage, Entitlement, logger);
            SetUser("user-1");
        }

        public void SetUser(string userId)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));
            Controller.ControllerContext = new ControllerContext { HttpContext = context };
        }
    }

    /// <summary>
    /// SECURITY — exploit: a user whose library row was added manually (not via purchase)
    /// must NOT be allowed to download.
    /// Before the patch this test fails (returns 200).
    /// After the patch it passes (returns 403).
    /// </summary>
    [Fact]
    public async Task C3_Security_Download_Returns403_WithLibraryItemButNoCompletedPurchase()
    {
        var fix = new C3Fixture();
        var trackId = Guid.NewGuid();

        // No completed purchase
        fix.Entitlement.CanDownloadAsync("user-1", trackId).Returns(false);

        var result = await fix.Controller.Download(trackId.ToString());

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
    }

    /// <summary>
    /// REGRESSION — a user who bought a track through the normal checkout flow
    /// must still be able to download it after the patch.
    /// </summary>
    [Fact]
    public async Task C3_Regression_Download_Returns200_WhenUserHasCompletedPurchase()
    {
        var fix = new C3Fixture();
        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            Title = "My Beat",
            AudioUrl = "tracks/beat.mp3",
            CambrianTrackId = "CAMB-TRK-TEST01",
            CreatorId = "creator-1"
        };

        fix.Entitlement.CanDownloadAsync("user-1", trackId).Returns(true);
        fix.Tracks.GetByIdAsync(trackId).Returns(track);
        fix.Storage.OpenReadAsync("tracks/beat.mp3").Returns(new StorageFile
        {
            Stream = new MemoryStream(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }),
            ContentType = "audio/mpeg",
            Length = 4
        });
        fix.Storage.GenerateDownloadUrl(Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/file.mp3");

        var result = await fix.Controller.Download(trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
    }

    /// <summary>
    /// REGRESSION — DownloadFile (binary fallback) also uses purchase entitlement.
    /// </summary>
    [Fact]
    public async Task C3_Regression_DownloadFile_Returns200_WhenUserHasCompletedPurchase()
    {
        var fix = new C3Fixture();
        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            Title = "My Beat",
            AudioUrl = "tracks/beat.mp3",
            CambrianTrackId = "CAMB-TRK-TEST02",
            CreatorId = "creator-1"
        };

        fix.Entitlement.CanDownloadAsync("user-1", trackId).Returns(true);
        fix.Tracks.GetByIdAsync(trackId).Returns(track);
        fix.Storage.OpenReadAsync("tracks/beat.mp3").Returns(new StorageFile
        {
            Stream = new MemoryStream(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }),
            ContentType = "audio/mpeg"
        });

        var result = await fix.Controller.DownloadFile(trackId.ToString());

        Assert.IsType<FileStreamResult>(result);
    }

    /// <summary>
    /// SECURITY — DownloadFile must also block users with no completed purchase.
    /// </summary>
    [Fact]
    public async Task C3_Security_DownloadFile_Returns403_WithNoCompletedPurchase()
    {
        var fix = new C3Fixture();
        var trackId = Guid.NewGuid();

        fix.Entitlement.CanDownloadAsync("user-1", trackId).Returns(false);

        var result = await fix.Controller.DownloadFile(trackId.ToString());

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────
    // C4 · Hidden tracks accessible
    // ──────────────────────────────────────────────────────────────

    private sealed class C4CatalogFixture
    {
        public ICatalogService CatalogService { get; } = Substitute.For<ICatalogService>();
        public IObjectStorage Storage { get; } = Substitute.For<IObjectStorage>();
        public IActivityService Activity { get; } = Substitute.For<IActivityService>();
        public CatalogController Controller { get; }

        public C4CatalogFixture()
        {
            var cache = Substitute.For<IMemoryCache>();
            Controller = new CatalogController(CatalogService, Storage, cache, Activity, new TrackVisibilityPolicy(), Substitute.For<ITrackDetailsRepository>());
        }

        public void SetAuthenticatedUser(string userId)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));
            Controller.ControllerContext = new ControllerContext { HttpContext = context };
        }

        public void SetAnonymousUser()
        {
            Controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }
    }

    /// <summary>
    /// SECURITY — a hidden track being accessible to a non-owner via GET /tracks/{id}.
    /// Before the patch this returns 200. After the patch it returns 404.
    /// </summary>
    [Fact]
    public async Task C4_Security_GetTrack_Returns404_ForHiddenTrack_WhenNotOwner()
    {
        var fix = new C4CatalogFixture();
        var trackId = Guid.NewGuid();

        fix.CatalogService.GetTrackAsync(trackId.ToString()).Returns(new TrackResponse
        {
            Id = trackId.ToString(),
            CambrianTrackId = "CAMB-TRK-HIDDEN",
            Title = "Hidden Beat",
            CreatorId = "creator-1",
            Visibility = "hidden"
        });
        fix.SetAuthenticatedUser("other-user");

        var result = await fix.Controller.GetTrack(trackId.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// REGRESSION — the track's creator must still be able to fetch their own hidden track.
    /// </summary>
    [Fact]
    public async Task C4_Regression_GetTrack_Returns200_ForHiddenTrack_WhenOwner()
    {
        var fix = new C4CatalogFixture();
        var trackId = Guid.NewGuid();

        fix.CatalogService.GetTrackAsync(trackId.ToString()).Returns(new TrackResponse
        {
            Id = trackId.ToString(),
            CambrianTrackId = "CAMB-TRK-HIDDEN",
            Title = "Hidden Beat",
            CreatorId = "creator-1",
            Visibility = "hidden"
        });
        fix.SetAuthenticatedUser("creator-1"); // same as CreatorId

        var result = await fix.Controller.GetTrack(trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
    }

    /// <summary>
    /// REGRESSION — public tracks must remain accessible to everyone.
    /// </summary>
    [Fact]
    public async Task C4_Regression_GetTrack_Returns200_ForPublicTrack_WhenAnonymous()
    {
        var fix = new C4CatalogFixture();
        var trackId = Guid.NewGuid();

        fix.CatalogService.GetTrackAsync(trackId.ToString()).Returns(new TrackResponse
        {
            Id = trackId.ToString(),
            CambrianTrackId = "CAMB-TRK-PUB",
            Title = "Public Beat",
            CreatorId = "creator-1",
            Visibility = "public"
        });
        fix.SetAnonymousUser();

        var result = await fix.Controller.GetTrack(trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
    }

    private sealed class C4StreamFixture
    {
        public ITrackRepository Tracks { get; } = Substitute.For<ITrackRepository>();
        public IObjectStorage Storage { get; } = Substitute.For<IObjectStorage>();
        public StreamController Controller { get; }

        public C4StreamFixture()
        {
            var streams = Substitute.For<IStreamRepository>();
            var logger = Substitute.For<ILogger<StreamController>>();
            Controller = new StreamController(Tracks, Storage, streams, new TrackVisibilityPolicy(), logger);
        }

        public void SetAnonymousUser()
        {
            Controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        public void SetAuthenticatedUser(string userId)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));
            Controller.ControllerContext = new ControllerContext { HttpContext = context };
        }
    }

    /// <summary>
    /// SECURITY — hidden tracks accessible anonymously via /stream/{id}/audio.
    /// Before the patch this returns 302 Redirect. After the patch it returns 404.
    /// </summary>
    [Fact]
    public async Task C4_Security_StreamAudio_Returns404_ForHiddenTrack_WhenAnonymous()
    {
        var fix = new C4StreamFixture();
        var trackId = Guid.NewGuid();

        fix.Tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            Title = "Secret Beat",
            AudioUrl = "tracks/secret.mp3",
            Visibility = "hidden",
            CreatorId = "creator-1"
        });
        fix.Storage.GenerateSignedUrl(Arg.Any<string>()).Returns("https://cdn.test/signed");
        fix.SetAnonymousUser();

        var result = await fix.Controller.StreamAudio(trackId.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// REGRESSION — public tracks must still stream for anonymous users.
    /// Audio is proxied through the backend (not redirected) to avoid R2 CORS issues.
    /// </summary>
    [Fact]
    public async Task C4_Regression_StreamAudio_Returns302_ForPublicTrack_WhenAnonymous()
    {
        var fix = new C4StreamFixture();
        var trackId = Guid.NewGuid();

        fix.Tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            Title = "Public Beat",
            AudioUrl = "tracks/public.mp3",
            Visibility = "public",
            CreatorId = "creator-1"
        });
        fix.Storage.OpenReadAsync("tracks/public.mp3").Returns(new StorageFile
        {
            Stream = new MemoryStream(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }),
            ContentType = "audio/mpeg",
            Length = 4
        });
        fix.SetAnonymousUser();

        var result = await fix.Controller.StreamAudio(trackId.ToString());

        Assert.IsType<FileStreamResult>(result);
    }

    /// <summary>
    /// REGRESSION — when the client sends a Range header and the storage backend
    /// returns a non-seekable partial-content stream (the real S3/R2 production
    /// path), StreamAudio must write a 206 Partial Content response with the
    /// exact Accept-Ranges, Content-Range, Content-Length, and Content-Type
    /// headers that Safari's &lt;audio&gt; element and iOS AVPlayer require to
    /// play the file. Without these headers both clients surface
    /// "Unable to load audio file, may be unavailable or corrupted".
    /// </summary>
    [Fact]
    public async Task C4_StreamAudio_Emits206_WhenRangeHeaderPresent_AndStreamIsNonSeekable()
    {
        var fix = new C4StreamFixture();
        var trackId = Guid.NewGuid();

        fix.Tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            Title = "Ranged Beat",
            AudioUrl = "tracks/ranged.mp3",
            Visibility = "public",
            CreatorId = "creator-1"
        });

        // Non-seekable stream wrapping an MP3 byte fragment — models the
        // HttpContent stream returned by S3ObjectStorage for a 206 response.
        var body = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        var nonSeekable = new NonSeekableStream(new MemoryStream(body));

        fix.Storage
            .OpenReadAsync("tracks/ranged.mp3", "bytes=0-3")
            .Returns(new StorageFile
            {
                Stream = nonSeekable,
                ContentType = "audio/mpeg",
                Length = 4,
                TotalLength = 1024,
                IsPartialContent = true,
                ContentRange = "bytes 0-3/1024"
            });

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Range"] = "bytes=0-3";
        ctx.Response.Body = new MemoryStream();
        fix.Controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        var result = await fix.Controller.StreamAudio(trackId.ToString());

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status206PartialContent, ctx.Response.StatusCode);
        Assert.Equal("bytes 0-3/1024", ctx.Response.Headers["Content-Range"].ToString());
        Assert.Equal("bytes", ctx.Response.Headers["Accept-Ranges"].ToString());
        Assert.Equal("audio/mpeg", ctx.Response.ContentType);
        Assert.Equal(4, ctx.Response.ContentLength);
        Assert.Equal(body, ((MemoryStream)ctx.Response.Body).ToArray());

        // Ensure the storage-level range call actually happened — if anyone
        // accidentally drops Range forwarding in the controller or storage
        // layer, this assertion fails loudly.
        await fix.Storage.Received(1).OpenReadAsync("tracks/ranged.mp3", "bytes=0-3");
    }

    /// <summary>
    /// REGRESSION — when the client sends a Range header on a public track but
    /// the storage backend returns a non-partial (full 200 OK) response with a
    /// non-seekable stream, StreamAudio must still emit Content-Length and
    /// Accept-Ranges so Safari/iOS can begin playback.
    /// </summary>
    [Fact]
    public async Task C4_StreamAudio_Emits200WithContentLength_WhenNoRangeHeader_AndStreamIsNonSeekable()
    {
        var fix = new C4StreamFixture();
        var trackId = Guid.NewGuid();

        fix.Tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            Title = "Full Beat",
            AudioUrl = "tracks/full.mp3",
            Visibility = "public",
            CreatorId = "creator-1"
        });

        var body = new byte[] { 0xFF, 0xFB, 0x90, 0x00, 0x11, 0x22 };
        var nonSeekable = new NonSeekableStream(new MemoryStream(body));

        fix.Storage
            .OpenReadAsync("tracks/full.mp3")
            .Returns(new StorageFile
            {
                Stream = nonSeekable,
                ContentType = "audio/mpeg",
                Length = 6,
                TotalLength = 6,
                IsPartialContent = false,
                ContentRange = null
            });

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        fix.Controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        var result = await fix.Controller.StreamAudio(trackId.ToString());

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal("bytes", ctx.Response.Headers["Accept-Ranges"].ToString());
        Assert.Equal("audio/mpeg", ctx.Response.ContentType);
        Assert.Equal(6, ctx.Response.ContentLength);
        Assert.False(ctx.Response.Headers.ContainsKey("Content-Range"));
        Assert.Equal(body, ((MemoryStream)ctx.Response.Body).ToArray());
    }

    /// <summary>
    /// Minimal non-seekable stream wrapper used to model the HttpContent stream
    /// returned by S3ObjectStorage — a forward-only network stream whose
    /// <see cref="Stream.CanSeek"/> is false. Delegates all reads to the inner
    /// stream and rejects every other operation.
    /// </summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStream(Stream inner) => _inner = inner;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // C2 · Email change without confirmation
    // ──────────────────────────────────────────────────────────────

    private sealed class C2Fixture
    {
        public UserManager<ApplicationUser> Users { get; }
        public IEmailService Email { get; } = Substitute.For<IEmailService>();
        public AuthService Sut { get; }

        public C2Fixture()
        {
            var store = Substitute.For<IUserStore<ApplicationUser>>();
            Users = Substitute.For<UserManager<ApplicationUser>>(
                store, null, null, null, null, null, null, null, null);

            var jwtOpts = Options.Create(new JwtSettings
            {
                Key = "test-secret-key-that-is-long-enough-for-hmac256!",
                Issuer = "test-issuer",
                Audience = "test-audience"
            });
            var subs = Substitute.For<ISubscriptionRepository>();
            var sms = Substitute.For<ISmsService>();
            var google = Options.Create(new GoogleSettings { ClientId = "g-client-id" });
            var logger = Substitute.For<ILogger<AuthService>>();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://test" })
                .Build();

            Sut = new AuthService(Users, jwtOpts, google, subs, Email, sms, config, logger);
        }

        public ClaimsPrincipal MakeUserPrincipal(string userId) =>
            new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));
    }

    /// <summary>
    /// SECURITY — email change must NOT apply immediately; it must require
    /// the user to click the verification link sent to the new address.
    /// Before the patch, Email is changed instantly.
    /// After the patch, Email stays the same and PendingEmail is set instead.
    /// </summary>
    [Fact]
    public async Task C2_Security_ChangeEmail_StoresPendingEmail_DoesNotChangeEmailImmediately()
    {
        var fix = new C2Fixture();

        var user = new ApplicationUser
        {
            Id = "u1",
            Email = "old@example.com",
            UserName = "old@example.com"
        };

        fix.Users.FindByIdAsync("u1").Returns(user);
        fix.Users.CheckPasswordAsync(user, "Password123!").Returns(true);
        fix.Users.FindByEmailAsync("new@example.com").Returns((ApplicationUser?)null);
        fix.Users.UpdateAsync(user).Returns(IdentityResult.Success);

        var principal = fix.MakeUserPrincipal("u1");
        await fix.Sut.ChangeEmailAsync(principal, new ChangeEmailRequest
        {
            Password = "Password123!",
            NewEmail = "new@example.com"
        });

        // Email must NOT change immediately
        Assert.Equal("old@example.com", user.Email);

        // PendingEmail must be set
        Assert.Equal("new@example.com", user.PendingEmail);

        // Token must be stored (as a 64-char SHA-256 hex hash)
        Assert.NotNull(user.EmailChangeToken);
        Assert.Equal(64, user.EmailChangeToken!.Length);

        // Verification email must go to new address
        await fix.Email.Received(1)
            .SendEmailChangeVerificationAsync(
                "new@example.com",
                Arg.Is<string>(link => !string.IsNullOrEmpty(link)));
    }

    /// <summary>
    /// SECURITY — notification must be sent to the OLD email address so the account
    /// owner can react if the change was not initiated by them.
    /// </summary>
    [Fact]
    public async Task C2_Security_ChangeEmail_NotifiesOldEmailAddress()
    {
        var fix = new C2Fixture();

        var user = new ApplicationUser
        {
            Id = "u1",
            Email = "old@example.com",
            UserName = "old@example.com"
        };

        fix.Users.FindByIdAsync("u1").Returns(user);
        fix.Users.CheckPasswordAsync(user, "Password123!").Returns(true);
        fix.Users.FindByEmailAsync("new@example.com").Returns((ApplicationUser?)null);
        fix.Users.UpdateAsync(user).Returns(IdentityResult.Success);

        var principal = fix.MakeUserPrincipal("u1");
        await fix.Sut.ChangeEmailAsync(principal, new ChangeEmailRequest
        {
            Password = "Password123!",
            NewEmail = "new@example.com"
        });

        await fix.Email.Received(1)
            .SendEmailChangeNotificationAsync("old@example.com", "new@example.com");
    }

    /// <summary>
    /// REGRESSION — completing the verification must actually update the live email.
    /// </summary>
    [Fact]
    public async Task C2_Regression_VerifyEmailChange_UpdatesEmail_WhenTokenIsValid()
    {
        var fix = new C2Fixture();

        // Token format: "{userId}.{randomBytes}" — userId is embedded so the service
        // can use FindByIdAsync instead of an EF async LINQ scan.
        const string userId = "u1";
        var plaintext = $"{userId}.TESTTEST";
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

        var user = new ApplicationUser
        {
            Id = userId,
            Email = "old@example.com",
            UserName = "old@example.com",
            PendingEmail = "new@example.com",
            EmailChangeToken = hash,
            EmailChangeTokenExpiry = DateTime.UtcNow.AddHours(24)
        };

        // Service resolves user via FindByIdAsync (no EF async LINQ needed)
        fix.Users.FindByIdAsync(userId).Returns(user);
        fix.Users.UpdateAsync(user).Returns(IdentityResult.Success);

        await fix.Sut.VerifyEmailChangeAsync(plaintext);

        Assert.Equal("new@example.com", user.Email);
        Assert.Equal("new@example.com", user.UserName);
        Assert.Null(user.PendingEmail);
        Assert.Null(user.EmailChangeToken);
    }
}
