using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Cambrian.Persistence;

public class CambrianDbContext : IdentityDbContext<ApplicationUser>
{
    public CambrianDbContext(DbContextOptions<CambrianDbContext> options)
        : base(options)
    {
    }

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
            e.HasIndex(p => p.StripeSessionId)
                .IsUnique()
                .HasFilter("\"StripeSessionId\" IS NOT NULL");
            e.HasOne(p => p.License)
                .WithOne()
                .HasForeignKey<Purchase>(p => p.LicenseId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(p => p.Buyer)
                .WithMany(u => u.Purchases)
                .HasForeignKey(p => p.BuyerId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.Track)
                .WithMany(t => t.Purchases)
                .HasForeignKey(p => p.TrackId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<LibraryItem>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => new { l.UserId, l.TrackId }).IsUnique();
            e.HasOne(l => l.User)
                .WithMany(u => u.Library)
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Track)
                .WithMany(t => t.LibraryItems)
                .HasForeignKey(l => l.TrackId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Purchase)
                .WithMany()
                .HasForeignKey(l => l.PurchaseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Payout>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasOne(p => p.Creator)
                .WithMany(u => u.Payouts)
                .HasForeignKey(p => p.CreatorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AbuseReport>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasOne(a => a.Track)
                .WithMany()
                .HasForeignKey(a => a.TrackId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditLog>(e =>
        {
            e.HasKey(a => a.Id);
        });

        builder.Entity<Subscription>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<StripeWebhookEvent>(e =>
        {
            e.HasKey(w => w.Id);
            e.Property(w => w.EventId).HasMaxLength(255).IsRequired();
            e.Property(w => w.EventType).HasMaxLength(100).IsRequired();
            e.Property(w => w.Payload).HasColumnType("text");
            e.HasIndex(w => w.EventId).IsUnique();
        });

        builder.Entity<Invoice>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasOne(i => i.User)
                .WithMany()
                .HasForeignKey(i => i.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(i => i.Purchase)
                .WithMany()
                .HasForeignKey(i => i.PurchaseId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<StreamSession>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasOne(s => s.Track)
                .WithMany()
                .HasForeignKey(s => s.TrackId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WalletTransaction>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasOne(w => w.User)
                .WithMany()
                .HasForeignKey(w => w.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LicenseCertificate>(e =>
        {
            e.HasKey(lc => lc.Id);
            e.Property(lc => lc.TrackId).HasMaxLength(25).IsRequired();
            e.Property(lc => lc.LicenseType).HasMaxLength(30);
            e.Property(lc => lc.UsageType).HasMaxLength(30).HasDefaultValue("personal");
            e.Property(lc => lc.CopyrightOwner).HasMaxLength(200);
            e.HasOne(lc => lc.Buyer)
                .WithMany()
                .HasForeignKey(lc => lc.BuyerId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(lc => lc.Creator)
                .WithMany()
                .HasForeignKey(lc => lc.CreatorId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(lc => lc.Purchase)
                .WithMany()
                .HasForeignKey(lc => lc.PurchaseId)
                .OnDelete(DeleteBehavior.Restrict);
            var listComparer = new ValueComparer<List<string>?>(
                (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                v => v == null ? 0 : v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                v => v == null ? null : v.ToList());
            e.Property(lc => lc.AllowedUses)
                .HasConversion(
                    v => v == null ? null : string.Join(',', v),
                    v => v == null ? null : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(listComparer);
            e.Property(lc => lc.Restrictions)
                .HasConversion(
                    v => v == null ? null : string.Join(',', v),
                    v => v == null ? null : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(listComparer);
        });
    }
}
