namespace Cambrian.Application.DTOs.Auth;

public class ForgotPasswordRequest
{
    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }
}
