using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Payments;

public class PaymentProcessRequest
{
    [Required]
    public string PurchaseId { get; set; } = string.Empty;

    public string? PaymentMethodId { get; set; }

    [Obsolete("Use PaymentMethodId instead")]
    public string? CardNumber { get; set; }

    [Obsolete("Use PaymentMethodId instead")]
    public string? CardExpiry { get; set; }

    [Obsolete("Use PaymentMethodId instead")]
    public string? CardCvc { get; set; }

    [Obsolete("Use PaymentMethodId instead")]
    public string? CardName { get; set; }
}
