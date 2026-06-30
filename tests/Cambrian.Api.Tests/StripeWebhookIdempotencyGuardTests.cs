using System.Reflection;
using Cambrian.Infrastructure.Stripe;

namespace Cambrian.Api.Tests;

/// <summary>
/// REGRESSION: webhook idempotency guard (<c>StripeWebhookService.IsDuplicateEventInsert</c>).
///
/// Concurrent/duplicate Stripe deliveries race the check-then-insert dedup and surface as
/// Postgres <c>23505</c> unique-violations on one of our idempotency keys — the webhook
/// event row (<c>IX_StripeWebhookEvents_EventId</c>) or a fulfillment dedup keyed by the
/// Stripe session (<c>ux_release_credit_purchases_session</c>). The winning delivery already
/// did the work, so these must be treated as benign duplicates (→ 200), NOT bubble up as a
/// 500 that makes Stripe retry the delivery forever.
///
/// Found + fixed while verifying the paid matrix against a local Stripe test-mode backend:
/// before, a duplicate credit-pack / subscription delivery returned 500; after, 0 500s.
/// This locks the string-matching contract so renaming a constraint (or narrowing the match)
/// can't silently reintroduce the retry storm. The guard is reached via reflection so the
/// production method can stay private; a rename makes <see cref="Invoke"/> fail loudly.
/// </summary>
public sealed class StripeWebhookIdempotencyGuardTests
{
    private static bool Invoke(Exception ex)
    {
        var method = typeof(StripeWebhookService).GetMethod(
            "IsDuplicateEventInsert",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method); // guards against a silent rename of the production method
        return (bool)method!.Invoke(null, new object[] { ex })!;
    }

    [Theory]
    [InlineData("23505: duplicate key value violates unique constraint \"IX_StripeWebhookEvents_EventId\"")]
    [InlineData("duplicate key value violates unique constraint \"ux_release_credit_purchases_session\"")]
    public void IdempotencyKeyCollision_IsTreatedAsBenignDuplicate(string message)
    {
        Assert.True(Invoke(new Exception(message)));
    }

    [Fact]
    public void Collision_NestedInInnerException_IsStillRecognised()
    {
        // EF Core wraps the Npgsql error: DbUpdateException → PostgresException.
        var inner = new Exception(
            "23505: duplicate key value violates unique constraint \"ux_release_credit_purchases_session\"");
        var outer = new InvalidOperationException(
            "An error occurred while saving the entity changes. See the inner exception for details.", inner);

        Assert.True(Invoke(outer));
    }

    [Fact]
    public void UnrelatedUniqueViolation_IsNotSwallowed()
    {
        // A 23505 on a NON-idempotency business constraint is a real error and must still
        // surface (so it is not silently converted into a webhook success).
        Assert.False(Invoke(new Exception(
            "23505: duplicate key value violates unique constraint \"ux_usernames\"")));
    }

    [Fact]
    public void ArbitraryError_IsNotADuplicate()
    {
        Assert.False(Invoke(new Exception("connection reset by peer")));
    }
}
