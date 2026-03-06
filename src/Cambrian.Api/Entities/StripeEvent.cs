using System.ComponentModel.DataAnnotations;

namespace Cambrian.Api.Entities;

public class StripeEvent
{
    [Key]
    public string EventId { get; set; } = string.Empty;

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
