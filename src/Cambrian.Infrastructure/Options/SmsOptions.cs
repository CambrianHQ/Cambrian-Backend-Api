namespace Cambrian.Infrastructure.Options;

public sealed class SmsOptions
{
    /// <summary>Provider: "console" (dev) or "twilio" (production).</summary>
    public string Provider { get; init; } = "console";

    /// <summary>Twilio Account SID (env var only — never hardcode).</summary>
    public string TwilioAccountSid { get; init; } = string.Empty;

    /// <summary>Twilio Auth Token (env var only — never hardcode).</summary>
    public string TwilioAuthToken { get; init; } = string.Empty;

    /// <summary>Twilio sender phone number in E.164 format (env var only).</summary>
    public string TwilioFromNumber { get; init; } = string.Empty;
}
