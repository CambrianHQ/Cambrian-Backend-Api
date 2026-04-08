using System.Net.Mail;
using Cambrian.Application.DTOs.Waitlist;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class WaitlistService : IWaitlistService
{
    private readonly IWaitlistRepository _repo;
    private readonly ILogger<WaitlistService> _logger;

    public WaitlistService(IWaitlistRepository repo, ILogger<WaitlistService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<WaitlistSignupResponse> SignupAsync(WaitlistSignupRequest request)
    {
        if (request is null)
            throw new ArgumentException("Request body is required.");

        var rawEmail = request.Email?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawEmail))
            throw new ArgumentException("Email is required.");

        // Validate format using the framework's standard parser. Rejects
        // anything that wouldn't survive being put into a `To:` header.
        if (!MailAddress.TryCreate(rawEmail, out _))
            throw new ArgumentException("Email is not a valid address.");

        var normalizedEmail = rawEmail.ToLowerInvariant();

        var existing = await _repo.GetByEmailAsync(normalizedEmail);
        if (existing is not null)
        {
            _logger.LogInformation("Waitlist signup deduped for {EmailHash}", HashEmailForLog(normalizedEmail));
            return new WaitlistSignupResponse { AlreadySignedUp = true };
        }

        var signup = new WaitlistSignup
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            Source = string.IsNullOrWhiteSpace(request.Source)
                ? null
                : request.Source.Trim(),
            CreatedAt = DateTime.UtcNow,
        };

        await _repo.AddAsync(signup);

        _logger.LogInformation(
            "Waitlist signup recorded: hash={EmailHash} source={Source}",
            HashEmailForLog(normalizedEmail), signup.Source ?? "(none)");

        return new WaitlistSignupResponse { AlreadySignedUp = false };
    }

    /// <summary>
    /// One-way hash of the normalized email so audit logs do not contain raw PII.
    /// First 16 hex chars of SHA-256 — short enough for log readability, long
    /// enough that collisions are not a practical concern at our volume.
    /// </summary>
    private static string HashEmailForLog(string normalizedEmail)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalizedEmail));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
