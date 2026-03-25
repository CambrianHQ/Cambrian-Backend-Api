namespace Cambrian.Application.Interfaces;

/// <summary>
/// Abstraction for sending transactional SMS messages (verification codes, password resets, etc.).
/// </summary>
public interface ISmsService
{
    Task SendAsync(string toPhoneNumber, string message);

    Task SendPasswordResetAsync(string toPhoneNumber, string code);

    Task SendVerificationCodeAsync(string toPhoneNumber, string code);
}
