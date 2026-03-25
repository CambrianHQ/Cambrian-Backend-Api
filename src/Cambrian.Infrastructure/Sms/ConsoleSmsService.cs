using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cambrian.Infrastructure.Sms;

/// <summary>
/// Development SMS service that logs messages to the console instead of sending them.
/// </summary>
public sealed class ConsoleSmsService : ISmsService
{
    private readonly ILogger<ConsoleSmsService> _logger;

    public ConsoleSmsService(ILogger<ConsoleSmsService> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string toPhoneNumber, string message)
    {
        _logger.LogInformation("[SMS] To: {To} | Message: {Message}", toPhoneNumber, message);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(string toPhoneNumber, string code)
    {
        // SECURITY: Only log that a code was sent, never the code itself
        _logger.LogInformation("[SMS] Password reset code sent to {To}", toPhoneNumber);
        return Task.CompletedTask;
    }

    public Task SendVerificationCodeAsync(string toPhoneNumber, string code)
    {
        _logger.LogInformation("[SMS] Verification code sent to {To}", toPhoneNumber);
        return Task.CompletedTask;
    }
}
