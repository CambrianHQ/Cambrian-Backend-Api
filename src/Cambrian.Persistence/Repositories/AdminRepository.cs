using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class AdminRepository : IAdminRepository
{
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
        var completedPurchases = await _db.Purchases.Where(p => p.Status == "completed").ToListAsync();
        var licensesSold = completedPurchases.Count;
        var totalRevenue = completedPurchases.Sum(p => p.AmountCents) / 100.0;
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
    /// </summary>
    public async Task<PurgeResult> PurgeTestDataAsync(string adminEmail)
    {
        var result = new PurgeResult { AdminPreserved = adminEmail };

        // 1. Child tables first (no FK dependents)
        var licenseCerts = await _db.LicenseCertificates.ToListAsync();
        _db.LicenseCertificates.RemoveRange(licenseCerts);
        result.LicenseCertificatesDeleted = licenseCerts.Count;

        var streamSessions = await _db.StreamSessions.ToListAsync();
        _db.StreamSessions.RemoveRange(streamSessions);
        result.StreamSessionsDeleted = streamSessions.Count;

        var walletTxns = await _db.WalletTransactions.ToListAsync();
        _db.WalletTransactions.RemoveRange(walletTxns);
        result.WalletTransactionsDeleted = walletTxns.Count;

        var webhookEvents = await _db.StripeWebhookEvents.ToListAsync();
        _db.StripeWebhookEvents.RemoveRange(webhookEvents);
        result.WebhookEventsDeleted = webhookEvents.Count;

        var auditLogs = await _db.AuditLogs.ToListAsync();
        _db.AuditLogs.RemoveRange(auditLogs);
        result.AuditLogsDeleted = auditLogs.Count;

        var abuseReports = await _db.AbuseReports.ToListAsync();
        _db.AbuseReports.RemoveRange(abuseReports);
        result.AbuseReportsDeleted = abuseReports.Count;

        var subscriptions = await _db.Subscriptions.ToListAsync();
        _db.Subscriptions.RemoveRange(subscriptions);
        result.SubscriptionsDeleted = subscriptions.Count;

        // 2. Library items (FK → Purchase, Track)
        var libraryItems = await _db.Library.ToListAsync();
        _db.Library.RemoveRange(libraryItems);
        result.LibraryItemsDeleted = libraryItems.Count;

        // 3. Invoices (FK → Purchase)
        var invoices = await _db.Invoices.ToListAsync();
        _db.Invoices.RemoveRange(invoices);
        result.InvoicesDeleted = invoices.Count;

        // 4. Payouts (FK → User)
        var payouts = await _db.Payouts.ToListAsync();
        _db.Payouts.RemoveRange(payouts);
        result.PayoutsDeleted = payouts.Count;

        // 5. Purchases (FK → Track, User)
        var purchases = await _db.Purchases.ToListAsync();
        _db.Purchases.RemoveRange(purchases);
        result.PurchasesDeleted = purchases.Count;

        // 6. Tracks (FK → User/Creator)
        var tracks = await _db.Tracks.ToListAsync();
        _db.Tracks.RemoveRange(tracks);
        result.TracksDeleted = tracks.Count;

        // 7. Users — delete everyone except the admin
        var nonAdminUsers = await _users.Users
            .Where(u => u.Email != adminEmail)
            .ToListAsync();
        foreach (var u in nonAdminUsers)
            _db.Users.Remove(u);
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
            Admin = "system",
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
            Admin = "system",
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
            Admin = "system",
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
            Admin = "system",
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
            Admin = "system",
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
            Admin = "system",
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
            Admin = "system",
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
            Admin = "system",
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
            Admin = "system",
            Details = $"Set visibility of '{track.Title}' to {visibility} (id={trackId})"
        });
        await _db.SaveChangesAsync();
        return true;
    }
}
