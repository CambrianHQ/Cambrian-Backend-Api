using System.Net;
using System.Net.Mail;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.Email;

/// <summary>
/// SMTP-based email service for production use.
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<EmailOptions> options, ILogger<SmtpEmailService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string htmlBody)
    {
        _logger.LogInformation("[Email] Sending to {To} via {Host}:{Port} from {From} (user={User})",
            to, _options.SmtpHost, _options.SmtpPort, _options.FromAddress, _options.SmtpUser);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            Credentials = new NetworkCredential(_options.SmtpUser, _options.SmtpPass),
            EnableSsl = true,
            Timeout = 15_000,
        };

        var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(to);

        try
        {
            await client.SendMailAsync(message, cts.Token);
            _logger.LogInformation("[Email] Sent successfully to {To}", to);
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("[Email] Timed out sending to {To} after 15s", to);
            throw new InvalidOperationException($"SMTP timeout sending to {to}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Email] Failed to send to {To}: {Error}", to, ex.Message);
            throw;
        }
    }

    public Task SendPasswordResetAsync(string to, string code)
    {
        var html = $"""
            <h2>Password Reset</h2>
            <p>Your password reset code is: <strong>{code}</strong></p>
            <p>This code expires in 15 minutes.</p>
            <p>If you didn't request this, you can safely ignore this email.</p>
            """;
        return SendAsync(to, "Cambrian — Password Reset Code", html);
    }

    public Task SendVerificationCodeAsync(string to, string code)
    {
        var html = $"""
            <h2>Verification Code</h2>
            <p>Your verification code is: <strong>{code}</strong></p>
            <p>This code expires in 15 minutes.</p>
            """;
        return SendAsync(to, "Cambrian — Verification Code", html);
    }

    public Task SendWelcomeAsync(string to, string displayName)
    {
        var html = $"""
            <h2>Welcome to Cambrian, {displayName}!</h2>
            <p>Your account has been created successfully.</p>
            <p>Start exploring AI-generated music on the marketplace.</p>
            """;
        return SendAsync(to, "Welcome to Cambrian Music", html);
    }
}
