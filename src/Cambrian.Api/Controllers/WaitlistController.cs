using Cambrian.Application.DTOs.Waitlist;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Public waitlist signup (issue #72). Anonymous endpoint, rate-limited via
/// the existing "auth" policy (10 req/min/IP in production) so a script
/// can't fire-hose us with junk emails.
/// </summary>
[Route("waitlist")]
[AllowAnonymous]
[EnableRateLimiting("auth")]
public class WaitlistController : BaseController
{
    private readonly IWaitlistService _waitlist;

    public WaitlistController(IWaitlistService waitlist)
    {
        _waitlist = waitlist;
    }

    /// <summary>POST /waitlist — record a public signup (idempotent on email).</summary>
    [HttpPost]
    public async Task<IActionResult> Signup([FromBody] WaitlistSignupRequest request)
    {
        try
        {
            var result = await _waitlist.SignupAsync(request);
            return result.AlreadySignedUp
                ? OkResponse(result, "You're already on the waitlist.")
                : CreatedResponse(result, "Thanks — you're on the waitlist.");
        }
        catch (ArgumentException ex)
        {
            return ErrorResponse(ex.Message);
        }
    }
}
