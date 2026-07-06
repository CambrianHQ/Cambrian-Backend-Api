using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests.Security;

/// <summary>
/// Proves the "VerifiedEmail" authorization policy actually guards the high-stakes write
/// surfaces its definition claims it protects — Upload, Payouts, API-key creation,
/// and Wallet withdrawal. An authenticated-but-unverified account is blocked with the
/// structured <c>email_not_verified</c> body; a verified account clears the email gate.
/// Before this wiring the policy was defined but applied to zero endpoints, so unverified
/// registrations could transact.
/// </summary>
public sealed class VerifiedEmailPolicyTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public VerifiedEmailPolicyTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    // ───────────────────────── Unverified accounts are blocked ─────────────────────────

    [Fact]
    public async Task WalletWithdraw_Blocks_UnverifiedAccount()
    {
        using var client = await _fixture.CreateUnverifiedClientAsync(Email("wallet"));

        var res = await client.PostAsJsonAsync("/wallet/withdraw", new { amount = 100 });

        await AssertBlockedByEmailGate(res);
    }

    [Fact]
    public async Task ApiKeyCreation_Blocks_UnverifiedAccount()
    {
        using var client = await _fixture.CreateUnverifiedClientAsync(Email("apikey"));

        var res = await client.PostAsJsonAsync("/api/v1/keys", new { name = "ci-key" });

        await AssertBlockedByEmailGate(res);
    }

    [Fact]
    public async Task PayoutRequest_Blocks_UnverifiedAccount()
    {
        // VerifiedEmail is enforced in the authorization middleware, ahead of the
        // creator-tier / username / Stripe-connect action filters — so even a plain
        // unverified account is stopped by the email gate first.
        using var client = await _fixture.CreateUnverifiedClientAsync(Email("payout"));

        var res = await client.PostAsJsonAsync("/payouts/request", new { amountCents = 1000 });

        await AssertBlockedByEmailGate(res);
    }

    [Fact]
    public async Task Upload_Blocks_UnverifiedCreator()
    {
        // Use an unverified *creator* so the CanUploadTrack capability is satisfied and the
        // VerifiedEmail policy is the sole failing gate.
        var seed = Guid.NewGuid().ToString("N");
        using var client = await _fixture.CreateUnverifiedClientAsync(
            $"ve-upload-{seed}@cambrian.com", role: "Creator", username: $"veup{seed[..8]}");

        using var form = BuildUploadForm();
        var res = await client.PostAsync("/upload", form);

        await AssertBlockedByEmailGate(res);
    }

    // Regression: bulk album upload (draft) was rejected with 403 for every track on an
    // unverified account, because the VerifiedEmail gate applied unconditionally. Drafts
    // (SaveAsDraft=true) stay hidden until the creator publishes, so they must not require
    // a verified email — only actually going public should.
    [Fact]
    public async Task Upload_AllowsDraftUpload_ForUnverifiedCreator()
    {
        var seed = Guid.NewGuid().ToString("N");
        using var client = await _fixture.CreateUnverifiedClientAsync(
            $"ve-draft-{seed}@cambrian.com", role: "Creator", username: $"vedraft{seed[..8]}");

        using var form = BuildUploadForm(saveAsDraft: true);
        var res = await client.PostAsync("/upload", form);

        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.DoesNotContain("email_not_verified", body);
    }

    [Fact]
    public async Task CreateTrack_AllowsDraftUpload_ForUnverifiedCreator()
    {
        var seed = Guid.NewGuid().ToString("N");
        using var client = await _fixture.CreateUnverifiedClientAsync(
            $"ve-draft2-{seed}@cambrian.com", role: "Creator", username: $"vedraft2{seed[..8]}");

        using var form = BuildUploadForm(saveAsDraft: true);
        var res = await client.PostAsync("/api/tracks", form);

        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.DoesNotContain("email_not_verified", body);
    }

    // ──────────────── Verified accounts clear the email gate (controls) ────────────────

    [Fact]
    public async Task WalletWithdraw_AllowsVerifiedAccount_PastEmailGate()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(Email("ok-wallet"));

        var res = await client.PostAsJsonAsync("/wallet/withdraw", new { amount = 100 });

        await AssertNotBlockedByEmailGate(res);
    }

    [Fact]
    public async Task ApiKeyCreation_AllowsVerifiedAccount_PastEmailGate()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(Email("ok-apikey"));

        var res = await client.PostAsJsonAsync("/api/v1/keys", new { name = "ci-key" });

        await AssertNotBlockedByEmailGate(res);
    }

    [Fact]
    public async Task Upload_AllowsVerifiedCreator_PastEmailGate()
    {
        var seed = Guid.NewGuid().ToString("N");
        using var client = await _fixture.CreateRoleClientAsync(
            $"ve-ok-upload-{seed}@cambrian.com", "Test1234!@", "Creator", $"veok{seed[..8]}");

        using var form = BuildUploadForm();
        var res = await client.PostAsync("/upload", form);

        await AssertNotBlockedByEmailGate(res);
    }

    // ───────────────────────────────── helpers ─────────────────────────────────

    private static string Email(string suffix) => $"ve-{suffix}-{Guid.NewGuid():N}@cambrian.com";

    private static async Task AssertBlockedByEmailGate(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("email_not_verified", body);
    }

    private static async Task AssertNotBlockedByEmailGate(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("email_not_verified", body);
    }

    private static MultipartFormDataContent BuildUploadForm(bool saveAsDraft = false)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("Verified Email Upload"), "Title");
        if (saveAsDraft)
            content.Add(new StringContent("true"), "SaveAsDraft");

        var audio = new ByteArrayContent(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        content.Add(audio, "Audio", "ve-test.mp3");

        return content;
    }
}
