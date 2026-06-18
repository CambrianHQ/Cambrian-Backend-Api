using System.Linq;
using Cambrian.Api.E2e;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Cambrian.Api.Tests.Unit.Application;

/// <summary>
/// Locks in the two safety-critical properties of the E2E gate: it is unreachable outside
/// Testing/explicitly-enabled-Development (NEVER Production/Staging), and its secret check is a
/// constant-time, fail-closed comparison.
/// </summary>
public sealed class E2eSupportTests
{
    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static IConfiguration Config(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    [Fact]
    public void IsEnabled_Testing_IsAlwaysTrue()
        => Assert.True(E2eSupport.IsEnabled(Env("Testing"), Config()));

    [Fact]
    public void IsEnabled_Development_RequiresExplicitFlag()
    {
        Assert.False(E2eSupport.IsEnabled(Env("Development"), Config()));
        Assert.True(E2eSupport.IsEnabled(Env("Development"), Config(("Cambrian:E2E:Enabled", "true"))));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void IsEnabled_DeployedEnvironments_AreFalseEvenWithFlagSet(string envName)
        => Assert.False(E2eSupport.IsEnabled(Env(envName), Config(("Cambrian:E2E:Enabled", "true"))));

    [Theory]
    [InlineData("QA")]
    [InlineData("Local")]
    [InlineData("")]
    public void IsEnabled_UnknownEnvironments_AreFalse(string envName)
        => Assert.False(E2eSupport.IsEnabled(Env(envName), Config(("Cambrian:E2E:Enabled", "true"))));

    [Fact]
    public void SecretMatches_ExactMatch_IsTrue()
        => Assert.True(E2eSupport.SecretMatches("hunter2-e2e-secret", "hunter2-e2e-secret"));

    [Theory]
    [InlineData("wrong", "right")]
    [InlineData("right", "righ")]   // differing length
    [InlineData("Right", "right")]  // case sensitive
    public void SecretMatches_Mismatch_IsFalse(string provided, string expected)
        => Assert.False(E2eSupport.SecretMatches(provided, expected));

    [Theory]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    [InlineData("", "x")]
    [InlineData("x", "")]
    [InlineData(null, null)]
    public void SecretMatches_NullOrEmpty_IsFalse(string? provided, string? expected)
        => Assert.False(E2eSupport.SecretMatches(provided, expected));

    [Fact]
    public void ResolveSecret_PrefersConfigKey()
        => Assert.Equal("from-config", E2eSupport.ResolveSecret(Config(("Cambrian:E2E:Secret", "from-config"))));
}
