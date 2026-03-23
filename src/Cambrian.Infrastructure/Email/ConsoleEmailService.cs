using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cambrian.Infrastructure.Email;

/// <summary>
/// Development email service that logs emails to the console instead of sending them.
/// </summary>
public sealed class ConsoleEmailService : IEmailService
{
    private readonly ILogger<ConsoleEmailService> _logger;

    public ConsoleEmailService(ILogger<ConsoleEmailService> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string to, string subject, string htmlBody)
    {
        _logger.LogInformation(
            "[Email] To: {To} | Subject: {Subject}\n{Body}",
            to, subject, htmlBody);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(string to, string code)
    {
        // SECURITY: Only log that a code was sent, never the code itself
        _logger.LogInformation("[Email] Password reset code sent to {To}", to);
        return Task.CompletedTask;
    }

    public Task SendVerificationCodeAsync(string to, string code)
    {
        _logger.LogInformation("[Email] Verification code sent to {To}", to);
        return Task.CompletedTask;
    }

    public Task SendWelcomeAsync(string to, string displayName)
    {
        _logger.LogInformation("[Email] Welcome email sent to {To} ({Name})", to, displayName);
        return Task.CompletedTask;
    }
}
