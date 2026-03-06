namespace Cambrian.Api.DTOs;

public class LoginResponse
{
    public string Token { get; set; } = "";

    public string Email { get; set; } = "";

    public string Tier { get; set; } = "";
}
