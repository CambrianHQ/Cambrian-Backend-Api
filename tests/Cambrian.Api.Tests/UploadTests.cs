using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests;

/// <summary>
/// Integration tests for the Upload flow:
///   POST /upload → multipart form upload (Creator role required)
/// </summary>
public sealed class UploadTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public UploadTests(CambrianApiFixture factory) => _factory = factory;

    [Fact]
    public async Task Upload_AsCreator_ReturnsCreated()
    {
        // Register + promote to Creator
        var email = "upload-creator@cambrian.com";
        var client = await _factory.CreateAuthenticatedClientAsync(email, "Test1234!@");
        await _factory.PromoteToCreatorAsync(email);

        // Re-login to get a fresh token with the Creator role
        var loginRes = await _factory.CreateClient().PostAsJsonAsync("/auth/login", new
        {
            email,
            password = "Test1234!@"
        });
        var loginJson = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        var token = loginJson.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Build multipart form
        var content = new MultipartFormDataContent();
        var fakeAudio = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-audio-data"));
        fakeAudio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        content.Add(fakeAudio, "audio", "test-beat.mp3");
        content.Add(new StringContent("Upload Integration Beat"), "title");
        content.Add(new StringContent("Hip-Hop"), "genre");
        content.Add(new StringContent("29.99"), "price");

        var res = await client.PostAsync("/upload", content);

        Assert.True(
            res.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Expected 201/200 but got {res.StatusCode}");
    }

    [Fact]
    public async Task Upload_AsRegularUser_Returns403()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(
            "upload-user@cambrian.com", "Test1234!@");

        var content = new MultipartFormDataContent();
        var fakeAudio = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-audio-data"));
        fakeAudio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        content.Add(fakeAudio, "audio", "test-beat.mp3");
        content.Add(new StringContent("Should Fail Beat"), "title");

        var res = await client.PostAsync("/upload", content);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Upload_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var content = new MultipartFormDataContent();
        var fakeAudio = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-audio-data"));
        fakeAudio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        content.Add(fakeAudio, "audio", "test-beat.mp3");
        content.Add(new StringContent("No Auth Beat"), "title");

        var res = await client.PostAsync("/upload", content);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Upload_MissingTitle_Returns400()
    {
        var email = "upload-notitle@cambrian.com";
        var client = await _factory.CreateAuthenticatedClientAsync(email, "Test1234!@");
        await _factory.PromoteToCreatorAsync(email);

        // Re-login for updated role claim
        var loginRes = await _factory.CreateClient().PostAsJsonAsync("/auth/login", new
        {
            email,
            password = "Test1234!@"
        });
        var loginJson = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        var token = loginJson.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var content = new MultipartFormDataContent();
        var fakeAudio = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-audio-data"));
        fakeAudio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        content.Add(fakeAudio, "audio", "test-beat.mp3");
        // Intentionally omit title

        var res = await client.PostAsync("/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
