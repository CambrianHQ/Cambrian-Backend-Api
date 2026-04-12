using Cambrian.Api;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Email;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cambrian.Api.Tests;

public sealed class EmailProviderStartupTests
{
    [Fact]
    public void AddEmailProvider_WithResendProviderAndMissingApiKey_Throws()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "resend",
            ["Email:FromAddress"] = "noreply@cambrianmusic.com",
            ["Email:FromName"] = "Cambrian Music"
        });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddEmailProvider());

        Assert.Contains("Email:ResendApiKey", ex.Message);
    }

    [Fact]
    public void AddEmailProvider_WithSmtpProviderAndMissingHost_Throws()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "smtp",
            ["Email:FromAddress"] = "noreply@cambrianmusic.com",
            ["Email:FromName"] = "Cambrian Music",
            ["Email:SmtpPort"] = "587"
        });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddEmailProvider());

        Assert.Contains("Email:SmtpHost", ex.Message);
    }

    [Fact]
    public void AddEmailProvider_WithResendProviderAndRequiredSettings_RegistersResendEmailService()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "resend",
            ["Email:FromAddress"] = "noreply@cambrianmusic.com",
            ["Email:FromName"] = "Cambrian Music",
            ["Email:ResendApiKey"] = "re_test_123"
        });

        builder.AddEmailProvider();

        using var provider = builder.Services.BuildServiceProvider();
        var emailService = provider.GetRequiredService<IEmailService>();

        Assert.IsType<ResendEmailService>(emailService);
    }

    private static WebApplicationBuilder CreateBuilder(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Staging
        });

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);

        return builder;
    }
}
