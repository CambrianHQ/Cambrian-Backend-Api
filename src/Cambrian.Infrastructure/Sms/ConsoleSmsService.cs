using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cambrian.Infrastructure.Sms;

/// <summary>
/// Development SMS service that logs messages to the console instead of sending them.
/// </summary>
public sealed class ConsoleSmsService : ISmsService
{
    private readonly ILogger<ConsoleSmsService> _logger;
    private readonly ILocalDeliveryDebugStore _debugStore;

    public ConsoleSmsService(ILogger<ConsoleSmsService> logger, ILocalDeliveryDebugStore debugStore)
    {
        _logger = logger;
        _debugStore = debugStore;
    }

    public Task SendAsync(string toPhoneNumber, string message)
    {
        _debugStore.CaptureSms(toPhoneNumber, message);
        _logger.LogInformation("[SMS] To: {To} | Message: {Message}", toPhoneNumber, message);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(string toPhoneNumber, string code)
    {
        _debugStore.CaptureSms(toPhoneNumber, "Password reset code sent.", code, "password_reset");
        // SECURITY: Only log that a code was sent, never the code itself
        _logger.LogInformation("[SMS] Password reset code sent to {To}", toPhoneNumber);
        return Task.CompletedTask;
    }

    public Task SendVerificationCodeAsync(string toPhoneNumber, string code)
    {
        _debugStore.CaptureSms(toPhoneNumber, "Verification code sent.", code, "verification_code");
        _logger.LogInformation("[SMS] Verification code sent to {To}", toPhoneNumber);
        return Task.CompletedTask;
    }
}
