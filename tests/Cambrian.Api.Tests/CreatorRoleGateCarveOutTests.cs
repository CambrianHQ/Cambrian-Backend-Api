using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests;

/// <summary>
/// Issue #73 carve-out: a `Role = "User"` account (i.e. a signed-in user that
/// has NOT been promoted to Creator) must be able to read `/payouts/earnings`
/// without hitting the `[RequireCreatorTier]` 403. The frontend's "all users
/// are creators" model relies on this.
///
/// Money-moving payout actions (request, connect, etc.) are still gated and
/// MUST still 403 for a User-role account — that's the regression guardrail.
/// </summary>
public sealed class CreatorRoleGateCarveOutTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public CreatorRoleGateCarveOutTests(CambrianApiFixture factory) => _factory = factory;

    [Fact]
    public async Task UserRoleAccount_CanReadPayoutsEarnings()
    {
        // Register a fresh account, demote it from Creator → User, re-login so
        // the JWT carries the new role claim.
        var email = $"user-role-earnings-{Guid.NewGuid():N}@cambrian.test";
        const string password = "Test1234!@";
        await _factory.RegisterUserAsync(email, password);
        await _factory.SetUserRoleAsync(email, "User");

        var token = await _factory.LoginUserAsync(email, password);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.GetAsync("/payouts/earnings");

        // Must NOT be 403. Earnings can return 200 with empty/default values for
        // an account with no purchases — anything other than 403 proves the gate
        // was lifted. (We accept 200 specifically as the success case.)
        Assert.NotEqual(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task UserRoleAccount_CannotRequestPayout_StillForbidden()
    {
        // Regression guardrail: the carve-out must NOT lift the gate from
        // money-moving actions. /payouts/request still requires Creator role.
        var email = $"user-role-request-{Guid.NewGuid():N}@cambrian.test";
        const string password = "Test1234!@";
        await _factory.RegisterUserAsync(email, password);
        await _factory.SetUserRoleAsync(email, "User");

        var token = await _factory.LoginUserAsync(email, password);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.PostAsJsonAsync("/payouts/request", new { amount = 10m });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
