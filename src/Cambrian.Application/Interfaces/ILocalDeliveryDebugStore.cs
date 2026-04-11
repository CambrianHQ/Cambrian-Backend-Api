namespace Cambrian.Application.Interfaces;

public sealed class LocalDeliveryMessage
{
    public DateTime CreatedAtUtc { get; init; }
    public string Channel { get; init; } = string.Empty;
    public string Recipient { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? Subject { get; init; }
    public string? Preview { get; init; }
    public string? Code { get; init; }
}

/// <summary>
/// Stores recent non-production console email/SMS deliveries so local QA can
/// inspect reset codes without scraping backend stdout.
/// </summary>
public interface ILocalDeliveryDebugStore
{
    void CaptureEmail(string to, string subject, string? body = null, string? code = null, string kind = "generic");

    void CaptureSms(string toPhoneNumber, string message, string? code = null, string kind = "generic");

    IReadOnlyCollection<LocalDeliveryMessage> GetRecent(int limit = 25, string? recipient = null, string? kind = null);

    LocalDeliveryMessage? GetLatestPasswordReset(string? email = null, string? phoneNumber = null);
}
