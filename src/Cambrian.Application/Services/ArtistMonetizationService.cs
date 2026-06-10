using Cambrian.Application.DTOs.Monetization;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <inheritdoc cref="IArtistMonetizationService" />
public sealed class ArtistMonetizationService : IArtistMonetizationService
{
    private const int MinTipCents = 100;          // $1
    private const int MaxTipCents = 100_000;      // $1,000
    private const int MinSubscriptionCents = 100; // $1
    private const int MaxSubscriptionCents = 50_000; // $500

    private readonly UserManager<ApplicationUser> _users;
    private readonly ICreatorIdentityRepository _creators;
    private readonly IFanSubscriptionRepository _fanSubs;
    private readonly IPaymentGateway _gateway;
    private readonly IConfiguration _config;
    private readonly ILogger<ArtistMonetizationService> _logger;

    public ArtistMonetizationService(
        UserManager<ApplicationUser> users,
        ICreatorIdentityRepository creators,
        IFanSubscriptionRepository fanSubs,
        IPaymentGateway gateway,
        IConfiguration config,
        ILogger<ArtistMonetizationService> logger)
    {
        _users = users;
        _creators = creators;
        _fanSubs = fanSubs;
        _gateway = gateway;
        _config = config;
        _logger = logger;
    }

    public async Task<MonetizationCheckoutResponse> CreateTipCheckoutAsync(
        string artistIdentifier, int amountCents, string payerUserId, CancellationToken ct = default)
    {
        if (amountCents is < MinTipCents or > MaxTipCents)
            throw new ArgumentException($"Tip amount must be between {MinTipCents} and {MaxTipCents} cents.");

        var artist = await ResolveArtistAsync(artistIdentifier);
        var accountId = await RequirePayoutsEnabledAsync(artist);

        var frontendUrl = FrontendUrl();
        var checkoutUrl = await _gateway.CreateConnectedCheckoutAsync(
            accountId,
            amountCents,
            $"Tip for {artist.DisplayName ?? artist.UserName}",
            clientReferenceId: $"{payerUserId}:tip:{artist.Id}",
            successUrl: $"{frontendUrl}/artists/{artistIdentifier}?tip=thanks",
            cancelUrl: $"{frontendUrl}/artists/{artistIdentifier}",
            applicationFeeCents: 0); // 0% platform fee on tips at launch

        _logger.LogInformation(
            "EVENT: TipCheckoutCreated artistId:{ArtistId} payerId:{PayerId} amountCents:{Amount}",
            artist.Id, payerUserId, amountCents);

        return new MonetizationCheckoutResponse { CheckoutUrl = checkoutUrl };
    }

    public async Task<MonetizationCheckoutResponse> CreateFanSubscriptionCheckoutAsync(
        string artistIdentifier, string payerUserId, CancellationToken ct = default)
    {
        var artist = await ResolveArtistAsync(artistIdentifier);

        if (string.Equals(artist.Id, payerUserId, StringComparison.Ordinal))
            throw new ArgumentException("You cannot subscribe to yourself.");

        // The price is always the artist's configured price — never fan-supplied.
        if (artist.FanSubscriptionPriceCents is not int priceCents
            || priceCents is < MinSubscriptionCents or > MaxSubscriptionCents)
            throw new InvalidOperationException("This artist has not enabled fan subscriptions.");

        var accountId = await RequirePayoutsEnabledAsync(artist);

        if (await _fanSubs.GetLiveByFanAndArtistAsync(payerUserId, artist.Id, ct) is not null)
            throw new InvalidOperationException("You already have an active subscription to this artist.");

        var fanSub = new FanSubscription
        {
            Id = Guid.NewGuid(),
            FanUserId = payerUserId,
            ArtistUserId = artist.Id,
            PriceCents = priceCents,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
        };
        await _fanSubs.AddAsync(fanSub, ct);

        var frontendUrl = FrontendUrl();
        var checkoutUrl = await _gateway.CreateConnectedSubscriptionCheckoutAsync(
            accountId,
            priceCents,
            $"Monthly support for {artist.DisplayName ?? artist.UserName}",
            clientReferenceId: $"{payerUserId}:fansub:{fanSub.Id}",
            successUrl: $"{frontendUrl}/artists/{artistIdentifier}?subscribed=true",
            cancelUrl: $"{frontendUrl}/artists/{artistIdentifier}",
            applicationFeePercent: IArtistMonetizationService.FanSubscriptionFeePercent);

        _logger.LogInformation(
            "EVENT: FanSubscriptionCheckoutCreated artistId:{ArtistId} fanId:{FanId} priceCents:{Price} fanSubId:{FanSubId}",
            artist.Id, payerUserId, priceCents, fanSub.Id);

        return new MonetizationCheckoutResponse { CheckoutUrl = checkoutUrl };
    }

    public async Task SetSubscriptionPriceAsync(string artistUserId, int? priceCents, CancellationToken ct = default)
    {
        if (priceCents is int p && (p is < MinSubscriptionCents or > MaxSubscriptionCents))
            throw new ArgumentException(
                $"Subscription price must be between {MinSubscriptionCents} and {MaxSubscriptionCents} cents (or null to disable).");

        var artist = await _users.FindByIdAsync(artistUserId)
            ?? throw new KeyNotFoundException("User not found.");

        artist.FanSubscriptionPriceCents = priceCents;
        await _users.UpdateAsync(artist);

        _logger.LogInformation(
            "EVENT: FanSubscriptionPriceSet artistId:{ArtistId} priceCents:{Price}",
            artistUserId, priceCents);
    }

    // ── Helpers ──

    /// <summary>Resolve "{id}" as an ApplicationUser id, Creator UUID, or creator username.</summary>
    private async Task<ApplicationUser> ResolveArtistAsync(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new KeyNotFoundException("Artist not found.");

        var user = await _users.FindByIdAsync(identifier);
        if (user is not null)
            return user;

        var creator = Guid.TryParse(identifier, out var creatorUuid)
            ? await _creators.GetByIdAsync(creatorUuid)
            : await _creators.GetByUsernameAsync(identifier.Trim().ToLowerInvariant());

        if (creator?.UserId is string userId && await _users.FindByIdAsync(userId) is { } artist)
            return artist;

        throw new KeyNotFoundException("Artist not found.");
    }

    /// <summary>Guard: 409 unless the artist's Connect account can receive payouts.</summary>
    private async Task<string> RequirePayoutsEnabledAsync(ApplicationUser artist)
    {
        if (string.IsNullOrWhiteSpace(artist.StripeAccountId))
            throw new ArtistPayoutsNotEnabledException();

        var status = await _gateway.GetConnectAccountStatusAsync(artist.StripeAccountId!);
        if (!status.PayoutsEnabled)
            throw new ArtistPayoutsNotEnabledException();

        return artist.StripeAccountId!;
    }

    private string FrontendUrl()
    {
        var url = (_config["App:FrontendUrl"] ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("App:FrontendUrl must be configured for monetization checkout.");
        return url;
    }
}
