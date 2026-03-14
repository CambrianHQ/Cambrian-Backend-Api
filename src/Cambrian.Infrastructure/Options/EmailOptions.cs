namespace Cambrian.Infrastructure.Options;

public sealed class EmailOptions
{
    /// <summary>Email provider: "console" (dev), "smtp", "resend", or "sendgrid".</summary>
    public string Provider { get; set; } = "console";

    public string FromAddress { get; set; } = "noreply@cambrianmusic.com";
    public string FromName { get; set; } = "Cambrian Music";

    // SMTP settings
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = string.Empty;
    public string SmtpPass { get; set; } = string.Empty;

    // Resend settings
    public string ResendApiKey { get; set; } = string.Empty;

    // SendGrid settings
    public string SendGridApiKey { get; set; } = string.Empty;
}
