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

    public DbSet<TrackMedia> TrackMedia => Set<TrackMedia>();

    public DbSet<MediaReconciliationRun> MediaReconciliationRuns => Set<MediaReconciliationRun>();

    public DbSet<MediaReconciliationFinding> MediaReconciliationFindings => Set<MediaReconciliationFinding>();

    public DbSet<TrackAiDisclosure> TrackAiDisclosures => Set<TrackAiDisclosure>();

    public DbSet<TrackAiDisclosureRevision> TrackAiDisclosureRevisions => Set<TrackAiDisclosureRevision>();

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

    public DbSet<QualifiedPlayEvent> QualifiedPlayEvents => Set<QualifiedPlayEvent>();

    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();

    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();

    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    public DbSet<ActivityItem> ActivityItems => Set<ActivityItem>();

    public DbSet<CreatorProfile> CreatorProfiles => Set<CreatorProfile>();

    public DbSet<TrackCollection> TrackCollections => Set<TrackCollection>();

    public DbSet<AlbumTrack> AlbumTracks => Set<AlbumTrack>();

    public DbSet<TrackLyrics> TrackLyrics => Set<TrackLyrics>();

    public DbSet<TrackCreationProcess> TrackCreationProcesses => Set<TrackCreationProcess>();

    public DbSet<TrackVideoProof> TrackVideoProofs => Set<TrackVideoProof>();

    public DbSet<CreatorFollow> CreatorFollows => Set<CreatorFollow>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<Entitlement> Entitlements => Set<Entitlement>();

    public DbSet<ApiIdempotencyKey> ApiIdempotencyKeys => Set<ApiIdempotencyKey>();

    public DbSet<TrackBoost> TrackBoosts => Set<TrackBoost>();

    public DbSet<ProvenanceAnchor> ProvenanceAnchors => Set<ProvenanceAnchor>();

    public DbSet<TrackAuthorship> TrackAuthorships => Set<TrackAuthorship>();

    public DbSet<MasteringJob> MasteringJobs => Set<MasteringJob>();

    public DbSet<ReleaseCreditPurchase> ReleaseCreditPurchases => Set<ReleaseCreditPurchase>();

    public DbSet<AuthorshipRecord> AuthorshipRecords => Set<AuthorshipRecord>();

    public DbSet<EarningsTransaction> EarningsTransactions => Set<EarningsTransaction>();

    public DbSet<FanSubscription> FanSubscriptions => Set<FanSubscription>();

    public DbSet<TrackStat> TrackStats => Set<TrackStat>();

    public DbSet<CreatorStat> CreatorStats => Set<CreatorStat>();

    public DbSet<NewsletterSubscriber> NewsletterSubscribers => Set<NewsletterSubscriber>();

    public DbSet<WeeklyChartSnapshot> WeeklyChartSnapshots => Set<WeeklyChartSnapshot>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<WeeklyChartSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatorId).IsRequired().HasMaxLength(450);
            e.Property(x => x.Title).IsRequired().HasMaxLength(300);
            e.Property(x => x.Artist).IsRequired().HasMaxLength(300);
            e.Property(x => x.Basis).IsRequired().HasMaxLength(32);
            // A week holds exactly one row per rank and per track; recompute
            // replaces the whole week atomically (idempotent per week).
            e.HasIndex(x => new { x.WeekStartUtc, x.Rank })
                .IsUnique()
                .HasDatabaseName("ux_weekly_chart_week_rank");
            e.HasIndex(x => new { x.WeekStartUtc, x.TrackId })
                .IsUnique()
                .HasDatabaseName("ux_weekly_chart_week_track");
            e.ToTable("WeeklyChartSnapshots");
        });

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
            // Additive: request-payload fingerprint (mismatch => 409 idempotency_key_reused)
            // and claim lifecycle (processing/completed/failed) for the upload finalization
            // idempotency boundary. Nullable/defaulted so existing rows stay valid.
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.Status).IsRequired().HasMaxLength(16).HasDefaultValue("completed");
            // Composite uniqueness: a (key, user, route) triple maps to exactly one stored response.
            // This is the DB-enforced backstop that makes claiming a key race-safe across
            // concurrently running API instances — see IdempotencyStore.TryBeginAsync.
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
            e.Property(p => p.StripeIdempotencyKey).HasMaxLength(255);
            e.Property(p => p.StripeTransferId).HasMaxLength(255);
            e.Property(p => p.ReviewedByUserId).HasMaxLength(450);
            e.Property(p => p.RejectionReason).HasMaxLength(1000);
            e.HasIndex(p => p.StripeIdempotencyKey)
                .IsUnique()
                .HasFilter("\"StripeIdempotencyKey\" IS NOT NULL");
            e.HasIndex(p => new { p.CreatorId, p.Status });
            e.HasOne(p => p.Creator)
                .WithMany(u => u.Payouts)
                .HasForeignKey(p => p.CreatorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AbuseReport>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.TargetType).HasMaxLength(20).HasDefaultValue("track");
            e.Property(a => a.TargetId).HasMaxLength(64);
            e.Property(a => a.ReportedByUserId).HasMaxLength(450);
            e.Property(a => a.InvestigatedByUserId).HasMaxLength(450);
            e.HasOne(a => a.Track)
                .WithMany()
                .HasForeignKey(a => a.TrackId)
                .OnDelete(DeleteBehavior.SetNull);
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
            e.Property(s => s.StripeSessionId).HasMaxLength(255);
            e.Property(s => s.TrialEndsAt);
            // invoice.paid / subscription.* webhooks resolve the user by Stripe customer id;
            // without this index every renewal does a full Subscriptions table scan.
            e.HasIndex(s => s.StripeCustomerId);
            e.HasIndex(s => s.StripeSubscriptionId);
            // Webhook idempotency: at most one subscription per Stripe checkout
            // session, so a duplicate/retried checkout.session.completed cannot
            // create duplicate subscriptions or re-overwrite the user's tier.
            // Mirrors ReleaseCreditPurchase / Purchase session idempotency.
            e.HasIndex(s => s.StripeSessionId)
                .IsUnique()
                .HasFilter("\"StripeSessionId\" IS NOT NULL");
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
            e.Property(s => s.ListenerKeyHash).HasMaxLength(64);
            e.Property(s => s.AnonymousSessionHash).HasMaxLength(64);
            e.Property(s => s.IdempotencyKey).HasMaxLength(64);
            e.Property(s => s.QualificationStatus).HasMaxLength(32).HasDefaultValue("legacy_unqualified");
            e.HasIndex(s => s.IdempotencyKey)
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL")
                .HasDatabaseName("ux_stream_sessions_idempotency_key");
            e.HasIndex(s => new { s.TrackId, s.ListenerKeyHash, s.StartedAt })
                .HasDatabaseName("ix_stream_sessions_track_listener_started");
            e.HasOne(s => s.Track)
                .WithMany()
                .HasForeignKey(s => s.TrackId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.ApplyConfiguration(new TrackMediaConfiguration());
        builder.ApplyConfiguration(new MediaReconciliationRunConfiguration());
        builder.ApplyConfiguration(new MediaReconciliationFindingConfiguration());

        builder.ApplyConfiguration(new QualifiedPlayEventConfiguration());

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
            e.Property(cp => cp.Genres).HasMaxLength(1000);
            e.Property(cp => cp.SocialLinks).HasMaxLength(2000);
            e.Property(cp => cp.BannerImageUrl).HasMaxLength(500);
            e.Property(cp => cp.ProfileImageUrl).HasMaxLength(500);
            // JSON blobs (SocialLinks precedent); sizes enforced again in the controller.
            e.Property(cp => cp.StudioSetup).HasMaxLength(8000);
            e.Property(cp => cp.JourneyEntries).HasMaxLength(16000);
        });

        builder.Entity<TrackCollection>(e =>
        {
            e.HasKey(tc => tc.Id);
            e.Property(tc => tc.CreatorId).HasMaxLength(450).IsRequired();
            e.HasIndex(tc => tc.CreatorId);
            e.Property(tc => tc.Title).HasMaxLength(200).IsRequired();
            // Slug is unique per creator (albums are addressed by id publicly;
            // the slug is an SEO affordance, so no global uniqueness needed).
            e.Property(tc => tc.Slug).HasMaxLength(220).IsRequired().HasDefaultValue("");
            e.HasIndex(tc => new { tc.CreatorId, tc.Slug }).IsUnique();
            e.Property(tc => tc.Description).HasMaxLength(2000);
            e.Property(tc => tc.CoverImageUrl).HasMaxLength(500);
            e.Property(tc => tc.TrackIds).HasMaxLength(5000);
            e.Property(tc => tc.Visibility).HasMaxLength(20).IsRequired().HasDefaultValue("public");
        });

        builder.Entity<AlbumTrack>(e =>
        {
            e.HasKey(at => new { at.AlbumId, at.TrackId });
            e.HasIndex(at => new { at.AlbumId, at.Position });
            e.HasIndex(at => at.TrackId);
            // No FK to Tracks on purpose: album membership must never block or
            // cascade into Track rows (albums are relationships only). Album
            // deletion cleans its own rows in the repository.
        });

        builder.Entity<TrackLyrics>(e =>
        {
            e.HasKey(tl => tl.TrackId);
            e.Property(tl => tl.Lyrics).HasMaxLength(20000).IsRequired();
            e.Property(tl => tl.Language).HasMaxLength(16).IsRequired().HasDefaultValue("en");
            e.Property(tl => tl.IsExplicit);
            e.Property(tl => tl.Version).IsConcurrencyToken().HasDefaultValue(1);
        });

        builder.Entity<TrackCreationProcess>(e =>
        {
            e.HasKey(cp => cp.TrackId);
            e.Property(cp => cp.Story).HasMaxLength(5000);
            e.Property(cp => cp.YoutubeUrl).HasMaxLength(500);
            e.Property(cp => cp.ToolsUsed).HasMaxLength(2000);
            e.Property(cp => cp.DAW).HasMaxLength(200);
            e.Property(cp => cp.VocalChain).HasMaxLength(2000);
            e.Property(cp => cp.PromptNotes).HasMaxLength(5000);
            e.Property(cp => cp.ProductionNotes).HasMaxLength(5000);
            e.Property(cp => cp.HumanContributionNotes).HasMaxLength(5000);
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

            // Admin editorial placement — additive, nullable/defaulted, one-way (no unfeature/unpin yet).
            e.Property(t => t.IsFeatured).HasDefaultValue(false);
            e.Property(t => t.FeaturedByUserId).HasMaxLength(450);
            e.Property(t => t.IsPinned).HasDefaultValue(false);
            e.Property(t => t.PinnedByUserId).HasMaxLength(450);

            // Trash / restore / permanent-delete — additive, all nullable. The row is
            // never SQL-deleted; see Track.PurgeRequestedAt/PurgedAt doc comments.
            e.Property(t => t.DeletedByUserId).HasMaxLength(450);
            e.Property(t => t.PreDeleteVisibility).HasMaxLength(20);
            e.Property(t => t.PreDeleteStatus).HasMaxLength(30);
            e.HasIndex(t => t.DeletedAt).HasDatabaseName("IX_Tracks_DeletedAt");
            e.HasIndex(t => t.PurgeRequestedAt).HasDatabaseName("IX_Tracks_PurgeRequestedAt");
        });

        // ── §9 provenance + authorship (additive tables) ──
        builder.ApplyConfiguration(new ProvenanceAnchorConfiguration());
        builder.ApplyConfiguration(new TrackAuthorshipConfiguration());

        // ── Behind The Track: proof videos (additive table) ──
        builder.ApplyConfiguration(new TrackVideoProofConfiguration());

        // ── Additive AI disclosure and immutable revision history ──
        builder.ApplyConfiguration(new TrackAiDisclosureConfiguration());
        builder.ApplyConfiguration(new TrackAiDisclosureRevisionConfiguration());

        // ── Release Ready mastering ──
        builder.ApplyConfiguration(new MasteringJobConfiguration());

        // ── Release Ready credit-pack purchases (never-expiring purchased credits) ──
        builder.ApplyConfiguration(new ReleaseCreditPurchaseConfiguration());

        // ── Release pipeline: paid authorship records + money-in earnings ledger ──
        builder.ApplyConfiguration(new AuthorshipRecordConfiguration());
        builder.ApplyConfiguration(new EarningsTransactionConfiguration());
        builder.ApplyConfiguration(new FanSubscriptionConfiguration());

        // ── Denormalized public-metrics counters (track + creator stats) ──
        builder.ApplyConfiguration(new TrackStatConfiguration());
        builder.ApplyConfiguration(new CreatorStatConfiguration());

        // ── Newsletter opt-ins ──
        builder.Entity<NewsletterSubscriber>(e =>
        {
            e.HasKey(n => n.Id);
            e.Property(n => n.Email).IsRequired().HasMaxLength(320); // RFC 5321 max
            // Unique email → duplicate submissions are idempotent (200, no new row).
            e.HasIndex(n => n.Email).IsUnique().HasDatabaseName("ux_newsletter_subscribers_email");
            e.Property(n => n.Source).HasMaxLength(100);
            e.Property(n => n.CreatedAt).IsRequired();
            e.Property(n => n.ProviderSynced).HasDefaultValue(false);
            e.ToTable("NewsletterSubscribers");
        });

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
