namespace Cambrian.Application.Interfaces;

public interface IAccountService
{
    Task<AccountResponse> GetAccountAsync(string userId);
}

public class AccountResponse
{
    public string Id { get; set; } = "";
    public string? Email { get; set; }
    public string Plan { get; set; } = "free";
    public string Region { get; set; } = "US";
    public string Status { get; set; } = "active";
}
