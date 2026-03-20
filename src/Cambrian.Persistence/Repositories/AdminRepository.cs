using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class AdminRepository : IAdminRepository
{
    private const string SystemActor = "system";

    private readonly CambrianDbContext _db;
    private readonly UserManager<ApplicationUser> _users;

    public AdminRepository(CambrianDbContext db, UserManager<ApplicationUser> users)
    {
        _db = db;
        _users = users;
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
            Role = u.Role,
            Status = u.Status,
            Tier = u.Tier,
            VerifiedCreator = u.VerifiedCreator
        }).ToList();
    }

    /// <summary>
    /// Delete all transactional data and non-admin users. FK-safe order.
    /// Uses raw SQL to avoid EF Core circular-dependency tracking issues
    /// (LicenseCertificate ↔ Purchase).
    /// </summary>
    public async Task<PurgeResult> PurgeTestDataAsync(string adminEmail)
    {
        var result = new PurgeResult { AdminPreserved = adminEmail };

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
                await _db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" = {0}", uid);
                await _db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"AspNetUserClaims\" WHERE \"UserId\" = {0}", uid);
                await _db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"AspNetUserTokens\" WHERE \"UserId\" = {0}", uid);
                await _db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"AspNetUserLogins\" WHERE \"UserId\" = {0}", uid);
            }

            // Now delete the user rows from AspNetUsers / Users
            foreach (var u in nonAdminUsers)
                _db.Users.Remove(u);
        }
        result.UsersDeleted = nonAdminUsers.Count;
        await _db.SaveChangesAsync();

        return result;
    }

    // ══════════════════════════════════════════════
    // User management
    // ══════════════════════════════════════════════

    public async Task<bool> SuspendUserAsync(string userId, string? reason)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return false;
        user.Status = "suspended";
        await _users.UpdateAsync(user);

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "suspend_user",
            Admin = SystemActor,
            Details = $"Suspended user {user.Email}. Reason: {reason ?? "N/A"}"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ReactivateUserAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return false;
        user.Status = "active";
        await _users.UpdateAsync(user);

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "reactivate_user",
            Admin = SystemActor,
            Details = $"Reactivated user {user.Email}"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetUserRoleAsync(string userId, string role)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return false;
        var oldRole = user.Role;
        user.Role = role;
        await _users.UpdateAsync(user);

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "change_user_role",
            Admin = SystemActor,
            Details = $"Changed role for {user.Email} from {oldRole} to {role}"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> VerifyCreatorAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return false;
        user.VerifiedCreator = true;
        user.Tier = "creator";
        await _users.UpdateAsync(user);

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "verify_creator",
            Admin = SystemActor,
            Details = $"Verified creator {user.Email}"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    // ══════════════════════════════════════════════
    // Track moderation
    // ══════════════════════════════════════════════

    public async Task<bool> RemoveTrackAsync(Guid trackId)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;
        track.Visibility = "hidden";
        track.Status = "removed";

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "remove_track",
            Admin = SystemActor,
            Details = $"Removed track '{track.Title}' (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RestoreTrackAsync(Guid trackId)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;
        track.Visibility = "public";
        track.Status = "available";

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "restore_track",
            Admin = SystemActor,
            Details = $"Restored track '{track.Title}' (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> HideTrackAsync(Guid trackId)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;
        track.Visibility = "hidden";

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "hide_track",
            Admin = SystemActor,
            Details = $"Hidden track '{track.Title}' (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> FlagTrackAsync(Guid trackId)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;
        track.Status = "flagged";

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "flag_track",
            Admin = SystemActor,
            Details = $"Flagged track '{track.Title}' (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetTrackVisibilityAsync(Guid trackId, string visibility)
    {
        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null) return false;
        track.Visibility = visibility;

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "set_track_visibility",
            Admin = SystemActor,
            Details = $"Set visibility of '{track.Title}' to {visibility} (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }
}
