namespace Cambrian.Application.DTOs.Waitlist;

/// <summary>POST /waitlist body. Email is required; Source is optional attribution.</summary>
public class WaitlistSignupRequest
{
    public string Email { get; set; } = "";

    public string? Source { get; set; }
}
