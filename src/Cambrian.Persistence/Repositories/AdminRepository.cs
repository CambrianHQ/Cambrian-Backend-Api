using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Constants;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Cambrian.Persistence.Repositories;

public class AdminRepository : IAdminRepository
{
    private static readonly HashSet<string> AllowedVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "hidden", "limited"
    };

    private readonly CambrianDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IEmailService _email;
    private readonly IPaymentGateway _gateway;
    private readonly IFeatureFlagRepository _flags;

    public AdminRepository(
        CambrianDbContext db,
        UserManager<ApplicationUser> users,
        IEmailService email,
        IPaymentGateway gateway,
        IFeatureFlagRepository flags)
    {
        _db = db;
        _users = users;
        _email = email;
        _gateway = gateway;
        _flags = flags;
    }

    public async Task<AdminDashboardSummary> GetDashboardStatsAsync()
    {
        var totalUsers = await _users.Users.CountAsync();
        var activeCreators = await _users.Users.CountAsync(u => u.Role == "Creator" || u.VerifiedCreator);
        var tracksUploaded = await _db.Tracks.CountAsync();
        var licensesSold = await _db.Purchases.CountAsync(p => p.Status == PurchaseStatuses.Completed);
        var totalRevenue = await _db.Purchases.Where(p => p.Status == PurchaseStatuses.Completed).SumAsync(p => (double)p.AmountCents) / 100.0;
        var pendingPayouts = await _db.Payouts.Where(p => p.Status == "pending").SumAsync(p => p.AmountCents) / 100.0;

        return new AdminDashboardSummary
        {
            TotalUsers = totalUsers,
            ActiveCreators = activeCreators,
            TracksUploaded = tracksUploaded,
            LicensesSold = licensesSold,
            TotalRevenue = totalRevenue,
            PendingPayouts = pendingPayouts
        };
    }

    public async Task<IReadOnlyCollection<AdminAuditLog>> GetAuditLogsAsync(int take = 200)
    {
        var logs = await _db.AuditLogs
            .OrderByDescending(a => a.Timestamp)
            .Take(take)
            .ToListAsync();

        return logs.Select(a => new AdminAuditLog
        {
            Id = a.Id.ToString(),
            Action = a.Action,
            Admin = a.Admin,
            Timestamp = a.Timestamp,
            Details = a.Details
        }).ToList();
    }

    public async Task<IReadOnlyCollection<AdminUser>> GetUsersAsync(int take = 500)
    {
        var users = await _users.Users
            .OrderByDescending(u => u.CreatedAt)
            .Take(take)
            .ToListAsync();

        return users.Select(u => new AdminUser
        {
            Id = u.Id,
            Email = u.Email ?? "",
            DisplayName = u.DisplayName,
            Role = u.Role,
            Status = u.Status,
            Tier = u.Tier,
            VerifiedCreator = u.VerifiedCreator,
            CreatorTier = u.CreatorTier.ToString(),
            UploadCount = u.UploadCount,
            CreatedAt = u.CreatedAt
        }).ToList();
    }

    /// <summary>
    /// Delete all transactional data and non-admin users. FK-safe order.
    /// Uses raw SQL to avoid EF Core circular-dependency tracking issues
    /// (LicenseCertificate ↔ Purchase).
    /// Wrapped in a transaction to ensure atomicity.
    /// </summary>
    public async Task<PurgeResult> PurgeTestDataAsync(string adminEmail)
    {
        var result = new PurgeResult { AdminPreserved = adminEmail };

        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Break the circular FK first: Purchase.LicenseId → LicenseCertificate
        await _db.Database.ExecuteSqlRawAsync("UPDATE \"Purchases\" SET \"LicenseId\" = NULL");

        // Delete in FK-safe order using raw SQL counts
        result.LicenseCertificatesDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"LicenseCertificates\"");
        result.StreamSessionsDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"StreamSessions\"");
        result.WalletTransactionsDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"WalletTransactions\"");
        result.WebhookEventsDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"StripeWebhookEvents\"");
        result.AuditLogsDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditLogs\"");
        result.AbuseReportsDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"AbuseReports\"");
        result.SubscriptionsDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"Subscriptions\"");
        result.LibraryItemsDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"Library\"");
        result.InvoicesDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"Invoices\"");
        result.PayoutsDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"Payouts\"");
        result.PurchasesDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"Purchases\"");
        result.TracksDeleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"Tracks\"");

        // Delete non-admin users (Identity tables: roles, claims, tokens, logins first)
        var nonAdminUsers = await _users.Users
            .Where(u => u.Email != adminEmail)
            .ToListAsync();
        var nonAdminIds = nonAdminUsers.Select(u => u.Id).ToList();

        if (nonAdminIds.Count > 0)
        {
            // Clean up Identity join tables for non-admin users
            foreach (var uid in nonAdminIds)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" = {uid}");
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM \"AspNetUserClaims\" WHERE \"UserId\" = {uid}");
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM \"AspNetUserTokens\" WHERE \"UserId\" = {uid}");
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM \"AspNetUserLogins\" WHERE \"UserId\" = {uid}");
            }

            // Now delete the user rows from AspNetUsers / Users
            foreach (var u in nonAdminUsers)
                _db.Users.Remove(u);
        }
        result.UsersDeleted = nonAdminUsers.Count;
        await _db.SaveChangesAsync();

        await transaction.CommitAsync();

        return result;
    }

    // ══════════════════════════════════════════════
    // User management
    // ══════════════════════════════════════════════

    public async Task<bool> SuspendUserAsync(string userId, string? reason, string adminActor)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return false;
        user.Status = "suspended";
        await _users.UpdateAsync(user);

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "suspend_user",
            Admin = adminActor,
            Details = $"Suspended user {user.Email}. Reason: {reason ?? "N/A"}"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ReactivateUserAsync(string userId, string adminActor)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return false;
        user.Status = "active";
        await _users.UpdateAsync(user);

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "reactivate_user",
            Admin = adminActor,
            Details = $"Reactivated user {user.Email}"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetUserRoleAsync(string userId, string role, string adminActor)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return false;

        // Guard: promoting to Creator requires a username (set via onboarding)
        if (string.Equals(role, "Creator", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(user.UserName)
                || string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Cannot promote to Creator: user has not completed username onboarding.");
        }

        var oldRole = user.Role;
        user.Role = role;
        await _users.UpdateAsync(user);

        // Ensure Creator table row exists when promoting to Creator
        if (string.Equals(role, "Creator", StringComparison.OrdinalIgnoreCase))
            await EnsureCreatorRowAsync(user);

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "change_user_role",
            Admin = adminActor,
            Details = $"Changed role for {user.Email} from {oldRole} to {role}"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> VerifyCreatorAsync(string userId, string adminActor)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return false;
        user.VerifiedCreator = true;
        user.Role = "Creator";
        user.CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro;
        user.Tier = "pro";
        user.SubscriptionStatus = "Active";
        user.SubscriptionEndDate = null; // manual grant — no expiry
        await _users.UpdateAsync(user);

        await EnsureCreatorRowAsync(user);

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "verify_creator",
            Admin = adminActor,
            Details = $"Verified creator {user.Email}"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpgradeCreatorTierAsync(string userId, string tier, string adminActor)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return false;
        var oldTier = user.CreatorTier;
        if (tier.Equals("pro", StringComparison.OrdinalIgnoreCase))
        {
            user.CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro;
            user.Tier = "pro";
            user.SubscriptionStatus = "Active";
            user.SubscriptionEndDate = null; // manual grant — no expiry
        }
        else
        {
            user.CreatorTier = Cambrian.Domain.Enums.CreatorTier.Free;
            user.Tier = "free";
            user.SubscriptionStatus = "Inactive";
            user.SubscriptionEndDate = null;
        }
        await _users.UpdateAsync(user);

        // Pro upgrade implies creator status — ensure Creator row + role
        if (tier.Equals("pro", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(user.Role, "Creator", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                user.Role = "Creator";
                await _users.UpdateAsync(user);
            }
            await EnsureCreatorRowAsync(user);
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "upgrade_creator_tier",
            Admin = adminActor,
            Details = $"Changed creator tier for {user.Email} from {oldTier} to {tier}"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Ensure a Creator table row exists for the given user.
    /// Uses ApplicationUser.UserName as the creator username.
    /// No-op if a row already exists or the user has no username set.
    /// </summary>
    private async Task EnsureCreatorRowAsync(ApplicationUser user)
    {
        var exists = await _db.Creators.AnyAsync(c => c.UserId == user.Id);
        if (exists) return;

        // Derive username: prefer UserName if it's not the email address
        var username = user.UserName;
        if (string.IsNullOrWhiteSpace(username)
            || string.Equals(username, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            // No usable username — skip Creator row creation.
            // The user will be prompted to set one on next login.
            return;
        }

        _db.Creators.Add(new Creator
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Username = username.Trim().ToLowerInvariant(),
            DisplayName = user.DisplayName ?? username,
            Bio = "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    public async Task<string?> ResetUserPasswordAsync(string userId, string adminActor)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return null;

        // Generate a cryptographically random 12-char temp password
        var tempPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9));
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, tempPassword);
        if (!result.Succeeded) return null;

        // Send the temporary password to the user's email
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            await _email.SendAsync(
                user.Email,
                "Your password has been reset",
                $"<p>An administrator has reset your password.</p><p>Your temporary password is: <strong>{tempPassword}</strong></p><p>Please log in and change your password immediately.</p>");
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "reset_user_password",
            Admin = adminActor,
            Details = $"Admin reset password for {user.Email}"
        });
        await _db.SaveChangesAsync();
        return tempPassword;
    }

    // ══════════════════════════════════════════════
    // List queries
    // ══════════════════════════════════════════════

    public async Task<IReadOnlyCollection<AdminTrack>> GetTracksAsync(int take = 500)
    {
        var tracks = await _db.Tracks
            .OrderByDescending(t => t.CreatedAt)
            .Take(take)
            .ToListAsync();

        var creatorIds = tracks.Select(t => t.CreatorId).Distinct().ToList();
        var creators = await _users.Users
            .Where(u => creatorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email })
            .ToDictionaryAsync(u => u.Id, u => u.Email);

        return tracks.Select(t => new AdminTrack
        {
            Id = t.Id.ToString(),
            Title = t.Title,
            Genre = t.Genre,
            CreatorId = t.CreatorId,
            CreatorEmail = creators.GetValueOrDefault(t.CreatorId),
            Status = t.Status,
            Visibility = t.Visibility,
            NonExclusivePriceCents = t.NonExclusivePriceCents,
            ExclusivePriceCents = t.ExclusivePriceCents,
            CopyrightBuyoutPriceCents = t.CopyrightBuyoutPriceCents,
            CreatedAt = t.CreatedAt,
            IsFeatured = t.IsFeatured,
            FeaturedAt = t.FeaturedAt,
            IsPinned = t.IsPinned,
            PinnedAt = t.PinnedAt
        }).ToList();
    }

    public async Task<IReadOnlyCollection<AdminPurchase>> GetPurchasesAsync(int take = 500)
    {
        var purchases = await _db.Purchases
            .Include(p => p.Buyer)
            .Include(p => p.Track)
            .OrderByDescending(p => p.CreatedAt)
            .Take(take)
            .ToListAsync();

        return purchases.Select(p => new AdminPurchase
        {
            Id = p.Id.ToString(),
            BuyerId = p.BuyerId,
            BuyerEmail = p.Buyer?.Email,
            TrackId = p.TrackId.ToString(),
            TrackTitle = p.Track?.Title,
            AmountCents = p.AmountCents,
            LicenseType = p.LicenseType,
            Status = p.Status,
            CompletedAt = p.CompletedAt,
            CreatedAt = p.CreatedAt
        }).ToList();
    }

    public async Task<IReadOnlyCollection<AdminPayout>> GetPayoutsAsync(int take = 500)
    {
        var payouts = await _db.Payouts
            .Include(p => p.Creator)
            .OrderByDescending(p => p.RequestedAt)
            .Take(take)
            .ToListAsync();

        return payouts.Select(MapPayout).ToList();
    }

    private static AdminPayout MapPayout(Payout p) => new()
    {
        Id = p.Id.ToString(),
        CreatorId = p.CreatorId,
        CreatorEmail = p.Creator?.Email,
        AmountCents = p.AmountCents,
        Status = p.Status,
        RequestedAt = p.RequestedAt,
        CompletedAt = p.CompletedAt,
        FailureReason = p.FailureReason,
        ReviewedByUserId = p.ReviewedByUserId,
        ReviewedAt = p.ReviewedAt,
        RejectionReason = p.RejectionReason
    };

    // ══════════════════════════════════════════════
    // Track moderation
    // ══════════════════════════════════════════════

    public async Task<bool> RemoveTrackAsync(Guid trackId, string adminActor)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;
        track.Visibility = "hidden";
        track.Status = "removed";

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "remove_track",
            Admin = adminActor,
            Details = $"Removed track '{track.Title}' (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RestoreTrackAsync(Guid trackId, string adminActor)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;
        track.Visibility = "public";
        track.Status = "available";

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "restore_track",
            Admin = adminActor,
            Details = $"Restored track '{track.Title}' (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> HideTrackAsync(Guid trackId, string adminActor)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;
        track.Visibility = "hidden";

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "hide_track",
            Admin = adminActor,
            Details = $"Hidden track '{track.Title}' (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> FlagTrackAsync(Guid trackId, string adminActor)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;
        track.Status = "flagged";

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "flag_track",
            Admin = adminActor,
            Details = $"Flagged track '{track.Title}' (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetTrackVisibilityAsync(Guid trackId, string visibility, string adminActor)
    {
        if (!AllowedVisibilities.Contains(visibility)) return false;

        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;
        track.Visibility = visibility;

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "set_track_visibility",
            Admin = adminActor,
            Details = $"Set visibility of '{track.Title}' to {visibility} (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    // ══════════════════════════════════════════════
    // Track editorial placement (idempotent, one-way)
    // ══════════════════════════════════════════════

    public async Task<bool> FeatureTrackAsync(Guid trackId, string adminActor)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;

        if (!track.IsFeatured)
        {
            track.IsFeatured = true;
            track.FeaturedAt = DateTime.UtcNow;
            track.FeaturedByUserId = adminActor;

            _db.AuditLogs.Add(new AuditLog
            {
                Action = "feature_track",
                Admin = adminActor,
                Details = $"Featured track '{track.Title}' (id={trackId})"
            });
            await _db.SaveChangesAsync();
        }
        return true;
    }

    public async Task<bool> PinTrackAsync(Guid trackId, string adminActor)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;

        if (!track.IsPinned)
        {
            track.IsPinned = true;
            track.PinnedAt = DateTime.UtcNow;
            track.PinnedByUserId = adminActor;

            _db.AuditLogs.Add(new AuditLog
            {
                Action = "pin_track",
                Admin = adminActor,
                Details = $"Pinned track '{track.Title}' (id={trackId})"
            });
            await _db.SaveChangesAsync();
        }
        return true;
    }

    // ══════════════════════════════════════════════
    // Payout review
    // ══════════════════════════════════════════════

    public async Task<PayoutReviewResult> ApprovePayoutAsync(Guid payoutId, string adminActor)
    {
        var payout = await _db.Payouts.Include(p => p.Creator).FirstOrDefaultAsync(p => p.Id == payoutId);
        if (payout is null)
            return new PayoutReviewResult { Outcome = PayoutReviewOutcome.NotFound, Message = "Payout not found." };

        if (payout.Status != "pending")
            return new PayoutReviewResult
            {
                Outcome = PayoutReviewOutcome.InvalidState,
                Payout = MapPayout(payout),
                Message = $"Payout is '{payout.Status}', not pending — only pending payouts can be approved."
            };

        payout.ReviewedByUserId = adminActor;
        payout.ReviewedAt = DateTime.UtcNow;

        var destinationAccountId = payout.Creator.StripeAccountId;
        if (string.IsNullOrEmpty(destinationAccountId))
        {
            payout.FailureReason = "Creator no longer has a connected Stripe account.";
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "approve_payout_retry_failed",
                Admin = adminActor,
                Details = $"Approve retry for payout {payoutId} failed: {payout.FailureReason}"
            });
            await _db.SaveChangesAsync();
            return new PayoutReviewResult
            {
                Outcome = PayoutReviewOutcome.ApprovalRetryFailed,
                Payout = MapPayout(payout),
                Message = $"Stripe transfer retry failed: {payout.FailureReason} Payout remains pending."
            };
        }

        // Reuse the payout's durable idempotency key — safe to retry even if the original
        // request's transfer actually succeeded silently (Stripe dedupes on this key and
        // returns the prior transfer instead of creating a duplicate).
        payout.StripeIdempotencyKey ??= $"cambrian-payout-{payout.Id:N}";

        try
        {
            var transferId = await _gateway.CreateTransferAsync(
                destinationAccountId,
                payout.AmountCents,
                $"Cambrian payout {payout.Id}",
                payout.StripeIdempotencyKey);

            payout.StripeTransferId = transferId;
            payout.Status = "completed";
            payout.FailureReason = null;
            payout.CompletedAt = DateTime.UtcNow;

            _db.AuditLogs.Add(new AuditLog
            {
                Action = "approve_payout",
                Admin = adminActor,
                Details = $"Approved payout {payoutId} ({payout.AmountCents}c) — Stripe transfer {transferId} succeeded"
            });
            await _db.SaveChangesAsync();

            return new PayoutReviewResult
            {
                Outcome = PayoutReviewOutcome.Approved,
                Payout = MapPayout(payout),
                Message = "Payout approved and the Stripe transfer completed."
            };
        }
        catch (Exception ex)
        {
            payout.FailureReason = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;

            _db.AuditLogs.Add(new AuditLog
            {
                Action = "approve_payout_retry_failed",
                Admin = adminActor,
                Details = $"Approve retry for payout {payoutId} failed: {payout.FailureReason}"
            });
            await _db.SaveChangesAsync();

            return new PayoutReviewResult
            {
                Outcome = PayoutReviewOutcome.ApprovalRetryFailed,
                Payout = MapPayout(payout),
                Message = $"Stripe transfer retry failed: {payout.FailureReason} Payout remains pending."
            };
        }
    }

    public async Task<PayoutReviewResult> RejectPayoutAsync(Guid payoutId, string adminActor, string rejectionReason)
    {
        var payout = await _db.Payouts.Include(p => p.Creator).FirstOrDefaultAsync(p => p.Id == payoutId);
        if (payout is null)
            return new PayoutReviewResult { Outcome = PayoutReviewOutcome.NotFound, Message = "Payout not found." };

        if (payout.Status != "pending")
            return new PayoutReviewResult
            {
                Outcome = PayoutReviewOutcome.InvalidState,
                Payout = MapPayout(payout),
                Message = $"Payout is '{payout.Status}', not pending — only pending payouts can be rejected."
            };

        payout.Status = "rejected";
        payout.RejectionReason = rejectionReason;
        payout.ReviewedByUserId = adminActor;
        payout.ReviewedAt = DateTime.UtcNow;

        // Deliberately NOT crediting the wallet back here: the prior automatic Stripe
        // transfer attempt for this payout may have already succeeded even though it's
        // still "pending" (transport errors can occur after Stripe accepts the transfer —
        // see PayoutService.RequestAsync). Auto-crediting could let a creator double-collect.
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "reject_payout",
            Admin = adminActor,
            Details = $"Rejected payout {payoutId} ({payout.AmountCents}c). Reason: {rejectionReason}. " +
                      "Wallet balance was not automatically restored — verify Stripe transfer status manually."
        });
        await _db.SaveChangesAsync();

        return new PayoutReviewResult
        {
            Outcome = PayoutReviewOutcome.Rejected,
            Payout = MapPayout(payout),
            Message = "Payout rejected. Wallet balance was not automatically adjusted — verify the Stripe transfer status manually before crediting the creator."
        };
    }

    // ══════════════════════════════════════════════
    // Reports / moderation
    // ══════════════════════════════════════════════

    public async Task<IReadOnlyCollection<AdminAbuseReport>> GetReportsAsync(int take = 500)
    {
        var reports = await _db.AbuseReports
            .Include(r => r.Track)
            .OrderByDescending(r => r.ReportedAt)
            .Take(take)
            .ToListAsync();

        return reports.Select(MapReport).ToList();
    }

    private static AdminAbuseReport MapReport(AbuseReport r) => new()
    {
        Id = r.Id.ToString(),
        TargetType = r.TargetType,
        TargetId = r.TargetId,
        TrackId = r.TrackId?.ToString(),
        TrackTitle = r.Track?.Title,
        Reason = r.Reason,
        Details = r.Details,
        Status = r.Status,
        ReportedByUserId = r.ReportedByUserId,
        ReportedAt = r.ReportedAt,
        UpdatedAt = r.UpdatedAt,
        InvestigatedAt = r.InvestigatedAt,
        InvestigatedByUserId = r.InvestigatedByUserId,
        ResolutionNote = r.ResolutionNote,
    };

    public async Task<ReportActionResult> InvestigateReportAsync(Guid reportId, string adminActor)
    {
        var report = await _db.AbuseReports.Include(r => r.Track).FirstOrDefaultAsync(r => r.Id == reportId);
        if (report is null)
            return new ReportActionResult { Outcome = ReportActionOutcome.NotFound, Message = "Report not found." };

        if (report.Status == "closed")
            return new ReportActionResult
            {
                Outcome = ReportActionOutcome.InvalidState,
                Report = MapReport(report),
                Message = "Report is closed and cannot be re-investigated."
            };

        if (report.Status != "investigating")
        {
            report.Status = "investigating";
            report.InvestigatedAt = DateTime.UtcNow;
            report.InvestigatedByUserId = adminActor;
            report.UpdatedAt = DateTime.UtcNow;

            _db.AuditLogs.Add(new AuditLog
            {
                Action = "investigate_report",
                Admin = adminActor,
                Details = $"Marked report {reportId} as investigating"
            });
            await _db.SaveChangesAsync();
        }

        return new ReportActionResult
        {
            Outcome = ReportActionOutcome.Success,
            Report = MapReport(report),
            Message = "Report is under investigation."
        };
    }

    public async Task<ReportActionResult> CloseReportAsync(Guid reportId, string adminActor, string? resolutionNote)
    {
        var report = await _db.AbuseReports.Include(r => r.Track).FirstOrDefaultAsync(r => r.Id == reportId);
        if (report is null)
            return new ReportActionResult { Outcome = ReportActionOutcome.NotFound, Message = "Report not found." };

        if (report.Status != "closed")
        {
            report.Status = "closed";
            report.ResolutionNote = resolutionNote;
            report.UpdatedAt = DateTime.UtcNow;

            _db.AuditLogs.Add(new AuditLog
            {
                Action = "close_report",
                Admin = adminActor,
                Details = $"Closed report {reportId}. Note: {resolutionNote ?? "N/A"}"
            });
            await _db.SaveChangesAsync();
        }

        return new ReportActionResult
        {
            Outcome = ReportActionOutcome.Success,
            Report = MapReport(report),
            Message = "Report closed."
        };
    }

    // ══════════════════════════════════════════════
    // Settings
    // ══════════════════════════════════════════════

    public Task<AdminSettingsResponse> GetSettingsAsync() => BuildSettingsResponseAsync();

    public async Task<AdminSettingsResponse> UpdateSettingsAsync(AdminSettingsUpdateRequest request, string adminActor)
    {
        await _flags.UpsertAsync(AdminSettingsFeatureFlags.PayoutsEnabled, request.PayoutsEnabled);
        await _flags.UpsertAsync(AdminSettingsFeatureFlags.ModerationEnabled, request.ModerationEnabled);
        await _flags.UpsertAsync(AdminSettingsFeatureFlags.MarketplaceEnabled, request.MarketplaceEnabled);
        await _flags.UpsertAsync(AdminSettingsFeatureFlags.AllowExclusiveListings, request.AllowExclusiveListings);
        await _flags.UpsertAsync(AdminSettingsFeatureFlags.RequireTrackReview, request.RequireTrackReview);

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "update_admin_settings",
            Admin = adminActor,
            Details = $"Updated runtime settings: payoutsEnabled={request.PayoutsEnabled}, moderationEnabled={request.ModerationEnabled}, " +
                      $"marketplaceEnabled={request.MarketplaceEnabled}, allowExclusiveListings={request.AllowExclusiveListings}, " +
                      $"requireTrackReview={request.RequireTrackReview}"
        });
        await _db.SaveChangesAsync();

        return await BuildSettingsResponseAsync();
    }

    private async Task<AdminSettingsResponse> BuildSettingsResponseAsync()
    {
        var planManifest = TierManifest.All.Select(t => new AdminTierManifestEntry
        {
            Slug = t.Slug,
            DisplayName = t.DisplayName,
            PriceCents = t.PriceCents,
            FeeRate = (double)t.FeeRate,
            UploadLimit = t.UploadLimit,
            ReleaseReadyCreditsPerMonth = t.ReleaseReadyCreditsPerMonth,
            FeatureFlags = t.FeatureFlags
        }).ToList();

        var runtime = new AdminRuntimeSettings
        {
            PayoutsEnabled = await _flags.IsEnabledAsync(AdminSettingsFeatureFlags.PayoutsEnabled),
            ModerationEnabled = await _flags.IsEnabledAsync(AdminSettingsFeatureFlags.ModerationEnabled),
            MarketplaceEnabled = await _flags.IsEnabledAsync(AdminSettingsFeatureFlags.MarketplaceEnabled),
            AllowExclusiveListings = await _flags.IsEnabledAsync(AdminSettingsFeatureFlags.AllowExclusiveListings),
            RequireTrackReview = await _flags.IsEnabledAsync(AdminSettingsFeatureFlags.RequireTrackReview),
        };

        return new AdminSettingsResponse { PlanManifest = planManifest, RuntimeSettings = runtime };
    }
}
