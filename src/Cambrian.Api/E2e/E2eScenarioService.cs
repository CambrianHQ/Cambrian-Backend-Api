using System.Text.Json;
using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Pricing;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.E2e;

/// <summary>
/// Test-only scenario engine behind <c>/__e2e/*</c>. Resets an isolated Testing database,
/// seeds a deterministic dataset, exposes state snapshots, and drives Stripe outcomes by
/// feeding synthetic signed events through the REAL webhook handler (no Stripe calls).
///
/// This type is registered in DI ONLY when <see cref="E2eSupport.IsEnabled"/> is true, so it
/// never exists in Production/Staging. It deliberately reads/writes existing tables and uses
/// <see cref="UserManager{TUser}"/> directly — no schema change, no new migration.
/// </summary>
public sealed class E2eScenarioService
{
    // ── Deterministic seed identifiers (stable across reseeds) ──
    public const string SeedPassword = "E2ePass!234";

    public static readonly DateTime SeedInstant = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public const string ListenerUserId = "11111111-1111-1111-1111-111111111111";
    public const string CreatorUserId = "22222222-2222-2222-2222-222222222222";
    public const string ProUserId = "33333333-3333-3333-3333-333333333333";
    public const string EmptyCreatorUserId = "44444444-4444-4444-4444-444444444444";

    public const string ListenerEmail = "listener@e2e.cambrian.test";
    public const string CreatorEmail = "creator@e2e.cambrian.test";
    public const string ProEmail = "pro@e2e.cambrian.test";
    public const string EmptyCreatorEmail = "empty@e2e.cambrian.test";

    public const string CreatorHandle = "testcreator";
    public const string EmptyCreatorHandle = "emptycreator";

    private static readonly Guid CreatorEntityId = new("2a000000-0000-0000-0000-000000000001");
    private static readonly Guid EmptyCreatorEntityId = new("2a000000-0000-0000-0000-000000000002");

    private static readonly Guid PlayableTrackId = new("10000000-0000-0000-0000-000000000001");
    private static readonly Guid MissingAudioTrackId = new("10000000-0000-0000-0000-000000000002");
    private static readonly Guid NoAuthorshipTrackId = new("10000000-0000-0000-0000-000000000003");
    private static readonly Guid DraftTrackId = new("10000000-0000-0000-0000-000000000004");

    private const string PlayableAudioKey = "e2e/seed/playable.mp3";
    private const string MissingAudioKey = "e2e/seed/missing-audio.mp3";
    private const string NoAuthorshipAudioKey = "e2e/seed/no-authorship.mp3";
    private const string DraftAudioKey = "e2e/seed/draft.mp3";

    private static readonly Guid CompletedPurchaseId = new("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid RefundedPurchaseId = new("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid SeedAuthorshipRecordId = new("a0000000-0000-0000-0000-000000000001");

    // A minimal valid MP3 frame header — enough that streamed bytes look like audio.
    private static readonly byte[] AudioBytes = { 0xFF, 0xFB, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00 };

    private readonly CambrianDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IWebhookService _webhooks;
    private readonly IObjectStorage _storage;
    private readonly IEntitlementService _entitlements;
    private readonly IReleaseCreditService _credits;
    private readonly IConfiguration _config;
    private readonly ILogger<E2eScenarioService> _logger;

    public E2eScenarioService(
        CambrianDbContext db,
        UserManager<ApplicationUser> users,
        IWebhookService webhooks,
        IObjectStorage storage,
        IEntitlementService entitlements,
        IReleaseCreditService credits,
        IConfiguration config,
        ILogger<E2eScenarioService> logger)
    {
        _db = db;
        _users = users;
        _webhooks = webhooks;
        _storage = storage;
        _entitlements = entitlements;
        _credits = credits;
        _config = config;
        _logger = logger;
    }

    // ────────────────────────────── Reset / Seed ──────────────────────────────

    /// <summary>Clear every Identity + domain table (transactionally) and optionally reseed.</summary>
    public async Task<E2eManifest> ResetAsync(bool reseed, CancellationToken ct = default)
    {
        await WipeAsync(ct);
        return reseed
            ? await PopulateAsync(ct)
            : new E2eManifest(false, SeedPassword, CreatorHandle, Array.Empty<E2eAccount>(), Array.Empty<E2eSeedTrack>());
    }

    /// <summary>Wipe then seed — idempotent and deterministic (same ids/state every run).</summary>
    public async Task<E2eManifest> SeedAsync(CancellationToken ct = default)
    {
        await WipeAsync(ct);
        return await PopulateAsync(ct);
    }

    /// <summary>
    /// Delete all rows in FK-safe order inside a single transaction. The SQLite test DB runs
    /// with foreign keys off, but a real Testing Postgres enforces them, so children are
    /// deleted before parents and AspNetUsers last.
    /// </summary>
    private async Task WipeAsync(CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Children / leaf tables first.
        await _db.Library.ExecuteDeleteAsync(ct);
        await _db.Invoices.ExecuteDeleteAsync(ct);
        await _db.WalletTransactions.ExecuteDeleteAsync(ct);
        await _db.Entitlements.ExecuteDeleteAsync(ct);
        await _db.AbuseReports.ExecuteDeleteAsync(ct);
        await _db.StreamSessions.ExecuteDeleteAsync(ct);
        await _db.TrackBoosts.ExecuteDeleteAsync(ct);
        await _db.TrackAuthorships.ExecuteDeleteAsync(ct);
        await _db.ProvenanceAnchors.ExecuteDeleteAsync(ct);
        await _db.CreatorFollows.ExecuteDeleteAsync(ct);
        await _db.Purchases.ExecuteDeleteAsync(ct);
        await _db.Tracks.ExecuteDeleteAsync(ct);
        await _db.Creators.ExecuteDeleteAsync(ct);
        await _db.CreatorProfiles.ExecuteDeleteAsync(ct);
        await _db.Subscriptions.ExecuteDeleteAsync(ct);
        await _db.FanSubscriptions.ExecuteDeleteAsync(ct);
        await _db.Payouts.ExecuteDeleteAsync(ct);
        await _db.ApiKeys.ExecuteDeleteAsync(ct);
        await _db.MasteringJobs.ExecuteDeleteAsync(ct);
        await _db.ReleaseCreditPurchases.ExecuteDeleteAsync(ct);
        await _db.AuthorshipRecords.ExecuteDeleteAsync(ct);
        await _db.EarningsTransactions.ExecuteDeleteAsync(ct);
        await _db.AnalyticsEvents.ExecuteDeleteAsync(ct);
        await _db.ActivityItems.ExecuteDeleteAsync(ct);
        await _db.AuditLogs.ExecuteDeleteAsync(ct);
        await _db.StripeWebhookEvents.ExecuteDeleteAsync(ct);
        await _db.TrackCollections.ExecuteDeleteAsync(ct);
        await _db.ApiIdempotencyKeys.ExecuteDeleteAsync(ct);
        await _db.FeatureFlags.ExecuteDeleteAsync(ct);

        // Identity: child tables, then the users themselves. (Roles are app-level config,
        // not per-test data, so they're left intact.)
        await _db.UserRoles.ExecuteDeleteAsync(ct);
        await _db.UserClaims.ExecuteDeleteAsync(ct);
        await _db.UserLogins.ExecuteDeleteAsync(ct);
        await _db.UserTokens.ExecuteDeleteAsync(ct);
        await _db.Users.ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        _logger.LogInformation("[E2E] Database wiped.");
    }

    private async Task<E2eManifest> PopulateAsync(CancellationToken ct)
    {
        // ── Accounts ──
        await CreateUserAsync(ListenerUserId, ListenerEmail, CreatorTier.Free, "free", "Inactive", verifiedCreator: false, ct);
        await CreateUserAsync(CreatorUserId, CreatorEmail, CreatorTier.Creator, "creator", "Active", verifiedCreator: true, ct);
        await CreateUserAsync(ProUserId, ProEmail, CreatorTier.Pro, "pro", "Active", verifiedCreator: true, ct);
        await CreateUserAsync(EmptyCreatorUserId, EmptyCreatorEmail, CreatorTier.Creator, "creator", "Active", verifiedCreator: true, ct);

        // Connect + monetization fields on the creator so support flows resolve.
        var creator = await _users.FindByIdAsync(CreatorUserId)
            ?? throw new InvalidOperationException("Seed creator missing after creation.");
        creator.StripeAccountId = "acct_e2e_creator";
        creator.FanSubscriptionPriceCents = 500;
        creator.WalletBalanceCents = 2549;
        await _users.UpdateAsync(creator);

        // ── Creator identities + profiles ──
        _db.Creators.Add(new Creator
        {
            Id = CreatorEntityId,
            UserId = CreatorUserId,
            Username = CreatorHandle,
            DisplayName = "Test Creator",
            Bio = "E2E seed creator",
            CreatedAt = SeedInstant,
            UpdatedAt = SeedInstant,
        });
        _db.Creators.Add(new Creator
        {
            Id = EmptyCreatorEntityId,
            UserId = EmptyCreatorUserId,
            Username = EmptyCreatorHandle,
            DisplayName = "Empty Creator",
            Bio = "E2E seed creator with no tracks",
            CreatedAt = SeedInstant,
            UpdatedAt = SeedInstant,
        });
        _db.CreatorProfiles.Add(new CreatorProfile
        {
            Id = new Guid("2b000000-0000-0000-0000-000000000001"),
            UserId = CreatorUserId,
            Slug = CreatorHandle,
            Bio = "E2E seed creator",
            CreatedAt = SeedInstant,
            UpdatedAt = SeedInstant,
        });
        _db.CreatorProfiles.Add(new CreatorProfile
        {
            Id = new Guid("2b000000-0000-0000-0000-000000000002"),
            UserId = EmptyCreatorUserId,
            Slug = EmptyCreatorHandle,
            Bio = "E2E seed creator with no tracks",
            CreatedAt = SeedInstant,
            UpdatedAt = SeedInstant,
        });

        // ── Tracks (owned by testcreator) ──
        AddTrack(PlayableTrackId, "CAMB-TRK-E2EPLAY1", "Playable Beat", PlayableAudioKey, "public", "available", commercialRightsVerified: true);
        AddTrack(MissingAudioTrackId, "CAMB-TRK-E2EMISS1", "Missing Audio Beat", MissingAudioKey, "public", "available", commercialRightsVerified: true);
        AddTrack(NoAuthorshipTrackId, "CAMB-TRK-E2ENOAU1", "No Authorship Beat", NoAuthorshipAudioKey, "public", "available", commercialRightsVerified: false);
        AddTrack(DraftTrackId, "CAMB-TRK-E2EDRFT1", "Draft Beat", DraftAudioKey, "hidden", "draft", commercialRightsVerified: false);

        // Authorship documented for the playable track only (so "no-authorship" is genuinely absent).
        _db.TrackAuthorships.Add(new TrackAuthorship
        {
            Id = new Guid("a1000000-0000-0000-0000-000000000001"),
            TrackId = PlayableTrackId,
            Edits = "Re-arranged the drop and replaced the lead.",
            ArrangementNotes = "Intro / verse / drop / outro.",
            LyricsAuthored = true,
            ProcessNotes = "Composed in-DAW from scratch.",
            AiDisclosure = "No generative AI used.",
            CreatedAt = SeedInstant,
            UpdatedAt = SeedInstant,
        });

        // Paid authorship record awaiting payment (authorship state).
        _db.AuthorshipRecords.Add(new AuthorshipRecord
        {
            Id = SeedAuthorshipRecordId,
            TrackId = PlayableTrackId,
            CreatorId = CreatorUserId,
            ArtistName = "Test Creator",
            Status = "pending_payment",
            EvidenceJson = "{}",
            StripeSessionId = "cs_e2e_seed_authrec",
            CreatedAt = SeedInstant,
        });

        // ── Subscription state ──
        AddSubscription(CreatorUserId, "creator");
        AddSubscription(ProUserId, "pro");

        // ── Credit state (Pro: purchased pack + one consumed job) ──
        _db.ReleaseCreditPurchases.Add(new ReleaseCreditPurchase
        {
            Id = new Guid("d0000000-0000-0000-0000-000000000001"),
            CreatorId = ProUserId,
            Credits = 10,
            AmountCents = 2999,
            Pack = "ten",
            Status = "paid",
            StripeSessionId = "cs_e2e_seed_credits",
            CreatedAt = SeedInstant,
        });
        _db.MasteringJobs.Add(new MasteringJob
        {
            Id = new Guid("d1000000-0000-0000-0000-000000000001"),
            CreatorId = ProUserId,
            Status = "done",
            Engine = "ffmpeg",
            Kind = "mastering",
            SourceKey = "e2e/seed/master-source.wav",
            ChargedAt = SeedInstant,
            CreditSource = "purchased",
            CreatedAt = SeedInstant,
            StartedAt = SeedInstant,
            CompletedAt = SeedInstant,
        });

        // ── Payment state (listener bought the playable track) ──
        _db.Purchases.Add(new Purchase
        {
            Id = CompletedPurchaseId,
            BuyerId = ListenerUserId,
            TrackId = PlayableTrackId,
            AmountCents = 2999,
            PaymentMethod = "stripe",
            LicenseType = "nonexclusive",
            Status = "completed",
            UsageType = "personal",
            StripeSessionId = "cs_e2e_seed_purchase",
            CompletedAt = SeedInstant,
            CreatedAt = SeedInstant,
            UpdatedAt = SeedInstant,
        });
        _db.Purchases.Add(new Purchase
        {
            Id = RefundedPurchaseId,
            BuyerId = ListenerUserId,
            TrackId = PlayableTrackId,
            AmountCents = 2999,
            PaymentMethod = "stripe",
            LicenseType = "nonexclusive",
            Status = "refunded",
            UsageType = "personal",
            StripeSessionId = "cs_e2e_seed_refund",
            CreatedAt = SeedInstant,
            UpdatedAt = SeedInstant,
        });
        _db.Library.Add(new LibraryItem
        {
            Id = new Guid("e0000000-0000-0000-0000-000000000001"),
            UserId = ListenerUserId,
            TrackId = PlayableTrackId,
            PurchaseId = CompletedPurchaseId,
            Title = "Playable Beat",
            Artist = "Test Creator",
            AudioUrl = PlayableAudioKey,
            SavedAt = SeedInstant,
        });
        _db.WalletTransactions.Add(new WalletTransaction
        {
            Id = new Guid("e1000000-0000-0000-0000-000000000001"),
            UserId = CreatorUserId,
            AmountCents = 2549,
            Type = "credit",
            Description = "Sale: Playable Beat",
            RelatedPurchaseId = CompletedPurchaseId,
            CreatedAt = SeedInstant,
        });

        // ── Entitlement state (listener can download the playable track) ──
        _db.Entitlements.Add(new Entitlement
        {
            Id = new Guid("e2000000-0000-0000-0000-000000000001"),
            UserId = ListenerUserId,
            ResourceType = EntitlementResourceType.Track,
            ResourceId = PlayableTrackId.ToString(),
            AccessLevel = EntitlementAccessLevel.Download,
            SourceType = EntitlementSourceType.Purchase,
            SourceId = CompletedPurchaseId.ToString(),
            GrantedAt = SeedInstant,
        });

        // ── Support state (tip + active fan subscription, listener → creator) ──
        _db.EarningsTransactions.Add(new EarningsTransaction
        {
            Id = new Guid("e3000000-0000-0000-0000-000000000001"),
            ArtistUserId = CreatorUserId,
            Source = "tip",
            GrossCents = 500,
            FeeCents = 75,
            NetCents = 425,
            Currency = "usd",
            ExternalRef = "cs_e2e_seed_tip",
            PayerUserId = ListenerUserId,
            CreatedAt = SeedInstant,
        });
        _db.FanSubscriptions.Add(new FanSubscription
        {
            Id = new Guid("e4000000-0000-0000-0000-000000000001"),
            FanUserId = ListenerUserId,
            ArtistUserId = CreatorUserId,
            PriceCents = 500,
            Status = "active",
            StripeSubscriptionId = "sub_e2e_seed",
            StripeSessionId = "cs_e2e_seed_fansub",
            CreatedAt = SeedInstant,
            ActivatedAt = SeedInstant,
        });

        // ── Feature flags the seeded flows rely on ──
        _db.FeatureFlags.Add(new FeatureFlag
        {
            Id = new Guid("f0000000-0000-0000-0000-000000000001"),
            Name = "StripeConnectEnabled",
            Enabled = true,
            RolloutPercentage = 100,
            CreatedAt = SeedInstant,
            UpdatedAt = SeedInstant,
        });

        await _db.SaveChangesAsync(ct);

        // ── Object storage: upload the playable + no-authorship objects; leave the
        // missing-audio (and draft) objects absent so they 404 truthfully. ──
        await UploadAudioAsync(PlayableAudioKey, ct);
        await UploadAudioAsync(NoAuthorshipAudioKey, ct);

        _logger.LogInformation("[E2E] Database seeded.");
        return await BuildManifestAsync(ct);
    }

    private async Task CreateUserAsync(
        string id, string email, CreatorTier tier, string tierString, string subscriptionStatus,
        bool verifiedCreator, CancellationToken ct)
    {
        var user = new ApplicationUser
        {
            Id = id,
            Email = email,
            UserName = email,
            DisplayName = email.Split('@')[0],
            // Creator-tier accounts need Role="Creator" — CapabilityResolver
            // grants track.upload/edit by ROLE, not tier (residue-be refactor),
            // and real creator accounts carry the Creator role in production.
            // Without this the seeded creator can't reach /upload or /studio.
            Role = tier == CreatorTier.Free ? "User" : "Creator",
            Status = "active",
            Tier = tierString,
            CreatorTier = tier,
            VerifiedCreator = verifiedCreator,
            SubscriptionStatus = subscriptionStatus,
            EmailConfirmed = true,
            EmailVerified = true,
            CreatedAt = SeedInstant,
        };

        var result = await _users.CreateAsync(user, SeedPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => $"{e.Code}:{e.Description}"));
            throw new InvalidOperationException($"[E2E] Failed to create seed user {email}: {errors}");
        }

        ct.ThrowIfCancellationRequested();
    }

    private void AddTrack(
        Guid id, string cambrianId, string title, string audioKey, string visibility, string status,
        bool commercialRightsVerified)
    {
        _db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = cambrianId,
            Title = title,
            Genre = "Hip-Hop",
            Price = 29.99m,
            LicenseType = "standard",
            AudioUrl = audioKey,
            Visibility = visibility,
            Status = status,
            CommercialRightsVerified = commercialRightsVerified,
            CreatorId = CreatorUserId,
            CreatorUuid = CreatorEntityId,
            CreatedAt = SeedInstant,
        });
    }

    private void AddSubscription(string userId, string plan)
    {
        _db.Subscriptions.Add(new Subscription
        {
            Id = userId == CreatorUserId
                ? new Guid("5b000000-0000-0000-0000-000000000001")
                : new Guid("5b000000-0000-0000-0000-000000000002"),
            UserId = userId,
            Plan = plan,
            Status = "active",
            StripeCustomerId = CustomerIdFor(userId),
            StripeSubscriptionId = $"sub_e2e_{userId[..8]}",
            StartedAt = SeedInstant,
            ExpiresAt = SeedInstant.AddDays(30),
        });
    }

    private async Task UploadAudioAsync(string key, CancellationToken ct)
    {
        using var ms = new MemoryStream(AudioBytes, writable: false);
        await _storage.UploadAsync(ms, key, "audio/mpeg");
        ct.ThrowIfCancellationRequested();
    }

    // ────────────────────────────── State snapshots ──────────────────────────────

    public async Task<object> BuildStateAsync(string? domain, string? email, Guid? trackId, CancellationToken ct = default)
    {
        return (domain?.ToLowerInvariant()) switch
        {
            null or "" or "global" => await GlobalStateAsync(ct),
            "payment" => await PaymentStateAsync(email, ct),
            "entitlement" or "entitlements" => await EntitlementStateAsync(email, ct),
            "credit" or "credits" => await CreditStateAsync(email, ct),
            "support" => await SupportStateAsync(email, ct),
            "authorship" => await AuthorshipStateAsync(email, trackId, ct),
            _ => throw new ArgumentException($"Unknown state domain '{domain}'.", nameof(domain)),
        };
    }

    private async Task<object> GlobalStateAsync(CancellationToken ct)
    {
        return new
        {
            counts = new
            {
                users = await _db.Users.CountAsync(ct),
                creators = await _db.Creators.CountAsync(ct),
                creatorProfiles = await _db.CreatorProfiles.CountAsync(ct),
                tracks = await _db.Tracks.CountAsync(ct),
                purchases = await _db.Purchases.CountAsync(ct),
                library = await _db.Library.CountAsync(ct),
                subscriptions = await _db.Subscriptions.CountAsync(ct),
                entitlements = await _db.Entitlements.CountAsync(ct),
                walletTransactions = await _db.WalletTransactions.CountAsync(ct),
                releaseCreditPurchases = await _db.ReleaseCreditPurchases.CountAsync(ct),
                masteringJobs = await _db.MasteringJobs.CountAsync(ct),
                earningsTransactions = await _db.EarningsTransactions.CountAsync(ct),
                fanSubscriptions = await _db.FanSubscriptions.CountAsync(ct),
                trackAuthorships = await _db.TrackAuthorships.CountAsync(ct),
                authorshipRecords = await _db.AuthorshipRecords.CountAsync(ct),
                stripeWebhookEvents = await _db.StripeWebhookEvents.CountAsync(ct),
            },
            manifest = await BuildManifestAsync(ct),
        };
    }

    private async Task<object> PaymentStateAsync(string? email, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(email, ct);

        var purchases = await _db.Purchases
            .Where(p => p.BuyerId == userId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new { id = p.Id, trackId = p.TrackId, amountCents = p.AmountCents, status = p.Status, stripeSessionId = p.StripeSessionId })
            .ToListAsync(ct);

        var subscriptions = await _db.Subscriptions
            .Where(s => s.UserId == userId)
            .Select(s => new { id = s.Id, plan = s.Plan, status = s.Status, stripeCustomerId = s.StripeCustomerId, expiresAt = s.ExpiresAt })
            .ToListAsync(ct);

        var user = await _users.FindByIdAsync(userId);
        var walletBalanceCents = await _db.WalletTransactions
            .Where(w => w.UserId == userId)
            .SumAsync(w => (long?)w.AmountCents, ct) ?? 0;

        return new
        {
            userId,
            email,
            subscriptionStatus = user?.SubscriptionStatus,
            tier = user?.Tier,
            creatorTier = user?.CreatorTier.ToString(),
            walletBalanceCents,
            purchases,
            subscriptions,
        };
    }

    private async Task<object> EntitlementStateAsync(string? email, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(email, ct);
        var entitlements = await _entitlements.GetForUserAsync(userId, includeRevoked: true, ct: ct);

        return new
        {
            userId,
            email,
            entitlements = entitlements.Select(e => new
            {
                id = e.Id,
                resourceType = e.ResourceType.ToString(),
                resourceId = e.ResourceId,
                accessLevel = e.AccessLevel.ToString(),
                sourceType = e.SourceType.ToString(),
                revoked = e.RevokedAt != null,
            }),
        };
    }

    private async Task<object> CreditStateAsync(string? email, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(email ?? ProEmail, ct);
        var status = await _credits.GetStatusAsync(userId, ct);
        return new
        {
            userId,
            email = email ?? ProEmail,
            status.Allowance,
            status.Used,
            status.Remaining,
            status.Plan,
            status.MonthlyRemaining,
            status.Purchased,
        };
    }

    private async Task<object> SupportStateAsync(string? email, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(email ?? CreatorEmail, ct);

        var earnings = await _db.EarningsTransactions
            .Where(e => e.ArtistUserId == userId)
            .GroupBy(e => e.Source)
            .Select(g => new { source = g.Key, count = g.Count(), netCents = g.Sum(x => x.NetCents) })
            .ToListAsync(ct);

        var fanSubsAsArtist = await _db.FanSubscriptions.CountAsync(f => f.ArtistUserId == userId && f.Status == "active", ct);
        var fanSubsAsFan = await _db.FanSubscriptions.CountAsync(f => f.FanUserId == userId && f.Status == "active", ct);

        return new
        {
            userId,
            email = email ?? CreatorEmail,
            earningsBySource = earnings,
            activeFanSubscribers = fanSubsAsArtist,
            activeFanSubscriptions = fanSubsAsFan,
        };
    }

    private async Task<object> AuthorshipStateAsync(string? email, Guid? trackId, CancellationToken ct)
    {
        if (trackId is { } id)
        {
            return new
            {
                trackId = id,
                hasAuthorship = await _db.TrackAuthorships.AnyAsync(t => t.TrackId == id, ct),
                authorshipRecordStatus = await _db.AuthorshipRecords
                    .Where(r => r.TrackId == id)
                    .Select(r => r.Status)
                    .FirstOrDefaultAsync(ct),
                commercialRightsVerified = await _db.Tracks
                    .Where(t => t.Id == id)
                    .Select(t => (bool?)t.CommercialRightsVerified)
                    .FirstOrDefaultAsync(ct),
            };
        }

        var creatorUserId = await ResolveUserIdAsync(email ?? CreatorEmail, ct);
        var tracks = await _db.Tracks
            .Where(t => t.CreatorId == creatorUserId)
            .OrderBy(t => t.CambrianTrackId)
            .Select(t => new
            {
                trackId = t.Id,
                cambrianTrackId = t.CambrianTrackId,
                visibility = t.Visibility,
                hasAuthorship = _db.TrackAuthorships.Any(a => a.TrackId == t.Id),
                commercialRightsVerified = t.CommercialRightsVerified,
            })
            .ToListAsync(ct);

        return new { creatorUserId, email = email ?? CreatorEmail, tracks };
    }

    // ────────────────────────── Stripe simulation (no real calls) ──────────────────────────

    /// <summary>Simulate a successful checkout for a subscription, credit pack, or authorship record.</summary>
    public async Task<E2eWebhookResult> SimulateCheckoutCompletedAsync(
        string email, string kind, string? tier, int? credits, Guid? recordId, string? eventId, string? sessionId,
        CancellationToken ct = default)
    {
        var userId = await ResolveUserIdAsync(email, ct);
        var evt = eventId ?? $"evt_e2e_{Guid.NewGuid():N}";
        var session = sessionId ?? $"cs_e2e_{Guid.NewGuid():N}";

        var creditCount = credits ?? 10;
        var creditPack = CreditPackCatalog.FindByCredits(creditCount);
        var (clientReferenceId, amountTotal) = kind.ToLowerInvariant() switch
        {
            "subscription" => ($"{userId}:subscription:{tier ?? "creator"}", (long?)TierManifest.For(tier ?? "creator").PriceCents),
            "credits" when creditPack is not null => ($"{userId}:credits:{creditCount}", (long?)creditPack.PriceCents),
            "credits" => throw new ArgumentException($"Unsupported E2E credit count '{creditCount}'.", nameof(credits)),
            "authorship" => ($"{userId}:authorship:{recordId ?? SeedAuthorshipRecordId}", (long?)PricingContract.AuthorshipRecordDefaultCents),
            _ => throw new ArgumentException($"Unknown checkout kind '{kind}'.", nameof(kind)),
        };

        var data = new Dictionary<string, object?>
        {
            ["object"] = "checkout.session",
            ["id"] = session,
            ["client_reference_id"] = clientReferenceId,
            ["amount_total"] = amountTotal,
            ["customer"] = CustomerIdFor(userId),
            ["payment_status"] = "paid",
            ["currency"] = "usd",
        };
        if (kind.Equals("subscription", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedTier = TierManifest.For(tier ?? "creator");
            data["subscription"] = new Dictionary<string, object?>
            {
                ["id"] = $"sub_e2e_{userId[..8]}",
                ["status"] = "active",
                ["items"] = new Dictionary<string, object?>
                {
                    ["data"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["price"] = new Dictionary<string, object?>
                            {
                                ["id"] = _config[resolvedTier.StripePriceConfigKey!]
                            }
                        }
                    }
                }
            };
        }

        return await ProcessAsync(evt, "checkout.session.completed", data, ct);
    }

    /// <summary>Simulate a failed renewal (invoice.payment_failed → PastDue).</summary>
    public Task<E2eWebhookResult> SimulatePaymentFailedAsync(string email, string? eventId, CancellationToken ct = default)
        => SimulateCustomerEventAsync(email, "invoice.payment_failed", "invoice", eventId, ct);

    /// <summary>Simulate a cancellation (customer.subscription.deleted → free/cancelled).</summary>
    public Task<E2eWebhookResult> SimulateSubscriptionCancelledAsync(string email, string? eventId, CancellationToken ct = default)
        => SimulateCustomerEventAsync(email, "customer.subscription.deleted", "subscription", eventId, ct);

    private async Task<E2eWebhookResult> SimulateCustomerEventAsync(
        string email, string type, string stripeObject, string? eventId, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(email, ct);
        var evt = eventId ?? $"evt_e2e_{Guid.NewGuid():N}";
        var data = new Dictionary<string, object?>
        {
            ["object"] = stripeObject,
            ["id"] = stripeObject == "subscription"
                ? $"sub_e2e_{userId[..8]}"
                : $"{stripeObject}_e2e_{userId[..8]}",
            ["customer"] = CustomerIdFor(userId),
        };
        if (type.StartsWith("invoice.", StringComparison.Ordinal))
        {
            data["subscription"] = $"sub_e2e_{userId[..8]}";
        }
        return await ProcessAsync(evt, type, data, ct);
    }

    /// <summary>
    /// Simulate an abandoned checkout. Real Stripe fires NO webhook when a customer cancels at
    /// the payment page, so this is intentionally a no-op that fulfills nothing — it exists so a
    /// frontend test can assert "cancelling changes no state".
    /// </summary>
    public Task<E2eWebhookResult> SimulateCheckoutCancelledAsync(string email, CancellationToken ct = default)
        => Task.FromResult(new E2eWebhookResult($"evt_e2e_cancelled_{email}", "checkout.session.cancelled", Processed: false, Deduplicated: false));

    private async Task<E2eWebhookResult> ProcessAsync(string eventId, string type, Dictionary<string, object?> dataObject, CancellationToken ct)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["object"] = "event",
            ["id"] = eventId,
            ["type"] = type,
            ["data"] = new Dictionary<string, object?> { ["object"] = dataObject },
        };

        var payload = JsonSerializer.Serialize(envelope);
        var secret = _config["Stripe:WebhookSecret"];
        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException("[E2E] Stripe:WebhookSecret is not configured; cannot sign simulated events.");

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = E2eSupport.SignStripePayload(payload, secret, ts);

        var alreadyCompleted = await _db.StripeWebhookEvents
            .AnyAsync(e => e.EventId == eventId && e.Status == "completed", ct);

        await _webhooks.HandleStripeAsync(payload, signature);

        var processed = await _db.StripeWebhookEvents
            .AnyAsync(e => e.EventId == eventId && e.Status == "completed", ct);

        return new E2eWebhookResult(eventId, type, processed, Deduplicated: alreadyCompleted);
    }

    // ────────────────────────────── Helpers ──────────────────────────────

    private static string CustomerIdFor(string userId) => $"cus_e2e_{userId[..8]}";

    private async Task<string> ResolveUserIdAsync(string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("An 'email' is required for this state domain.", nameof(email));

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, ct)
            ?? throw new ArgumentException($"No seeded user with email '{email}'.", nameof(email));
        return user.Id;
    }

    private async Task<E2eManifest> BuildManifestAsync(CancellationToken ct)
    {
        var accounts = new List<E2eAccount>
        {
            new("listener", ListenerEmail, SeedPassword, ListenerUserId, nameof(CreatorTier.Free), null),
            new("creator", CreatorEmail, SeedPassword, CreatorUserId, nameof(CreatorTier.Creator), CreatorHandle),
            new("pro", ProEmail, SeedPassword, ProUserId, nameof(CreatorTier.Pro), null),
            new("zero-track-creator", EmptyCreatorEmail, SeedPassword, EmptyCreatorUserId, nameof(CreatorTier.Creator), EmptyCreatorHandle),
        };

        var tracks = new List<E2eSeedTrack>
        {
            new("playable", PlayableTrackId.ToString(), "CAMB-TRK-E2EPLAY1", "public",
                await AudioAvailableAsync(PlayableAudioKey), await HasAuthorshipAsync(PlayableTrackId, ct)),
            new("missing-audio", MissingAudioTrackId.ToString(), "CAMB-TRK-E2EMISS1", "public",
                await AudioAvailableAsync(MissingAudioKey), await HasAuthorshipAsync(MissingAudioTrackId, ct)),
            new("no-authorship", NoAuthorshipTrackId.ToString(), "CAMB-TRK-E2ENOAU1", "public",
                await AudioAvailableAsync(NoAuthorshipAudioKey), await HasAuthorshipAsync(NoAuthorshipTrackId, ct)),
            new("draft", DraftTrackId.ToString(), "CAMB-TRK-E2EDRFT1", "hidden",
                await AudioAvailableAsync(DraftAudioKey), await HasAuthorshipAsync(DraftTrackId, ct)),
        };

        return new E2eManifest(true, SeedPassword, CreatorHandle, accounts, tracks);
    }

    private async Task<bool> AudioAvailableAsync(string key)
    {
        var file = await _storage.OpenReadAsync(key);
        if (file is null) return false;
        file.Dispose();
        return true;
    }

    private Task<bool> HasAuthorshipAsync(Guid trackId, CancellationToken ct)
        => _db.TrackAuthorships.AnyAsync(t => t.TrackId == trackId, ct);
}
