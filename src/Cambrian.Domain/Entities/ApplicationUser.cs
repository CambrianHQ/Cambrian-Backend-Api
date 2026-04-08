using Cambrian.Domain.Enums;
using Microsoft.AspNetCore.Identity;

namespace Cambrian.Domain.Entities;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }

    public string Role { get; set; } = "User"; // User, Admin

    public string Status { get; set; } = "active"; // active, suspended

    public string Tier { get; set; } = "free"; // free, paid, creator, pro

    public bool VerifiedCreator { get; set; }

    public string? Plan { get; set; }

    /// <summary>Creator-specific tier (Free or Pro). Only meaningful when Role includes Creator access.</summary>
    public CreatorTier CreatorTier { get; set; } = CreatorTier.Free;

    /// <summary>Number of tracks this creator has uploaded (denormalized for fast limit checks).</summary>
    public int UploadCount { get; set; }

    /// <summary>Creator subscription status: Active, Inactive, Cancelled.</summary>
    public string SubscriptionStatus { get; set; } = "Inactive";

    /// <summary>When the current creator Pro subscription expires (null if free or no subscription).</summary>
    public DateTime? SubscriptionEndDate { get; set; }

    /// <summary>Stripe Connect Express account ID (e.g. acct_xxx). Null if not connected.</summary>
    public string? StripeAccountId { get; set; }

    public long WalletBalanceCents { get; set; }

    /// <summary>Hashed 8-character alphanumeric code for password reset (null = no pending reset).</summary>
    public string? PasswordResetCode { get; set; }

    /// <summary>UTC expiry of the current password reset code.</summary>
    public DateTime? PasswordResetCodeExpiry { get; set; }

    /// <summary>
    /// Consecutive failed code-verification attempts for the current reset code.
    /// Reset to 0 when a new code is issued or a correct code is accepted.
    /// </summary>
    public int PasswordResetAttemptCount { get; set; }

    /// <summary>
    /// When set, reset code verification is locked out until this UTC time.
    /// Null = no active lockout.
    /// </summary>
    public DateTime? PasswordResetLockedUntil { get; set; }

    /// <summary>User's profile/avatar image URL (stored in object storage).</summary>
    public string? ProfileImageUrl { get; set; }

    /// <summary>User's cover/banner image URL (stored in object storage).</summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>Short user bio / tagline. Max 500 characters.</summary>
    public string? Bio { get; set; }

    /// <summary>Google OAuth subject identifier (links Google account to this user).</summary>
    public string? GoogleId { get; set; }

    /// <summary>Primary auth provider: "Local", "Google", etc.</summary>
    public string? AuthProvider { get; set; }

    /// <summary>
    /// True when the user's email address has been verified.
    /// Google-registered accounts are pre-verified. Local-registration accounts
    /// start as false until the verification email is confirmed.
    /// Existing accounts default to true via migration (backward-compatible).
    /// </summary>
    public bool EmailVerified { get; set; }

    // ── Email change (two-step verification) ──

    /// <summary>
    /// New email address awaiting verification. Null when no change is pending.
    /// The live Email field is NOT changed until the verification token is confirmed.
    /// </summary>
    public string? PendingEmail { get; set; }

    /// <summary>
    /// SHA-256 hash (64 hex chars) of the email-change verification token.
    /// Null when no change is pending. Cleared after successful verification.
    /// </summary>
    public string? EmailChangeToken { get; set; }

    /// <summary>UTC expiry of the email change token (24 hours from issuance).</summary>
    public DateTime? EmailChangeTokenExpiry { get; set; }

    // ── Initial email verification (separate from email-change above) ──

    /// <summary>
    /// SHA-256 hash (64 hex chars) of the initial email verification token sent on
    /// registration. Null when no verification is pending. Cleared after successful
    /// verification.
    /// </summary>
    public string? EmailVerificationToken { get; set; }

    /// <summary>UTC expiry of the email verification token (24 hours from issuance).</summary>
    public DateTime? EmailVerificationTokenExpiry { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Track> Tracks { get; set; } = new List<Track>();

    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();

    public ICollection<LibraryItem> Library { get; set; } = new List<LibraryItem>();

    public ICollection<Payout> Payouts { get; set; } = new List<Payout>();
}