namespace Cambrian.Domain.Entities;

public class Referral
{
    public Guid Id { get; set; }

    public string ReferrerId { get; set; } = null!;

    public string? ReferredUserId { get; set; }

    public string ReferralCode { get; set; } = null!;

    public DateTime ReferredAt { get; set; } = DateTime.UtcNow;

    public DateTime? ConvertedAt { get; set; }

    public decimal CommissionRate { get; set; } = 0.10m;

    public decimal TotalCommissionEarned { get; set; }

    public ApplicationUser Referrer { get; set; } = null!;
}
