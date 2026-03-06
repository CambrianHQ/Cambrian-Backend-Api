namespace Cambrian.Application.DTOs.Payments;

public class PaymentCheckoutRequest
{
    public string? TrackId { get; set; }

    public string? ClientReferenceId { get; set; }
}
