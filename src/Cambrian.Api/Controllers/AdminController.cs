using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("admin")]
[Authorize(Roles = "Admin")]
public class AdminController : BaseController
{
    private readonly IAdminService _admin;

    public AdminController(IAdminService admin)
    {
        _admin = admin;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        return OkResponse(await _admin.GetDashboardAsync());
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit()
    {
        return OkResponse(await _admin.GetAuditLogsAsync());
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        return OkResponse(await _admin.GetUsersAsync());
    }
}