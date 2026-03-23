using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.Email;

/// <summary>
/// Resend (resend.com) HTTP API email service.
/// Uses HTTPS (port 443) — works on all cloud hosts including Render.
/// </summary>
public sealed class ResendEmailService : IEmailService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly EmailOptions _options;
    private readonly ILogger<ResendEmailService> _logger;
    private readonly HttpClient _http;

    public ResendEmailService(IOptions<EmailOptions> options, ILogger<ResendEmailService> logger, IHttpClientFactory httpFactory)
    {
        _options = options.Value;
        _logger = logger;
        _http = httpFactory.CreateClient("Resend");
        _http.BaseAddress = new Uri("https://api.resend.com/");
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.ResendApiKey);
    }

    public async Task SendAsync(string to, string subject, string htmlBody)
    {
        _logger.LogDebug("[Email:Resend] Sending email subject=\"{Subject}\"", subject);

        var payload = new
        {
            from = $"{_options.FromName} <{_options.FromAddress}>",
            to = new[] { to },
            subject,
            html = htmlBody
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var response = await _http.PostAsync("emails", content, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[Email:Resend] API error {Status}: {Body}", (int)response.StatusCode, body);
                throw new InvalidOperationException($"Resend API error {(int)response.StatusCode}: {body}");
            }

            _logger.LogInformation("[Email:Resend] Sent successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("[Email:Resend] Timed out sending to {To} after 15s", to);
            throw new InvalidOperationException($"Resend API timeout sending to {to}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[Email:Resend] HTTP error sending to {To}: {Error}", to, ex.Message);
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
