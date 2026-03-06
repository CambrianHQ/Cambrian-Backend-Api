using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly IAdminService _admin;

    public AdminController(IAdminService admin)
    {
        _admin = admin;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        return Ok(await _admin.GetDashboardAsync());
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit()
    {
        return Ok(await _admin.GetAuditLogsAsync());
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        return Ok(await _admin.GetUsersAsync());
    }
}