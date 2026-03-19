using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("tiers")]
public class TierController : BaseController
{
    private readonly ITierService _tiers;

    public TierController(ITierService tiers)
    {
        _tiers = tiers;
    }

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return OkResponse(_tiers.GetTierConfig());
    }
}
