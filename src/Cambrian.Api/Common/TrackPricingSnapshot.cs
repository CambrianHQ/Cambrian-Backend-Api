using Cambrian.Domain.Entities;

namespace Cambrian.Api.Common;

internal sealed record TrackPricingSnapshot(
    decimal Price,
    decimal NonExclusivePrice,
    decimal ExclusivePrice,
    decimal CopyrightBuyoutPrice,
    int NonExclusivePriceCents,
    int ExclusivePriceCents,
    int CopyrightBuyoutPriceCents)
{
    public static TrackPricingSnapshot FromTrack(Track track)
    {
        var legacyPriceCents = ToCents(track.Price);
        var nonExclusivePriceCents = track.NonExclusivePriceCents > 0
            ? track.NonExclusivePriceCents
            : legacyPriceCents;
        var exclusivePriceCents = track.ExclusivePriceCents > 0
            ? track.ExclusivePriceCents
            : legacyPriceCents;
        var copyrightBuyoutPriceCents = track.CopyrightBuyoutPriceCents > 0
            ? track.CopyrightBuyoutPriceCents
            : exclusivePriceCents;

        return new TrackPricingSnapshot(
            Price: track.Price > 0 ? track.Price : nonExclusivePriceCents / 100m,
            NonExclusivePrice: nonExclusivePriceCents / 100m,
            ExclusivePrice: exclusivePriceCents / 100m,
            CopyrightBuyoutPrice: copyrightBuyoutPriceCents / 100m,
            NonExclusivePriceCents: nonExclusivePriceCents,
            ExclusivePriceCents: exclusivePriceCents,
            CopyrightBuyoutPriceCents: copyrightBuyoutPriceCents);
    }

    private static int ToCents(decimal dollars) =>
        (int)Math.Round(dollars * 100m, MidpointRounding.AwayFromZero);
}
