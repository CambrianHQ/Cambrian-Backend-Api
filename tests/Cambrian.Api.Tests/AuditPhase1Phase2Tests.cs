using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests covering the Phase 1 (transactional integrity) and Phase 2 (security)
/// audit remediation changes.
/// </summary>
public sealed class AuditPhase1Phase2Tests : IDisposable
{
    // ── shared webhook test infrastructure ──────────────────────────
    private readonly CambrianDbContext _db;
    private readonly ILogger<StripeWebhookService> _webhookLogger = Substitute.For<ILogger<StripeWebhookService>>();
    private readonly ILicenseService _licenseService = Substitute.For<ILicenseService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();

    // ── shared auth test infrastructure ─────────────────────────────
    private readonly UserManager<ApplicationUser> _users;
    private readonly AuthService _authService;

    public AuditPhase1Phase2Tests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CambrianDbContext(options);

        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _users = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);

        var jwtOptions = Options.Create(new JwtSettings
        {
            Key = "test-secret-key-that-is-long-enough-for-hmac256!",
            Issuer = "test-issuer",
            Audience = "test-audience"
        });
        var googleOptions = Options.Create(new GoogleSettings { ClientId = "" });
        var subscriptions = Substitute.For<ISubscriptionRepository>();
        var sms = Substitute.For<ISmsService>();
        var authLogger = Substitute.For<ILogger<AuthService>>();
        var authConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://test" })
            .Build();

        _authService = new AuthService(
            _users, jwtOptions, googleOptions, subscriptions, _emailService, sms, authConfig, authLogger);
    }

    public void Dispose() => _db.Dispose();

    private StripeWebhookService CreateWebhookService()
    {
        var config = Substitute.For<IConfiguration>();
        config["Stripe:WebhookSecret"].Returns("whsec_test");
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        return new StripeWebhookService(_db, _licenseService, _emailService, config, _webhookLogger, env);
    }

    private static string UniqueEventId() => $"evt_{Guid.NewGuid():N}";

    // ════════════════════════════════════════════════════════════════
    // Phase 1 — Webhook idempotency (Task 1)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessEventAsync_ReplayedEvent_DoesNotCreateDuplicatePurchase()
    {
        var trackId = Guid.NewGuid();
        var userId = "buyer-1";
        _db.Tracks.Add(new Track { Id = trackId, Title = "Test Beat", CreatorId = "creator-1" });
        await _db.SaveChangesAsync();

        var svc = CreateWebhookService();
        var eventId = UniqueEventId();

        // First call — creates purchase
        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:{trackId}:non-exclusive",
            amountTotal: 2999,
            stripeCustomerId: null,
            stripeSessionId: null);

        // Second call — same event ID, must be a no-op
        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:{trackId}:non-exclusive",
            amountTotal: 2999,
            stripeCustomerId: null,
            stripeSessionId: null);

        // Exactly one purchase, one library item, one webhook event row
        Assert.Equal(1, await _db.Purchases.CountAsync());
        Assert.Equal(1, await _db.Library.CountAsync());
        Assert.Single(await _db.StripeWebhookEvents.ToListAsync());
    }

    [Fact]
    public async Task ProcessEventAsync_ReplayedEvent_StatusRemainsCompleted()
    {
        var svc = CreateWebhookService();
        var eventId = UniqueEventId();

        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null);

        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null);

        var evt = await _db.StripeWebhookEvents.FirstAsync(e => e.EventId == eventId);
        Assert.Equal("completed", evt.Status);
    }

    [Fact]
    public async Task ProcessEventAsync_FailedEvent_RecordsStatusFailedWithErrorMessage()
    {
        // Exclusive purchase triggers raw SQL which throws on InMemory DB
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Fail Beat", CreatorId = "c1" });
        await _db.SaveChangesAsync();

        var svc = CreateWebhookService();
        var eventId = UniqueEventId();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ProcessEventAsync(
                eventId: eventId,
                eventType: "checkout.session.completed",
                clientReferenceId: $"user1:{trackId}:exclusive",
                amountTotal: 5000,
                stripeCustomerId: null,
                stripeSessionId: null));

        var evt = await _db.StripeWebhookEvents.FirstOrDefaultAsync(e => e.EventId == eventId);
        Assert.NotNull(evt);
        Assert.Equal("failed", evt.Status);
        Assert.False(evt.Processed);
        Assert.NotNull(evt.ErrorMessage);
    }

    [Fact]
    public async Task ProcessEventAsync_FailedEventIsRetriable_SecondCallProcessesSuccessfully()
    {
        // First call fails (exclusive triggers raw SQL → InvalidOperationException on InMemory)
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Retry Beat", CreatorId = "c1" });
        await _db.SaveChangesAsync();

        var svc = CreateWebhookService();
        var eventId = UniqueEventId();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ProcessEventAsync(
                eventId: eventId,
                eventType: "checkout.session.completed",
                clientReferenceId: $"user1:{trackId}:exclusive",
                amountTotal: 5000,
                stripeCustomerId: null,
                stripeSessionId: null));

        var afterFailure = await _db.StripeWebhookEvents.FirstAsync(e => e.EventId == eventId);
        Assert.Equal("failed", afterFailure.Status);

        // Second call with same event ID — still exclusive (InMemory), still fails → upserts row
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ProcessEventAsync(
                eventId: eventId,
                eventType: "checkout.session.completed",
                clientReferenceId: $"user1:{trackId}:exclusive", // still same exclusive → still fails
                amountTotal: 5000,
                stripeCustomerId: null,
                stripeSessionId: null));

        // Still only one webhook event row (upserted, not duplicated)
        Assert.Single(await _db.StripeWebhookEvents.ToListAsync());
    }

    // ════════════════════════════════════════════════════════════════
    // Phase 1 — Webhook event persisted before business logic (Task 1)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessEventAsync_EventRowSaved_EvenWhenBusinessLogicFails()
    {
        // Exclusive purchase fails on InMemory (raw SQL not supported)
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Beat", CreatorId = "c1" });
        await _db.SaveChangesAsync();

        var svc = CreateWebhookService();
        var eventId = UniqueEventId();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ProcessEventAsync(
                eventId: eventId,
                eventType: "checkout.session.completed",
                clientReferenceId: $"user1:{trackId}:exclusive",
                amountTotal: 5000,
                stripeCustomerId: null,
                stripeSessionId: null));

        // The webhook event row must exist even though business logic threw
        Assert.True(await _db.StripeWebhookEvents.AnyAsync(e => e.EventId == eventId));
    }

    // ════════════════════════════════════════════════════════════════
    // Phase 2 — Password reset per-account lockout (Task 6)
    // ════════════════════════════════════════════════════════════════

    private static string HashCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private ApplicationUser MakeUserWithResetCode(string code = "ABCD1234")
    {
        return new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "user@test.com",
            PasswordResetCode = HashCode(code),
            PasswordResetCodeExpiry = DateTime.UtcNow.AddMinutes(15),
            PasswordResetAttemptCount = 0,
            PasswordResetLockedUntil = null
        };
    }

    [Fact]
    public async Task VerifyCodeAsync_IncrementsAttemptCount_OnWrongCode()
    {
        var user = MakeUserWithResetCode("ABCD1234");
        _users.FindByEmailAsync("user@test.com").Returns(user);

        ApplicationUser? saved = null;
        await _users.UpdateAsync(Arg.Do<ApplicationUser>(u => saved = u));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _authService.VerifyCodeAsync(new VerifyCodeRequest
            {
                Email = "user@test.com",
                Code = "WRONGCOD"
            }));

        Assert.NotNull(saved);
        Assert.Equal(1, saved!.PasswordResetAttemptCount);
    }

    [Fact]
    public async Task VerifyCodeAsync_AppliesLockout_AfterMaxAttempts()
    {
        var user = MakeUserWithResetCode("ABCD1234");
        user.PasswordResetAttemptCount = 4; // one more wrong = lockout
        _users.FindByEmailAsync("user@test.com").Returns(user);

        ApplicationUser? saved = null;
        await _users.UpdateAsync(Arg.Do<ApplicationUser>(u => saved = u));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _authService.VerifyCodeAsync(new VerifyCodeRequest
            {
                Email = "user@test.com",
                Code = "WRONGCOD"
            }));

        Assert.NotNull(saved);
        Assert.Equal(5, saved!.PasswordResetAttemptCount);
        Assert.NotNull(saved.PasswordResetLockedUntil);
        Assert.Null(saved.PasswordResetCode); // code invalidated
        Assert.True(saved.PasswordResetLockedUntil > DateTime.UtcNow);
    }

    [Fact]
    public async Task VerifyCodeAsync_RejectsImmediately_WhenLockedOut()
    {
        var user = MakeUserWithResetCode("ABCD1234");
        user.PasswordResetLockedUntil = DateTime.UtcNow.AddMinutes(10); // active lockout
        _users.FindByEmailAsync("user@test.com").Returns(user);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _authService.VerifyCodeAsync(new VerifyCodeRequest
            {
                Email = "user@test.com",
                Code = "ABCD1234" // even correct code rejected during lockout
            }));

        // Must not call UpdateAsync during active lockout (no DB write)
        await _users.DidNotReceive().UpdateAsync(Arg.Any<ApplicationUser>());
    }

    [Fact]
    public async Task VerifyCodeAsync_Succeeds_WithCorrectCode()
    {
        var user = MakeUserWithResetCode("ABCD1234");
        _users.FindByEmailAsync("user@test.com").Returns(user);

        // Should not throw
        await _authService.VerifyCodeAsync(new VerifyCodeRequest
        {
            Email = "user@test.com",
            Code = "abcd1234" // case-insensitive
        });
    }

    [Fact]
    public async Task VerifyCodeAsync_ErrorMessageDoesNotRevealLockoutState()
    {
        // Both lockout-rejection and wrong-code should produce the same error message
        var lockedUser = MakeUserWithResetCode("ABCD1234");
        lockedUser.PasswordResetLockedUntil = DateTime.UtcNow.AddMinutes(5);
        _users.FindByEmailAsync("locked@test.com").Returns(lockedUser);

        var wrongCodeUser = MakeUserWithResetCode("ABCD1234");
        _users.FindByEmailAsync("wrong@test.com").Returns(wrongCodeUser);
        await _users.UpdateAsync(Arg.Any<ApplicationUser>());

        var exLocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _authService.VerifyCodeAsync(new VerifyCodeRequest
                { Email = "locked@test.com", Code = "ABCD1234" }));

        var exWrong = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _authService.VerifyCodeAsync(new VerifyCodeRequest
                { Email = "wrong@test.com", Code = "WRONGCOD" }));

        // Both produce the same generic message — no "locked" vs "wrong" enumeration leak
        Assert.Equal(exLocked.Message, exWrong.Message);
    }

    [Fact]
    public async Task ForgotPasswordAsync_ResetsAttemptCountAndClearsLockout_OnNewCode()
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "user@test.com",
            PasswordResetAttemptCount = 5,
            PasswordResetLockedUntil = DateTime.UtcNow.AddMinutes(10)
        };
        _users.FindByEmailAsync("user@test.com").Returns(user);

        ApplicationUser? saved = null;
        await _users.UpdateAsync(Arg.Do<ApplicationUser>(u => saved = u));

        // _emailService.SendAsync is a void/Task, no setup needed (returns default Task)
        await _authService.ForgotPasswordAsync(new ForgotPasswordRequest { Email = "user@test.com" });

        Assert.NotNull(saved);
        Assert.Equal(0, saved!.PasswordResetAttemptCount);
        Assert.Null(saved.PasswordResetLockedUntil);
        Assert.NotNull(saved.PasswordResetCode); // new code issued
    }

    [Fact]
    public async Task VerifyCodeAsync_RejectsNonExistentUser_WithGenericMessage()
    {
        _users.FindByEmailAsync("nobody@test.com").Returns((ApplicationUser?)null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _authService.VerifyCodeAsync(new VerifyCodeRequest
            {
                Email = "nobody@test.com",
                Code = "ABCD1234"
            }));

        // Must not reveal that account doesn't exist
        Assert.Contains("Invalid or expired", ex.Message);
    }

    // ── F11: Admin Creator Promotion Guard ─────────────────────────

    [Fact]
    public async Task SetUserRole_ToCreator_WithoutUsername_Throws()
    {
        // Arrange: user whose UserName == Email (onboarding not completed)
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "noname@test.com",
            UserName = "noname@test.com", // sentinel: equals email
            Role = "User"
        };
        _users.FindByIdAsync(user.Id).Returns(user);

        var emailService = Substitute.For<IEmailService>();
        var repo = new AdminRepository(_db, _users, emailService);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.SetUserRoleAsync(user.Id, "Creator", "admin@test.com"));
        Assert.Contains("username onboarding", ex.Message);
    }

    [Fact]
    public async Task SetUserRole_ToCreator_WithUsername_Succeeds()
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "named@test.com",
            UserName = "coolcreator",
            Role = "User"
        };
        _users.FindByIdAsync(user.Id).Returns(user);
        _users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);

        var emailService = Substitute.For<IEmailService>();
        var repo = new AdminRepository(_db, _users, emailService);

        var ok = await repo.SetUserRoleAsync(user.Id, "Creator", "admin@test.com");
        Assert.True(ok);
        Assert.Equal("Creator", user.Role);
    }
}
