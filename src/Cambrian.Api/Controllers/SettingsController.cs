using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("settings")]
[Authorize]
public class SettingsController : BaseController
{
    [HttpGet("profile")]
    public IActionResult GetProfile()
    {
        return OkResponse(new { displayName = (string?)null, email = (string?)null });
    }

    [HttpPut("password")]
    public IActionResult UpdatePassword()
    {
        return MessageResponse("Password updated.");
    }

    [HttpPut("email")]
    public IActionResult UpdateEmail()
    {
        return MessageResponse("Email updated.");
    }
}
