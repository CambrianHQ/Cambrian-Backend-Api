namespace Cambrian.Application.DTOs.Admin;

public class AdminPayout
{
    public string Id { get; set; } = string.Empty;

    public string CreatorId { get; set; } = string.Empty;

    public string? CreatorEmail { get; set; }

    public int AmountCents { get; set; }

    public string Status { get; set; } = "pending";

    public DateTime RequestedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
