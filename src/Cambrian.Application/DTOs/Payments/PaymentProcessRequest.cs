using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Payments;

public class PaymentProcessRequest
{
    [Required]
    public string PurchaseId { get; set; } = string.Empty;

    /// <summary>
    /// Stripe PaymentMethod ID (e.g. pm_xxx). Card details are collected
    /// client-side via Stripe.js — never sent to our server.
    /// </summary>
    public string? PaymentMethodId { get; set; }
}
