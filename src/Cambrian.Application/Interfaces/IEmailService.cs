namespace Cambrian.Application.Interfaces;

/// <summary>
/// Abstraction for sending transactional emails (verification codes, password resets, etc.).
/// </summary>
public interface IEmailService
{
    Task SendAsync(string to, string subject, string htmlBody);

    Task SendPasswordResetAsync(string to, string code);

    Task SendVerificationCodeAsync(string to, string code);

    Task SendWelcomeAsync(string to, string displayName);
}
