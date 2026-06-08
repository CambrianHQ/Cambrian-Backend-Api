using Cambrian.Domain.Entities;
using Cambrian.Persistence.Configurations;
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

    public DbSet<Creator> Creators => Set<Creator>();

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

    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();

    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    public DbSet<ActivityItem> ActivityItems => Set<ActivityItem>();

    public DbSet<CreatorProfile> CreatorProfiles => Set<CreatorProfile>();

    public DbSet<TrackCollection> TrackCollections => Set<TrackCollection>();

    public DbSet<CreatorFollow> CreatorFollows => Set<CreatorFollow>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<Entitlement> Entitlements => Set<Entitlement>();

    public DbSet<ApiIdempotencyKey> ApiIdempotencyKeys => Set<ApiIdempotencyKey>();

    public DbSet<TrackBoost> TrackBoosts => Set<TrackBoost>();

    public DbSet<ProvenanceAnchor> ProvenanceAnchors => Set<ProvenanceAnchor>();

    public DbSet<TrackAuthorship> TrackAuthorships => Set<TrackAuthorship>();

    public DbSet<MasteringJob> MasteringJobs => Set<MasteringJob>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApiIdempotencyKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).IsRequired().HasMaxLength(128);
            e.Property(x => x.UserId).IsRequired().HasMaxLength(450);
            e.Property(x => x.RouteKey).IsRequired().HasMaxLength(128);
            e.Property(x => x.ResponseBody).IsRequired();
            e.Property(x => x.StatusCode).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.ExpiresAt).IsRequired();
            // Composite uniqueness: a (key, user, route) triple maps to exactly one stored response.
            e.HasIndex(x => new { x.Key, x.UserId, x.RouteKey })
                .IsUnique()
                .HasDatabaseName("ux_api_idempotency_keys_key_user_route");
            // Sweep-friendly: background cleanup walks expired rows.
            e.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_api_idempotency_keys_expires_at");
            e.ToTable("ApiIdempotencyKeys");
        });

        builder.Entity<TrackBoost>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.UserId).IsRequired().HasMaxLength(450);
            e.Property(b => b.CreatedAt).IsRequired();
            // One boost per user per track — enforced at the database level,
            // not just in application logic.
            e.HasIndex(b => new { b.UserId, b.TrackId })
                .IsUnique()
                .HasDatabaseName("ux_track_boosts_user_track");
            // Hot-This-Week ranks by boost count within a rolling window, so we
            // index both the track (group-by) and the timestamp (window filter).
            e.HasIndex(b => b.TrackId).HasDatabaseName("ix_track_boosts_track_id");
            e.HasIndex(b => b.CreatedAt).HasDatabaseName("ix_track_boosts_created_at");
            e.HasOne(b => b.Track)
                .WithMany()
                .HasForeignKey(b => b.TrackId)
                .OnDelete(DeleteBehavior.Cascade);
            e.ToTable("TrackBoosts");
        });

        builder.Entity<Track>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Title).HasMaxLength(200).IsRequired();
            e.Property(t => t.CambrianTrackId).HasMaxLength(25).IsRequired();
            e.HasIndex(t => t.CambrianTrackId).IsUnique();
            e.Property(t => t.Visibility).HasMaxLength(20).HasDefaultValue("public");
            e.Property(t => t.Status).HasMaxLength(30).HasDefaultValue("available");
            e.Property(t => t.Genre).HasMaxLength(60);
            e.Property(t => t.PrimaryGenre).HasMaxLength(60);
            e.Property(t => t.Subgenre).HasMaxLength(60);
            e.Property(t => t.Mood).HasMaxLength(50);
            e.Property(t => t.Tempo).HasMaxLength(30);
            e.HasOne(t => t.Creator)
                .WithMany(u => u.Tracks)
                .HasForeignKey(t => t.CreatorId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.CreatorEntity)
                .WithMany(c => c.Tracks)
                .HasForeignKey(t => t.CreatorUuid)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => t.CreatorUuid);
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
            // LicenseId is retained as an inert, orphaned column after the
            // LicenseCertificates table was dropped (licensing removed). No FK/navigation.
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
            e.Property(s => s.StripeSubscriptionId).HasMaxLength(255);
            e.Property(s => s.StripeCustomerId).HasMaxLength(255);
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
            e.Property(w => w.Status).HasMaxLength(20).HasDefaultValue("received").IsRequired();
            e.Property(w => w.ErrorMessage).HasMaxLength(2000);
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

        builder.Entity<AnalyticsEvent>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.EventType).HasMaxLength(64).IsRequired();
            e.Property(a => a.Metadata).HasMaxLength(500);
            e.Property(a => a.IsSimulated).HasDefaultValue(false);
            e.HasIndex(a => a.EventType);
            e.HasIndex(a => a.CreatedAt);
            e.HasIndex(a => new { a.TrackId, a.EventType, a.CreatedAt })
                .HasDatabaseName("ix_analytics_events_track_type_created");
        });

        builder.Entity<FeatureFlag>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(f => f.Name).IsUnique();
        });

        builder.Entity<CreatorProfile>(e =>
        {
            e.HasKey(cp => cp.Id);
            e.Property(cp => cp.UserId).HasMaxLength(450).IsRequired();
            e.HasIndex(cp => cp.UserId).IsUnique();
            e.Property(cp => cp.Slug).HasMaxLength(100).IsRequired();
            e.HasIndex(cp => cp.Slug).IsUnique();
            e.Property(cp => cp.Bio).HasMaxLength(2000);
            e.Property(cp => cp.Niche).HasMaxLength(100);
            e.Property(cp => cp.SocialLinks).HasMaxLength(2000);
            e.Property(cp => cp.BannerImageUrl).HasMaxLength(500);
            e.Property(cp => cp.ProfileImageUrl).HasMaxLength(500);
        });

        builder.Entity<TrackCollection>(e =>
        {
            e.HasKey(tc => tc.Id);
            e.Property(tc => tc.CreatorId).HasMaxLength(450).IsRequired();
            e.HasIndex(tc => tc.CreatorId);
            e.Property(tc => tc.Title).HasMaxLength(200).IsRequired();
            e.Property(tc => tc.Description).HasMaxLength(2000);
            e.Property(tc => tc.CoverImageUrl).HasMaxLength(500);
            e.Property(tc => tc.TrackIds).HasMaxLength(5000);
        });

        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.ProfileImageUrl).HasMaxLength(500);
            e.Property(u => u.CoverImageUrl).HasMaxLength(500);
            e.Property(u => u.Bio).HasMaxLength(500);
        });

        // ── Creator identity table ──
        builder.Entity<Creator>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.UserId).HasMaxLength(450).IsRequired();
            e.HasIndex(c => c.UserId).IsUnique();
            e.Property(c => c.Username).HasMaxLength(40).IsRequired();
            e.HasIndex(c => c.Username).IsUnique();
            e.Property(c => c.DisplayName).HasMaxLength(100);
            e.Property(c => c.Bio).HasMaxLength(2000);
            e.Property(c => c.ProfileImageUrl).HasMaxLength(500);
            e.Property(c => c.CoverImageUrl).HasMaxLength(500);
            e.Property(c => c.SocialLinks).HasMaxLength(2000);
            e.HasOne(c => c.User)
                .WithOne()
                .HasForeignKey<Creator>(c => c.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Creator follows ──
        builder.Entity<CreatorFollow>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.FollowerId).HasMaxLength(450).IsRequired();
            e.HasIndex(f => new { f.FollowerId, f.CreatorId }).IsUnique();
            e.HasIndex(f => f.CreatorId);
            e.HasOne(f => f.Creator)
                .WithMany()
                .HasForeignKey(f => f.CreatorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Activity items (backfill-safe display layer) ──
        builder.ApplyConfiguration(new ActivityItemConfiguration());

        // ── Unified entitlement system (Chunk 1 — access control across all resources) ──
        builder.ApplyConfiguration(new EntitlementConfiguration());

        // ── Track extensions (additive, nullable / defaulted) ──
        builder.Entity<Track>(e =>
        {
            e.Property(t => t.UseCase).HasMaxLength(100);
            e.Property(t => t.TrendingScore).HasDefaultValue(0m);
            // §9 provenance/compliance: content hash + signed stamp + commercial-rights attestation.
            e.Property(t => t.ContentHash).HasMaxLength(64);
            e.HasIndex(t => t.ContentHash).HasDatabaseName("IX_Tracks_ContentHash");
            e.Property(t => t.Signature).HasMaxLength(200);
            e.Property(t => t.CommercialRightsVerified).HasDefaultValue(false);
        });

        // ── §9 provenance + authorship (additive tables) ──
        builder.ApplyConfiguration(new ProvenanceAnchorConfiguration());
        builder.ApplyConfiguration(new TrackAuthorshipConfiguration());

        // ── Release Ready mastering ──
        builder.ApplyConfiguration(new MasteringJobConfiguration());

        // ── API keys ──
        builder.Entity<ApiKey>(e =>
        {
            e.HasKey(k => k.Id);
            e.Property(k => k.UserId).HasMaxLength(450).IsRequired();
            e.Property(k => k.KeyHash).IsRequired();
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.Property(k => k.KeyPrefix).HasMaxLength(8).IsRequired();
            e.Property(k => k.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(k => k.UserId);
            e.HasOne(k => k.User)
                .WithMany()
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
