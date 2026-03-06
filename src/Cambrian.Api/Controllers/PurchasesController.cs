using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("purchases")]
[Authorize]
public class PurchasesController : BaseController
{
    [HttpPost]
    public IActionResult Create()
    {
        return CreatedResponse<object?>(null, "Purchase initiated.");
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("credit-creator")]
    public IActionResult CreditCreator()
    {
        return MessageResponse("Creator credited.");
    }
}
