using Cambrian.Application.DTOs.ApiKeys;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("api/keys")]
[Authorize]
public class ApiKeysController : BaseController
{
    private readonly IApiKeyService _apiKeyService;

    public ApiKeysController(IApiKeyService apiKeyService)
    {
        _apiKeyService = apiKeyService;
    }

    /// <summary>Generate a new API key. The raw key is returned only once.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateKey([FromBody] CreateApiKeyRequest request)
    {
        var userId = GetRequiredUserId();
        if (userId is null) return ErrorResponse("User not authenticated.");

        var result = await _apiKeyService.CreateKeyAsync(userId, request);
        return CreatedResponse(result, "API key created. Store this key securely — it will not be shown again.");
    }

    /// <summary>List the current user's API keys (masked).</summary>
    [HttpGet]
    public async Task<IActionResult> GetKeys()
    {
        var userId = GetRequiredUserId();
        if (userId is null) return ErrorResponse("User not authenticated.");

        var result = await _apiKeyService.GetKeysAsync(userId);
        return OkResponse(result);
    }

    /// <summary>Deactivate an API key (soft delete).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeactivateKey(Guid id)
    {
        var userId = GetRequiredUserId();
        if (userId is null) return ErrorResponse("User not authenticated.");

        var success = await _apiKeyService.DeactivateKeyAsync(userId, id);
        if (!success) return NotFoundResponse("API key not found.");
        return MessageResponse("API key deactivated.");
    }
}
