namespace Cambrian.Application.DTOs.FoundingCreator;

public class FoundingCreatorDto
{
    public string UserId { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public bool IsFoundingCreator { get; set; }
    public DateTime? EnrolledAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? DaysRemaining { get; set; }
}

public class FoundingCreatorStatusDto
{
    public bool IsFoundingCreator { get; set; }
    public DateTime? EnrolledAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int DaysRemaining { get; set; }
    public decimal CurrentFeeRate { get; set; }
}
