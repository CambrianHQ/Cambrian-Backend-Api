# Platform Stability Audit Report

**Branch:** `cursor/platform-stability-audit-7360`
**Date:** 2026-03-19
**Scope:** Authentication, user flows, entities, repositories, DTOs, controllers

---

## 1. FULL FILE CONTENTS

### 1.1 AuthService.cs

```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Cambrian.Application.Services;

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IConfiguration _config;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IEmailService _email;

    private static readonly TimeSpan ResetCodeLifetime = TimeSpan.FromMinutes(15);

    public AuthService(
        UserManager<ApplicationUser> users,
        IConfiguration config,
        ISubscriptionRepository subscriptions,
        IEmailService email)
    {
        _users = users;
        _config = config;
        _subscriptions = subscriptions;
        _email = email;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var user = await _users.FindByEmailAsync(request.Email);
        if (user is null)
            throw new UnauthorizedAccessException("Invalid credentials");

        var valid = await _users.CheckPasswordAsync(user, request.Password);
        if (!valid)
            throw new UnauthorizedAccessException("Invalid credentials");

        var token = GenerateJwt(user);
        return new AuthResponse
        {
            UserId = Guid.Parse(user.Id),
            Email = user.Email ?? "",
            Token = token,
            Tier = (user.Tier ?? "free").ToLowerInvariant(),
            Role = user.Role ?? "User"
        };
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
    {
        var isCreator = string.Equals(request.Role, "creator", StringComparison.OrdinalIgnoreCase);
        var user = new ApplicationUser
        {
            Email = request.Email,
            UserName = request.Email,
            DisplayName = request.DisplayName ?? request.Email.Split('@')[0],
            Tier = isCreator ? "creator" : "free",
            Role = isCreator ? "Creator" : "User",
            CreatorTier = CreatorTier.Free
        };

        var result = await _users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Registration failed: {errors}");
        }

        var token = GenerateJwt(user);
        return new AuthResponse
        {
            UserId = Guid.Parse(user.Id),
            Email = user.Email ?? "",
            Token = token,
            Tier = user.Tier,
            Role = user.Role
        };
    }

    public async Task<UserProfileResponse> GetCurrentUserAsync(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null)
            throw new UnauthorizedAccessException("No user identity found");

        var user = await _users.FindByIdAsync(userId)
                   ?? throw new UnauthorizedAccessException("User not found");

        var tierConfig = TierManifest.For(user.CreatorTier);
        return new UserProfileResponse
        {
            UserId = user.Id,
            Email = user.Email ?? "",
            DisplayName = user.DisplayName,
            Role = user.Role,
            Tier = user.Tier,
            VerifiedCreator = user.VerifiedCreator,
            CreatorTier = user.CreatorTier.ToString(),
            UploadCount = user.UploadCount,
            UploadLimit = tierConfig.UploadLimit,
            SubscriptionStatus = user.SubscriptionStatus,
            SubscriptionEndDate = user.SubscriptionEndDate,
            PlatformFeePercent = tierConfig.FeeRate,
            ContractVersion = TierManifest.ContractVersion
        };
    }

    public async Task<AuthResponse> GetSessionAsync(ClaimsPrincipal principal)
    {
        var profile = await GetCurrentUserAsync(principal);
        var sub = await _subscriptions.GetActiveAsync(profile.UserId);
        var tier = sub?.Plan ?? profile.Tier ?? "free";

        return new AuthResponse
        {
            UserId = Guid.Parse(profile.UserId),
            Email = profile.Email,
            Token = "",
            Tier = tier.ToLowerInvariant()
        };
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request) { /* ... */ }
    public async Task VerifyCodeAsync(VerifyCodeRequest request) { /* ... */ }
    public async Task ResetPasswordAsync(ResetPasswordRequest request) { /* ... */ }
    public async Task RecoverUsernameAsync(RecoverUsernameRequest request) { /* ... */ }
    public async Task ChangePasswordAsync(ClaimsPrincipal principal, ChangePasswordRequest request) { /* ... */ }
    public async Task ChangeEmailAsync(ClaimsPrincipal principal, ChangeEmailRequest request) { /* ... */ }

    private string GenerateJwt(ApplicationUser user)
    {
        var key = _config["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key is not configured.");
        var issuer = _config["Jwt:Issuer"] ?? "cambrian-api";
        var audience = _config["Jwt:Audience"] ?? "cambrian-client";

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new(ClaimTypes.Role, user.Role),
            new("tier", (user.Tier ?? "free").ToLowerInvariant()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<string?> GenerateFreshTokenAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return null;
        return GenerateJwt(user);
    }
}
```

### 1.2 AuthController.cs

```csharp
using System.Security.Claims;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("auth")]
public class AuthController : BaseController
{
    private readonly IAuthService _auth;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService auth, ISubscriptionRepository subscriptions, ILogger<AuthController> logger)
    {
        _auth = auth;
        _subscriptions = subscriptions;
        _logger = logger;
    }

    [EnableRateLimiting("auth")]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        _logger.LogInformation("EVENT: RegisterStarted email:{Email}", request.Email);
        var result = await _auth.RegisterAsync(request);
        _logger.LogInformation("EVENT: RegisterCompleted userId:{UserId} email:{Email} tier:{Tier}", result.UserId, result.Email, result.Tier);
        return CreatedResponse(ToSession(result));
    }

    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        _logger.LogInformation("EVENT: LoginStarted email:{Email}", request.Email);
        var result = await _auth.LoginAsync(request);
        _logger.LogInformation("EVENT: LoginCompleted userId:{UserId} email:{Email} tier:{Tier}", result.UserId, result.Email, result.Tier);
        return OkResponse(ToSession(result));
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var profile = await _auth.GetCurrentUserAsync(User);
        if (profile is null)  // <-- DEAD CODE: GetCurrentUserAsync never returns null
        {
            _logger.LogWarning("EVENT: MeFailed — user not found in database");
            return NotFoundResponse("User not found.");
        }

        var freshToken = await _auth.GenerateFreshTokenAsync(profile.UserId);
        var sub = await _subscriptions.GetActiveAsync(profile.UserId);
        var tier = sub?.Plan ?? profile.Tier ?? "free";

        _logger.LogInformation(
            "EVENT: MeResolved userId:{UserId} email:{Email} profileTier:{ProfileTier} subscriptionPlan:{SubPlan} resolvedTier:{ResolvedTier}",
            profile.UserId, profile.Email, profile.Tier, sub?.Plan, tier.ToLowerInvariant());

        return OkResponse(new
        {
            token = freshToken ?? Request.Headers.Authorization.ToString().Replace("Bearer ", ""),
            user = new
            {
                id = profile.UserId,
                email = profile.Email,
                tier = tier.ToLowerInvariant(),
                role = profile.Role ?? "User",
                creatorTier = profile.CreatorTier,
                uploadCount = profile.UploadCount,
                uploadLimit = profile.UploadLimit,
                subscriptionStatus = profile.SubscriptionStatus,
                subscriptionEndDate = profile.SubscriptionEndDate,
                platformFeePercent = profile.PlatformFeePercent,
                contractVersion = profile.ContractVersion
            }
        });
    }

    private static object ToSession(AuthResponse auth) => new
    {
        token = auth.Token,
        tier = auth.Tier,
        user = new
        {
            id = auth.UserId.ToString(),
            email = auth.Email,
            tier = (auth.Tier ?? "free").ToLowerInvariant(),
            role = auth.Role ?? "User"
        }
    };

    [Authorize]
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        return MessageResponse("Logged out successfully.");
    }

    [HttpGet("health")]
    public IActionResult Health() => OkResponse(new { status = "ok", timestamp = DateTime.UtcNow });

    [HttpGet("csrf-token")]
    public IActionResult GetCsrfToken() => OkResponse(new { token = Guid.NewGuid() });

    [EnableRateLimiting("auth")]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request) { /* ... */ }

    [EnableRateLimiting("auth")]
    [HttpPost("verify-code")]
    public async Task<IActionResult> VerifyCode(VerifyCodeRequest request) { /* ... */ }

    [EnableRateLimiting("auth")]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request) { /* ... */ }

    [HttpPost("recover-username")]
    public async Task<IActionResult> RecoverUsername(RecoverUsernameRequest request) { /* ... */ }

    [Authorize]
    [HttpGet("/settings/profile")]
    public async Task<IActionResult> GetProfile() { /* ... */ }

    [Authorize]
    [HttpPost("/settings/password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request) { /* ... */ }

    [Authorize]
    [HttpPut("/settings/password")]
    public Task<IActionResult> UpdatePassword([FromBody] ChangePasswordRequest request) => ChangePassword(request);

    [Authorize]
    [HttpPost("/settings/email")]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest request) { /* ... */ }

    [Authorize]
    [HttpPut("/settings/email")]
    public Task<IActionResult> UpdateEmail([FromBody] ChangeEmailRequest request) => ChangeEmail(request);
}
```

### 1.3 CambrianDbContext.cs

```csharp
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Cambrian.Persistence;

public class CambrianDbContext : IdentityDbContext<ApplicationUser>
{
    public CambrianDbContext(DbContextOptions<CambrianDbContext> options) : base(options) { }

    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<LibraryItem> Library => Set<LibraryItem>();
    public DbSet<Payout> Payouts => Set<Payout>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<AbuseReport> AbuseReports => Set<AbuseReport>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<StripeWebhookEvent> StripeWebhookEvents => Set<StripeWebhookEvent>();
    public DbSet<StreamSession> StreamSessions => Set<StreamSession>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<LicenseCertificate> LicenseCertificates => Set<LicenseCertificate>();
    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<CreatorProfile> CreatorProfiles => Set<CreatorProfile>();
    public DbSet<TrackCollection> TrackCollections => Set<TrackCollection>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Track>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Title).HasMaxLength(200).IsRequired();
            e.Property(t => t.CambrianTrackId).HasMaxLength(25).IsRequired();
            e.HasIndex(t => t.CambrianTrackId).IsUnique();
            e.Property(t => t.Visibility).HasMaxLength(20).HasDefaultValue("public");
            e.Property(t => t.Status).HasMaxLength(30).HasDefaultValue("available");
            e.Property(t => t.Mood).HasMaxLength(50);
            e.Property(t => t.Tempo).HasMaxLength(30);
            e.HasOne(t => t.Creator)
                .WithMany(u => u.Tracks)
                .HasForeignKey(t => t.CreatorId)
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(t => t.Tags)
                .HasConversion(
                    v => string.Join(',', v),
                    v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(new ValueComparer<ICollection<string>>(
                    (c1, c2) => c1!.SequenceEqual(c2!),
                    c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                    c => c.ToList()));
        });

        builder.Entity<Purchase>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.UsageType).HasMaxLength(30).HasDefaultValue("personal");
            e.Property(p => p.StripeSessionId).HasMaxLength(255);
            e.HasIndex(p => p.StripeSessionId).IsUnique().HasFilter("\"StripeSessionId\" IS NOT NULL");
            e.HasOne(p => p.License).WithOne().HasForeignKey<Purchase>(p => p.LicenseId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(p => p.Buyer).WithMany(u => u.Purchases).HasForeignKey(p => p.BuyerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.Track).WithMany(t => t.Purchases).HasForeignKey(p => p.TrackId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<LibraryItem>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => new { l.UserId, l.TrackId }).IsUnique();
            e.HasOne(l => l.User).WithMany(u => u.Library).HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Track).WithMany(t => t.LibraryItems).HasForeignKey(l => l.TrackId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Purchase).WithMany().HasForeignKey(l => l.PurchaseId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Payout>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasOne(p => p.Creator).WithMany(u => u.Payouts).HasForeignKey(p => p.CreatorId).OnDelete(DeleteBehavior.Restrict);
        });

        // AbuseReport, AuditLog, Subscription, StripeWebhookEvent, Invoice,
        // StreamSession, WalletTransaction, LicenseCertificate, AnalyticsEvent,
        // FeatureFlag, CreatorProfile, TrackCollection — all configured with
        // HasKey, property constraints, and indexes.
        // (See full source for details.)
    }
}
```

---

## 2. ISSUES FOUND — BY CATEGORY

### 2.1 Auth Flow Correctness

| # | Severity | File | Issue |
|---|----------|------|-------|
| A1 | **HIGH** | `AuthService.GetSessionAsync` | Returns `AuthResponse` with `Token = ""` and **missing `Role`** field. Frontend may break if it relies on a token or role from this endpoint. |
| A2 | **MEDIUM** | `AuthController.Me` (line 51) | Dead code: `if (profile is null)` — `GetCurrentUserAsync` **never returns null**, it throws `UnauthorizedAccessException`. The null check and `NotFoundResponse` are unreachable. |
| A3 | **MEDIUM** | `AuthController.Logout` | JWT is **stateless** — logout returns a success message but the token remains valid for up to 24 hours. No token blacklisting or revocation mechanism exists. |
| A4 | **LOW** | `AuthController.GetCsrfToken` | Returns a random GUID but **never validates it**. Since JWT bearer auth is used (not cookies), CSRF protection is unnecessary, making this endpoint misleading. |
| A5 | **LOW** | `AuthService.ForgotPasswordAsync` | Accepts `PhoneNumber` in the DTO but **only supports email lookup**. Phone-based reset is advertised but not implemented. |

### 2.2 JWT Token Generation & Validation

| # | Severity | File | Issue |
|---|----------|------|-------|
| J1 | **MEDIUM** | `AuthService.GenerateJwt` | Token expires in 24 hours with no refresh token mechanism. Long-lived JWTs increase the window for stolen token exploitation. |
| J2 | **LOW** | `AuthService.GenerateJwt` | The `tier` claim embeds the current tier at login time. If a user upgrades mid-session, the JWT claim is stale until `GET /auth/me` re-issues a token. This is partially mitigated by `RequireCreatorTierAttribute`'s DB fallback. |
| J3 | **LOW** | `AuthService.LoginAsync` / `RegisterAsync` | `Guid.Parse(user.Id)` — safe for default Identity GUID IDs but would throw `FormatException` if custom ID generation is used. |

### 2.3 Role-Based Access Control

| # | Severity | File | Issue |
|---|----------|------|-------|
| R1 | **HIGH** | `ExceptionMiddleware` | `UnauthorizedAccessException` maps to HTTP **403 Forbidden** instead of **401 Unauthorized**. Login failures (invalid credentials) return 403 instead of 401, which is semantically incorrect. 403 means "authenticated but not authorized"; 401 means "not authenticated." |
| R2 | **MEDIUM** | `CatalogController` | **No authorization on any endpoint** — all catalog, track detail, and trending endpoints are fully public. `GetTrack` exposes detailed pricing (non-exclusive, exclusive, copyright buyout prices, platform fees, creator earnings). No rate limiting either. |
| R3 | **MEDIUM** | `AnalyticsController.RecordEvent` | Requires `[Authorize]` but `User.FindFirstValue(ClaimTypes.NameIdentifier)` can return null if the claim is missing from a valid JWT. This null `userId` is silently stored in the analytics event. |
| R4 | **LOW** | `AdminController` | Role check via `[Authorize(Roles = "Admin")]` correctly uses the `ClaimTypes.Role` claim from JWT. Working correctly. |

### 2.4 Missing Authorization on Endpoints

| # | Severity | File | Issue |
|---|----------|------|-------|
| M1 | **MEDIUM** | `CatalogController` | `/discover`, `/catalog`, `/tracks/{trackId}`, `/trending`, `/tracks` — all fully public, no rate limiting. Consider adding rate limiting to prevent scraping. |
| M2 | **LOW** | `CreatorProfileController.GetBySlug` | Public endpoint exposes `ShowEarnings` and `ShowDownloadStats` toggle values and full social links. Profile data exposure may be intentional but should be reviewed for PII. |
| M3 | **LOW** | `AuthController.RecoverUsername` | No rate limiting (`[EnableRateLimiting("auth")]` is missing). Could be used for email enumeration even though the response is generic — timing attacks are possible. |

### 2.5 DTO Mapping Issues

| # | Severity | File | Issue |
|---|----------|------|-------|
| D1 | **HIGH** | `AuthResponse` → `ToSession()` in `AuthController` | `ToSession()` maps `auth.Tier` at the top-level **and** nested inside `user.tier`. The session response duplicates `tier` at two levels with potentially different casing (`auth.Tier` raw vs `(auth.Tier ?? "free").ToLowerInvariant()`). |
| D2 | **MEDIUM** | `UserProfileResponse.DisplayName` | Property type is `string` (non-nullable) but `ApplicationUser.DisplayName` is `string?` (nullable). If `DisplayName` is null, the DTO receives null for a non-nullable string property — no compiler warning due to NRT limitations with runtime assignment. |
| D3 | **MEDIUM** | `AuthResponse` | `UserId` is `Guid` type, but `UserProfileResponse.UserId` is `string` type. Inconsistent ID typing across DTOs. The `/auth/me` endpoint returns `id` as a string (from `UserProfileResponse`), while `/auth/login` and `/auth/register` return `id` as `UserId.ToString()` (Guid serialized). |
| D4 | **MEDIUM** | `TrackResponse` | Missing `Mood`, `Tempo`, `Instrumental`, and `Tags` fields. These are available on the `Track` entity and in search filters, but never returned to the frontend. |
| D5 | **LOW** | `AdminDashboardSummary` | `TotalUsers`, `ActiveCreators`, `TracksUploaded`, `LicensesSold` are typed as `double` but represent integer counts. Should be `int` or `long`. |

### 2.6 Entity Relationships & FK Issues

| # | Severity | File | Issue |
|---|----------|------|-------|
| E1 | **HIGH** | `CreatorProfile` | **No FK constraint** from `CreatorProfile.UserId` to `AspNetUsers.Id`. Only a unique index exists. Deleting a user leaves orphaned creator profiles with no cascade or restrict behavior. |
| E2 | **HIGH** | `TrackCollection` | **No FK constraint** from `TrackCollection.CreatorId` to `AspNetUsers.Id`. Same orphan risk as E1. |
| E3 | **HIGH** | `AnalyticsEvent` | **No FK constraints** for `UserId` or `TrackId`. Analytics events can reference deleted users and tracks with no cleanup. |
| E4 | **MEDIUM** | `CreatorBalance` entity | **Orphaned entity** — not registered in `CambrianDbContext`, has no `DbSet`, no configuration, no repository. `CreatorId` is `Guid` but user IDs are strings. Dead code. |
| E5 | **MEDIUM** | `User` entity (Domain) | **Orphaned entity** — uses `Guid Id` and `UserRole` enum but is not connected to any DbSet, repository, or service. `ApplicationUser` (with string ID) is the actual user entity used everywhere. `User` appears to be legacy/dead code. |
| E6 | **MEDIUM** | `TrackFile`, `ModerationAction`, `Payment` entities | Exist in `Domain/Entities/` but have **no DbSet, no configuration, no repository**. Orphaned entities that may cause confusion. |
| E7 | **LOW** | `License` entity | Referenced by `Purchase.LicenseId` FK but the `License` entity is separate from `LicenseCertificate`. The FK configuration maps `Purchase.License` → `LicenseCertificate`, so the naming is misleading. |

### 2.7 Repository Query Correctness

| # | Severity | File | Issue |
|---|----------|------|-------|
| Q1 | **CRITICAL** | `CreatorProfileRepository.GetByUserIdAsync` | Loads **ALL profiles into memory** with `ToListAsync()` then filters with a `foreach` loop. Should use `FirstOrDefaultAsync(p => p.UserId == userId)`. O(n) memory and time instead of O(1). |
| Q2 | **CRITICAL** | `CreatorProfileRepository.GetBySlugAsync` | Same anti-pattern — `ToListAsync()` + manual iteration instead of `FirstOrDefaultAsync()`. |
| Q3 | **CRITICAL** | `CreatorProfileRepository.UpsertAsync` | Loads **ALL profiles** to find one by userId. Same O(n) issue. |
| Q4 | **CRITICAL** | `CreatorProfileRepository.UpdateImageAsync` | Same `ToListAsync()` + manual loop pattern. |
| Q5 | **CRITICAL** | `CreatorProfileRepository.UpdatePinnedTracksAsync` | Same pattern. |
| Q6 | **CRITICAL** | `CreatorProfileRepository.GetCollectionsAsync` | Loads **ALL collections** then filters by `CreatorId`. |
| Q7 | **MEDIUM** | `AdminRepository.GetDashboardStatsAsync` | `var completedPurchases = await _db.Purchases.Where(p => p.Status == "completed").ToListAsync()` loads **all completed purchases into memory** just to count them and sum amounts. Should use `CountAsync()` and `SumAsync()` directly. |
| Q8 | **LOW** | `TrackRepository.BrowseAsync` | Uses `t.Genre.ToLower()` and `t.Title.ToLower().Contains(search.ToLower())` — may not translate to efficient SQL on all providers. Consider `EF.Functions.ILike` for PostgreSQL. |

### 2.8 Missing Error Handling

| # | Severity | File | Issue |
|---|----------|------|-------|
| H1 | **MEDIUM** | `CatalogController.Discover` / `Catalog` | `result!.Items` uses null-forgiving operator. If `GetOrCreateAsync` returns null (cache miss + factory returns null), this will throw `NullReferenceException`. |
| H2 | **MEDIUM** | `CreatorProfileController.UploadImage` | Returns `null` for three different failure conditions (no file, too large, wrong extension) but the caller shows the same generic "Invalid image file." error. Users can't diagnose the problem. |
| H3 | **LOW** | `AuthService.ForgotPasswordAsync` | `catch (Exception)` block silently swallows email sending failures. The code is saved, but there's no logging of the failure. Should at minimum log the exception. |
| H4 | **LOW** | `CreatorProfileRepository.MapToDto` | `catch { /* ignore malformed JSON */ }` silently swallows JSON deserialization errors for social links. Returns null links for corrupted data with no logging. |

### 2.9 Potential Null Reference Crashes

| # | Severity | File | Issue |
|---|----------|------|-------|
| N1 | **MEDIUM** | `AuthController.Me` (line 69) | `Request.Headers.Authorization.ToString().Replace("Bearer ", "")` — if `Authorization` header is missing or malformed (e.g. when using cookie auth), `ToString()` returns empty string, and `Replace` produces an empty token. Not a crash but returns invalid data. |
| N2 | **MEDIUM** | `CreatorProfileController` (lines 67, 81, 115, 129, etc.) | `User.FindFirstValue(ClaimTypes.NameIdentifier)!` — null-forgiving operator used widely. If the claim is missing from a valid JWT (e.g. after token format change), this will throw `NullReferenceException`. The `[Authorize]` filter guarantees authentication but not the presence of specific claims. |
| N3 | **LOW** | `LibraryController.GetLibrary` (line 26) | `var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)` — could be null, but used only for logging. Not a crash risk. |

### 2.10 CreatorTier-Type Issues (Undefined Fields / Enum Mismatches)

| # | Severity | File | Issue |
|---|----------|------|-------|
| T1 | **HIGH** | `ApplicationUser` | **Dual tier system** — `Tier` (string: "free", "paid", "creator", "pro") and `CreatorTier` (enum: `Free`, `Pro`) coexist and can become **desynchronized**. No code enforces consistency between them. A user can have `Tier = "creator"` with `CreatorTier = Pro` or `Tier = "pro"` with `CreatorTier = Free`. |
| T2 | **HIGH** | `RegisterAsync` | All new creators get `CreatorTier = CreatorTier.Free` regardless of any other setting. **No code path** upgrades `CreatorTier` from `Free` to `Pro` when a creator subscribes to the Pro plan (subscription only updates `Tier` string). `TierManifest.For(user.CreatorTier)` will always return `Free` config for users who upgraded via subscription but didn't have `CreatorTier` explicitly set to `Pro`. |
| T3 | **MEDIUM** | `UserRole` enum | Values `Listener=1`, `Creator=2`, `Admin=3` don't match the string conventions used in `ApplicationUser.Role` (`"User"`, `"Creator"`, `"Admin"`). Note `Listener` vs `User` mismatch. This enum is only used by the orphaned `User` entity and is effectively dead code. |
| T4 | **MEDIUM** | `RequireCreatorTierAttribute` | Checks `tier == "creator" || tier == "pro"` on the string `Tier` field. But `TierManifest` resolves config from the `CreatorTier` **enum**. If the string is "creator" but enum is `Pro`, the manifest returns Pro config but the string check also passes. Opposite case: string "free" + enum `Pro` fails the middleware check despite having Pro enum. |

### 2.11 Empty State Handling

| # | Severity | File | Issue |
|---|----------|------|-------|
| S1 | **LOW** | `AdminController.PayoutRequests` | Always returns `Array.Empty<object>()`. No actual payout listing from DB. |
| S2 | **LOW** | `AdminController.Reports` | Always returns `Array.Empty<object>()`. No actual report listing. |
| S3 | **LOW** | `AdminController.UpdateSettings` / `ResetUserPassword` | **No-op endpoints** — return success without persisting anything. |
| S4 | **LOW** | `AdminController.FeatureTrack` / `PinTrack` / `CurateCollection` / `ManageTags` | **No-op endpoints** — return success messages without any persistence. |
| S5 | **LOW** | `CreatorProfileRepository.MapToDto` | `Stats` always returns `TotalDownloads = 0, TotalEarnings = 0`. No actual stats aggregation from purchases/analytics. |

### 2.12 Data Integrity — Admin Purge Gap

| # | Severity | File | Issue |
|---|----------|------|-------|
| P1 | **MEDIUM** | `AdminRepository.PurgeTestDataAsync` | Does **not delete** from `AnalyticsEvents`, `FeatureFlags`, `CreatorProfiles`, or `TrackCollections` tables. These tables were added after the purge function was written. Purge leaves stale data in newer tables. |

---

## 3. RISK SUMMARY

### Critical (Fix Immediately)
- **Q1–Q6**: `CreatorProfileRepository` loads ALL rows into memory for every query. Will cause out-of-memory crashes as data grows.

### High (Fix Before Next Release)
- **T1–T2**: `Tier` string vs `CreatorTier` enum desynchronization means creator subscription upgrades don't actually change feature limits/fee rates.
- **R1**: Login failures return 403 instead of 401.
- **E1–E3**: Missing FK constraints on `CreatorProfile`, `TrackCollection`, `AnalyticsEvent` — orphaned data on user/track deletion.
- **A1**: `GetSessionAsync` returns empty token and missing role.
- **D1**: Duplicate/inconsistent tier in session response.

### Medium (Plan for Next Sprint)
- **Q7**: Dashboard loads all purchases into memory.
- **H1**: Null-forgiving cache results in CatalogController.
- **D2–D4**: DTO mapping gaps (nullable strings, missing fields).
- **N2**: Null-forgiving claim access in CreatorProfileController.
- **M1–M3**: Missing rate limiting and authorization gaps.
- **P1**: Purge function doesn't cover newer tables.

### Low (Backlog)
- **A3–A5**: JWT stateless logout, fake CSRF, missing phone reset.
- **J1–J2**: No refresh token, stale tier claims.
- **S1–S5**: No-op admin endpoints, empty stats.
- **E4–E7**: Orphaned entities and misleading naming.
