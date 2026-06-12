namespace Cambrian.Application.Exceptions;

/// <summary>
/// Thrown when money-in is attempted for an artist whose Stripe Connect account
/// is missing or has payouts disabled. Controllers map this to 409 Conflict.
/// </summary>
public sealed class ArtistPayoutsNotEnabledException : Exception
{
    public ArtistPayoutsNotEnabledException()
        : base("This artist has not enabled payouts yet — tips and subscriptions are unavailable.")
    {
    }
}
