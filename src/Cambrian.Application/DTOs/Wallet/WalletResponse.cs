namespace Cambrian.Application.DTOs.Wallet;

public class WalletResponse
{
    public long BalanceCents { get; set; }

    public string Currency { get; set; } = "usd";
}
