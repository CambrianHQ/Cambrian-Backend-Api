using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Controllers;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Regressions for the creator report of 2026-07-14: mastered-asset downloads
/// errored because the browser navigated to the 302 download route with no
/// credentials — production's auth cookie was set SameSite=Lax from a cross-site
/// login response, which browsers refuse to store.
/// </summary>
public sealed class AuthCookiePolicyTests
{
    [Fact]
    public void ProductionAuthCookieIsCrossSiteStorable()
    {
        var options = AuthController.BuildAuthCookieOptions(isProduction: true);

        Assert.Equal(SameSiteMode.None, options.SameSite);
        Assert.True(options.Secure);
        Assert.True(options.HttpOnly);
        Assert.Equal("/", options.Path);
    }

    [Fact]
    public void DevelopmentAuthCookieStaysLaxForHttpLocalhost()
    {
        var options = AuthController.BuildAuthCookieOptions(isProduction: false);

        Assert.Equal(SameSiteMode.Lax, options.SameSite);
        Assert.False(options.Secure);
    }
}

public sealed class MasteredDownloadUrlTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public MasteredDownloadUrlTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task OwnerGetsBearerFriendlyDownloadUrlJson()
    {
        var email = $"master-dl-{Guid.NewGuid():N}@cambrian.test";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var jobId = await SeedCompletedJobAsync(userId, withWav: true);

        using var response = await client.GetAsync($"/release-ready/jobs/{jobId:D}/download-url?format=wav");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("url").GetString()));
        Assert.Equal($"master-{jobId}.wav", data.GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task AnonymousDownloadUrlIsUnauthorized()
    {
        using var response = await _fixture.CreateClient()
            .GetAsync($"/release-ready/jobs/{Guid.NewGuid():D}/download-url");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StrangerCannotResolveAnotherCreatorsMaster()
    {
        var ownerEmail = $"master-owner-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.CreateAuthenticatedClientAsync(ownerEmail);
        var ownerId = await _fixture.GetUserIdAsync(ownerEmail);
        var jobId = await SeedCompletedJobAsync(ownerId, withWav: true);

        var stranger = await _fixture.CreateAuthenticatedClientAsync(
            $"master-stranger-{Guid.NewGuid():N}@cambrian.test");
        using var response = await stranger.GetAsync($"/release-ready/jobs/{jobId:D}/download-url?format=wav");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MissingFormatArtifactReturnsNotFound()
    {
        var email = $"master-nomp3-{Guid.NewGuid():N}@cambrian.test";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var jobId = await SeedCompletedJobAsync(userId, withWav: true); // no MP3 key

        using var response = await client.GetAsync($"/release-ready/jobs/{jobId:D}/download-url?format=mp3");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<Guid> SeedCompletedJobAsync(string creatorId, bool withWav)
    {
        var jobId = Guid.NewGuid();
        var wavKey = $"masters/{jobId:D}.wav";

        using var scope = _fixture.Services.CreateScope();
        if (withWav)
        {
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
            using var content = new MemoryStream(Encoding.ASCII.GetBytes("RIFF-fake-wav-bytes"));
            await storage.UploadAsync(content, wavKey, "audio/wav");
        }

        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.MasteringJobs.Add(new MasteringJob
        {
            Id = jobId,
            CreatorId = creatorId,
            Status = "done",
            SourceKey = $"uploads/{jobId:D}.wav",
            MasteredWavKey = withWav ? wavKey : null,
        });
        await db.SaveChangesAsync();
        return jobId;
    }
}
