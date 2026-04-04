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

    /// <summary>
    /// Send a purchase confirmation email after a track is bought.
    /// </summary>
    Task SendPurchaseConfirmationAsync(string to, string trackTitle, string licenseType, decimal pricePaid, string licenseUrl);

    /// <summary>
    /// Send a verification link to the new email address when a user requests an email change.
    /// </summary>
    Task SendEmailChangeVerificationAsync(string newEmail, string verificationLink);

    /// <summary>
    /// Notify the old email address that an email change has been requested.
    /// </summary>
    Task SendEmailChangeNotificationAsync(string oldEmail, string newEmail);
}
