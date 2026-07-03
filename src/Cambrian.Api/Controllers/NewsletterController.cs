using System.ComponentModel.DataAnnotations;
using Cambrian.Application.DTOs.Newsletter;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Public newsletter signup. Correct semantics (F2): a valid new email → 200 + row;
/// a duplicate → 200 (idempotent no-op); an invalid email → 400. This path never
/// returns 503 — the email provider is synced by a follow-up job, so provider
/// outages cannot fail the request.
/// </summary>
[ApiController]
[Route("api/newsletter")]
[AllowAnonymous]
public sealed class NewsletterController : ControllerBase
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    private readonly INewsletterService _newsletter;
    private readonly ILogger<NewsletterController> _logger;

    public NewsletterController(INewsletterService newsletter, ILogger<NewsletterController> logger)
    {
        _newsletter = newsletter;
        _logger = logger;
    }

    [HttpPost]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Subscribe([FromBody] NewsletterSubscribeRequest? body, CancellationToken ct)
    {
        var email = body?.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
            return BadRequest(new { success = false, error = "A valid email address is required." });

        try
        {
            var result = await _newsletter.SubscribeAsync(email, body?.Source, ct);
            return Ok(new
            {
                success = true,
                subscribed = true,
                alreadySubscribed = result.AlreadySubscribed,
                message = result.AlreadySubscribed ? "You're already subscribed." : "Thanks for subscribing!",
            });
        }
        catch (Exception ex)
        {
            // The only failure mode here is a datastore write error. Surface a 500 (never
            // a 503) and log — the client can safely retry.
            _logger.LogError(ex, "Newsletter subscribe failed for a valid email.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { success = false, error = "Could not process your subscription right now. Please try again." });
        }
    }

    private static bool IsValidEmail(string email)
    {
        if (email.Length > 320) return false;

        // Exactly one '@', a non-empty local part...
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@')) return false;

        // ...and a domain with a dot and a non-empty TLD (rejects "a@b").
        var domain = email[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        if (dot <= 0 || dot == domain.Length - 1) return false;

        return EmailValidator.IsValid(email);
    }
}
