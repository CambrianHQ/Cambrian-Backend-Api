using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Cambrian.Infrastructure.Email;

/// <summary>
/// SMTP-based email service for production use (MailKit).
/// Supports port 587 (STARTTLS) and port 465 (implicit SSL).
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
        _logger.LogDebug("[Email] Sending via {Host}:{Port}",
            _options.SmtpHost, _options.SmtpPort);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            using var client = new MailKit.Net.Smtp.SmtpClient();

            // Port 465 = implicit SSL, port 587 = STARTTLS, other = auto
            var secureSocket = _options.SmtpPort == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, secureSocket, cts.Token);
            await client.AuthenticateAsync(_options.SmtpUser, _options.SmtpPass, cts.Token);
            await client.SendAsync(message, cts.Token);
            await client.DisconnectAsync(true, cts.Token);

            _logger.LogInformation("[Email] Sent successfully");
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
