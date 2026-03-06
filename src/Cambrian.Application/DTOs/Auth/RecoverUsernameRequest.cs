namespace Cambrian.Application.DTOs.Auth;

public class RecoverUsernameRequest
{
    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }
}
