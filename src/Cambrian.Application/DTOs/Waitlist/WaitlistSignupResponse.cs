namespace Cambrian.Application.DTOs.Waitlist;

/// <summary>
/// Response shape for POST /waitlist. Always returns success on a valid email
/// (the operation is idempotent — duplicate signups don't fail). The
/// AlreadySignedUp flag lets the frontend distinguish "thanks!" from
/// "you're already on the list" without leaking it via different HTTP codes.
/// </summary>
public class WaitlistSignupResponse
{
    public bool AlreadySignedUp { get; set; }
}
