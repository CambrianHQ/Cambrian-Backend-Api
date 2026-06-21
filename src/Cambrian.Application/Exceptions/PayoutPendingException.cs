namespace Cambrian.Application.Exceptions;

/// <summary>
/// The wallet debit is durable, but Stripe transfer completion is not yet confirmed.
/// Retrying the same amount resumes the same provider-idempotent payout.
/// </summary>
public sealed class PayoutPendingException : Exception
{
    public PayoutPendingException()
        : base("Payout transfer is still processing. Retry the same amount; your wallet will not be debited again.")
    {
    }
}
