using System.Collections.Concurrent;
using Cambrian.Application.Interfaces;

namespace Cambrian.Infrastructure.Diagnostics;

public sealed class LocalDeliveryDebugStore : ILocalDeliveryDebugStore
{
    private const int MaxEntries = 250;
    private readonly ConcurrentQueue<LocalDeliveryMessage> _entries = new();

    public void CaptureEmail(string to, string subject, string? body = null, string? code = null, string kind = "generic")
    {
        Enqueue(new LocalDeliveryMessage
        {
            CreatedAtUtc = DateTime.UtcNow,
            Channel = "email",
            Recipient = to,
            Kind = kind,
            Subject = subject,
            Preview = BuildPreview(body),
            Code = code
        });
    }

    public void CaptureSms(string toPhoneNumber, string message, string? code = null, string kind = "generic")
    {
        Enqueue(new LocalDeliveryMessage
        {
            CreatedAtUtc = DateTime.UtcNow,
            Channel = "sms",
            Recipient = toPhoneNumber,
            Kind = kind,
            Preview = BuildPreview(message),
            Code = code
        });
    }

    public IReadOnlyCollection<LocalDeliveryMessage> GetRecent(int limit = 25, string? recipient = null, string? kind = null)
    {
        return _entries
            .Where(entry => string.IsNullOrWhiteSpace(recipient)
                || string.Equals(entry.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(kind)
                || string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    public LocalDeliveryMessage? GetLatestPasswordReset(string? email = null, string? phoneNumber = null)
    {
        var recipient = !string.IsNullOrWhiteSpace(email) ? email : phoneNumber;
        var channel = !string.IsNullOrWhiteSpace(email) ? "email"
            : !string.IsNullOrWhiteSpace(phoneNumber) ? "sms"
            : null;

        return _entries
            .Where(entry => string.Equals(entry.Kind, "password_reset", StringComparison.OrdinalIgnoreCase))
            .Where(entry => channel is null
                || string.Equals(entry.Channel, channel, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(recipient)
                || string.Equals(entry.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .FirstOrDefault();
    }

    private void Enqueue(LocalDeliveryMessage entry)
    {
        _entries.Enqueue(entry);

        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    private static string? BuildPreview(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var flattened = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        return flattened.Length <= 160 ? flattened : flattened[..160];
    }
}
