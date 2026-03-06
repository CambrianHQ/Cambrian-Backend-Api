namespace Cambrian.Application.DTOs.Wallet;

public class WalletTransactionResponse
{
    public string Id { get; set; } = "";

    public long AmountCents { get; set; }

    public string Type { get; set; } = "";

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }
}
