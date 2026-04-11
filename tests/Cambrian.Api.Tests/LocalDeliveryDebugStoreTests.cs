using Cambrian.Infrastructure.Diagnostics;
using Cambrian.Infrastructure.Email;
using Cambrian.Infrastructure.Sms;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class LocalDeliveryDebugStoreTests
{
    [Fact]
    public async Task ConsoleEmailService_CapturesPasswordResetCode_InDebugStore()
    {
        var store = new LocalDeliveryDebugStore();
        var service = new ConsoleEmailService(Substitute.For<ILogger<ConsoleEmailService>>(), store);

        await service.SendPasswordResetAsync("listener@test.com", "123456");

        var latest = store.GetLatestPasswordReset(email: "listener@test.com");
        Assert.NotNull(latest);
        Assert.Equal("email", latest!.Channel);
        Assert.Equal("123456", latest.Code);
    }

    [Fact]
    public async Task ConsoleSmsService_CapturesPasswordResetCode_InDebugStore()
    {
        var store = new LocalDeliveryDebugStore();
        var service = new ConsoleSmsService(Substitute.For<ILogger<ConsoleSmsService>>(), store);

        await service.SendPasswordResetAsync("+15555550123", "654321");

        var latest = store.GetLatestPasswordReset(phoneNumber: "+15555550123");
        Assert.NotNull(latest);
        Assert.Equal("sms", latest!.Channel);
        Assert.Equal("654321", latest.Code);
    }
}
