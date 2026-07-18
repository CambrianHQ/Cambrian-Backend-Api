namespace Cambrian.Application.Exceptions;

/// <summary>Client-safe signal that the verification provider did not accept delivery.</summary>
public sealed class VerificationEmailDeliveryException : Exception
{
    public VerificationEmailDeliveryException(Exception innerException)
        : base("Verification email could not be delivered. Please try again later.", innerException) { }
}
