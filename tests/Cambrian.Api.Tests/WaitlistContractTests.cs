using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// End-to-end contract tests for POST /waitlist (issue #72).
///
/// Verifies through the real HTTP pipeline (CambrianApiFixture +
/// in-memory SQLite) that:
///  - A new email is persisted and returns 201
///  - The same email re-submitted returns 200 with AlreadySignedUp = true
///    and the DB row count stays at 1
///  - Anonymous requests are accepted (no [Authorize] required)
///  - Invalid email returns 400
/// </summary>
public sealed class WaitlistContractTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public WaitlistContractTests(CambrianApiFixture factory) => _factory = factory;

    [Fact]
    public async Task Signup_NewEmail_Returns201_AndPersistsRow()
    {
        var client = _factory.CreateClient();
        var email = $"waitlist-new-{Guid.NewGuid():N}@cambrian.test";

        var res = await client.PostAsJsonAsync("/waitlist", new { email, source = "homepage" });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.False(json.GetProperty("data").GetProperty("alreadySignedUp").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var row = await db.WaitlistSignups.FirstOrDefaultAsync(s => s.Email == email);
        Assert.NotNull(row);
        Assert.Equal("homepage", row!.Source);
    }

    [Fact]
    public async Task Signup_DuplicateEmail_Returns200_WithAlreadyFlag_AndKeepsSingleRow()
    {
        var client = _factory.CreateClient();
        var email = $"waitlist-dup-{Guid.NewGuid():N}@cambrian.test";

        var first = await client.PostAsJsonAsync("/waitlist", new { email });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/waitlist", new { email = email.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var json = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("data").GetProperty("alreadySignedUp").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var rows = await db.WaitlistSignups.Where(s => s.Email == email.ToLowerInvariant()).CountAsync();
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Signup_InvalidEmail_Returns400()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/waitlist", new { email = "not-an-email" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Signup_AcceptsAnonymous_NoAuthHeaderRequired()
    {
        // _factory.CreateClient() returns a client with no Authorization header.
        var client = _factory.CreateClient();
        var email = $"waitlist-anon-{Guid.NewGuid():N}@cambrian.test";

        var res = await client.PostAsJsonAsync("/waitlist", new { email });

        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }
}
