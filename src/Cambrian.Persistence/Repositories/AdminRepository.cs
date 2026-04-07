using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;
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

    public AdminRepository(CambrianDbContext db, UserManager<ApplicationUser> users, IEmailService email)
    {
        _db = db;
        _users = users;
        _email = email;
    }

    public async Task<AdminDashboardSummary> GetDashboardStatsAsync()
    {
        var totalUsers = await _users.Users.CountAsync();
        var activeCreators = await _users.Users.CountAsync(u => u.Role == "Creator" || u.VerifiedCreator);
        var tracksUploaded = await _db.Tracks.CountAsync();
        var licensesSold = await _db.Purchases.CountAsync(p => p.Status == "completed");
        var totalRevenue = await _db.Purchases.Where(p => p.Status == "completed").SumAsync(p => (double)p.AmountCents) / 100.0;
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
        user.Tier = "creator";
        await _users.UpdateAsync(user);

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

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "upgrade_creator_tier",
            Admin = adminActor,
            Details = $"Changed creator tier for {user.Email} from {oldTier} to {tier}"
        });
        await _db.SaveChangesAsync();
        return true;
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
            CreatedAt = t.CreatedAt
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

        return payouts.Select(p => new AdminPayout
        {
            Id = p.Id.ToString(),
            CreatorId = p.CreatorId,
            CreatorEmail = p.Creator?.Email,
            AmountCents = p.AmountCents,
            Status = p.Status,
            RequestedAt = p.RequestedAt,
            CompletedAt = p.CompletedAt
        }).ToList();
    }

    // ══════════════════════════════════════════════
    // Payout management
    // ══════════════════════════════════════════════

    public async Task<bool> ApprovePayoutAsync(Guid payoutId, string adminActor)
    {
        var payout = await _db.Payouts.FindAsync(payoutId);
        if (payout is null) return false;
        if (payout.Status != "pending") return false;

        payout.Status = "approved";
        payout.CompletedAt = DateTime.UtcNow;

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "approve_payout",
            Admin = adminActor,
            Details = $"Approved payout {payoutId} (${payout.AmountCents / 100.0:F2})"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RejectPayoutAsync(Guid payoutId, string adminActor)
    {
        var payout = await _db.Payouts.FindAsync(payoutId);
        if (payout is null) return false;
        if (payout.Status != "pending") return false;

        payout.Status = "rejected";

        // Re-credit the creator's wallet — the debit was taken at request time
        _db.WalletTransactions.Add(new WalletTransaction
        {
            Id = Guid.NewGuid(),
            UserId = payout.CreatorId,
            AmountCents = payout.AmountCents,
            Type = "credit",
            Description = $"Payout {payoutId} rejected — funds returned",
            CreatedAt = DateTime.UtcNow
        });

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "reject_payout",
            Admin = adminActor,
            Details = $"Rejected payout {payoutId} (${payout.AmountCents / 100.0:F2}), wallet re-credited"
        });
        await _db.SaveChangesAsync();
        return true;
    }

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
}
