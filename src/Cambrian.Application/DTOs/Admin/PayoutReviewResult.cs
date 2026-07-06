namespace Cambrian.Application.DTOs.Admin;

public enum PayoutReviewOutcome
{
    NotFound,
    InvalidState,
    Rejected,
    Approved,

    /// <summary>Approve was attempted (payout was pending) but the Stripe transfer retry
    /// failed again — the payout stays pending. Never report this as a bare success.</summary>
    ApprovalRetryFailed,
}

public class PayoutReviewResult
{
    public PayoutReviewOutcome Outcome { get; set; }

    public AdminPayout? Payout { get; set; }

    public string Message { get; set; } = string.Empty;
}
