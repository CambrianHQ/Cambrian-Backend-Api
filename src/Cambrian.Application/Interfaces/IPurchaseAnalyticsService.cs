namespace Cambrian.Application.Interfaces;

public interface IPurchaseAnalyticsService
{
    Task CaptureAsync(PurchaseAnalyticsEvent purchaseEvent, CancellationToken ct = default);
}

public sealed class PurchaseAnalyticsEvent
{
    public required string EventName { get; init; }
    public required string StripeEventId { get; init; }
    public required string DistinctId { get; init; }
    public IReadOnlyDictionary<string, object?> Properties { get; init; } = new Dictionary<string, object?>();
}
