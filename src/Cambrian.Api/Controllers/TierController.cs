using Cambrian.Application.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("tiers")]
public class TierController : BaseController
{
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        var configs = TierManifest.All.Select(t => new
        {
            tier = t.Slug,
            displayName = t.DisplayName,
            uploadLimit = t.UploadLimit,
            feeRate = t.FeeRate,
            priceCents = t.PriceCents,
            features = t.Features,
            analyticsAccess = t.AnalyticsAccess.ToString().ToLowerInvariant()
        });

        return OkResponse(new
        {
            version = TierManifest.ContractVersion,
            tiers = configs
        });
    }
}
