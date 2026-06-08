using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests.Security;

/// <summary>
/// Regression coverage for the unintended listener→creator promotion bug.
/// POST /auth/set-username used to silently set Role = "Creator" for any "User" and
/// provision a public Creators row + storefront. Choosing a username is generic onboarding
/// available to every account, so it must NOT change the role or create creator artifacts.
/// An account becomes a creator only through an explicit path (registration with
/// role=creator, admin promotion, or admin/billing tier upgrade).
/// </summary>
public sealed class SetUsernameRolePromotionTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public SetUsernameRolePromotionTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SetUsername_KeepsListenerAsListener_AndProvisionsNoCreatorArtifacts()
    {
        var email = $"listener-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email); // default role => "User" (listener)
        using var client = await AuthedClientAsync(email);

        var username = $"listen{Guid.NewGuid():N}"[..14];
        var res = await client.PostAsJsonAsync("/auth/set-username", new { username });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // The endpoint reports the unchanged role...
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("User", json.GetProperty("data").GetProperty("role").GetString());

        // ...and the database agrees: a listener stays a listener.
        Assert.Equal("User", await _fixture.GetUserRoleAsync(email));

        // No Creators row was provisioned, so the listener does not surface in the public
        // creator directory (which applies no role filter of its own).
        var userId = await _fixture.GetUserIdAsync(email);
        Assert.False(await _fixture.CreatorRowExistsAsync(userId));

        using var anon = _fixture.CreateClient();
        var lookup = await anon.GetAsync($"/creator/username/{username}");
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Fact]
    public async Task SetUsername_Listener_StillCannotUpload()
    {
        var email = $"listener-noupload-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email);
        using var client = await AuthedClientAsync(email);

        var username = $"noup{Guid.NewGuid():N}"[..12];
        var set = await client.PostAsJsonAsync("/auth/set-username", new { username });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        // Re-login so the upload attempt carries the freshest role claim.
        using var fresh = await AuthedClientAsync(email);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Should Not Upload"), "Title");
        var audio = new ByteArrayContent(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(audio, "Audio", "nope.mp3");

        var res = await fresh.PostAsync("/upload", form);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task SetUsername_Creator_StaysCreator_AndIsPubliclyResolvable()
    {
        var email = $"creator-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email);
        await _fixture.SetUserRoleAsync(email, "Creator"); // explicit creator account
        using var client = await AuthedClientAsync(email);

        var username = $"creator{Guid.NewGuid():N}"[..14];
        var res = await client.PostAsJsonAsync("/auth/set-username", new { username });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Creator", await _fixture.GetUserRoleAsync(email));

        // A real creator DOES get the Creators row + public storefront provisioned.
        var userId = await _fixture.GetUserIdAsync(email);
        Assert.True(await _fixture.CreatorRowExistsAsync(userId));

        using var anon = _fixture.CreateClient();
        var lookup = await anon.GetAsync($"/creator/username/{username}");
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
    }

    private async Task<HttpClient> AuthedClientAsync(string email)
    {
        var token = await _fixture.LoginUserAsync(email);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
