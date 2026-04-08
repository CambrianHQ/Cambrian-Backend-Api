using Cambrian.Application.DTOs.Waitlist;

namespace Cambrian.Application.Interfaces;

public interface IWaitlistService
{
    /// <summary>
    /// Persist a waitlist signup. Idempotent on email — re-signups return
    /// AlreadySignedUp = true without inserting a duplicate row.
    /// Throws ArgumentException on invalid email format.
    /// </summary>
    Task<WaitlistSignupResponse> SignupAsync(WaitlistSignupRequest request);
}
