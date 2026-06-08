using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// B11 regression (integration): GET /catalog must honor the sort parameter end-to-end. Previously
/// the response order was identical regardless of sort. This proves the controller→service→repo
/// pipeline applies the requested ordering and that two sorts yield different orderings.
///
/// Uses non-decimal sorts (newest by date, title by string) because the unit-test database is
/// SQLite, which cannot ORDER BY decimal. The exact price/trending mappings are covered by
/// <see cref="TrackSortingTests"/>; production runs on PostgreSQL where decimal ordering works.
/// </summary>
public sealed class CatalogSortTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CatalogSortTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Catalog_Honors_Sort_Parameter()
    {
        // Tracks must reference a real ApplicationUser: the catalog query inner-joins Track.Creator.
        var email = $"catsort-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var creatorId = await _fixture.GetUserIdAsync(email);

        // Unique genre isolates these three tracks from anything else in the shared catalog.
        var genre = $"sorttest-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;

        // Title order and recency order are deliberately different so the two sorts can't coincide.
        await SeedCatalogTrackAsync(creatorId, genre, "Zulu", createdAt: now.AddMinutes(-10));  // newest
        await SeedCatalogTrackAsync(creatorId, genre, "Alpha", createdAt: now.AddMinutes(-20));
        await SeedCatalogTrackAsync(creatorId, genre, "Mike", createdAt: now.AddMinutes(-30));   // oldest

        var client = _fixture.CreateClient();

        var newest = await TitlesAsync(client, genre, "newest");
        var byTitle = await TitlesAsync(client, genre, "title");

        Assert.Equal(new[] { "Zulu", "Alpha", "Mike" }, newest);
        Assert.Equal(new[] { "Alpha", "Mike", "Zulu" }, byTitle);
        Assert.NotEqual(newest, byTitle); // the bug was identical ordering regardless of sort
    }

    private async Task<string[]> TitlesAsync(HttpClient client, string genre, string sort)
    {
        var res = await client.GetAsync($"/catalog?genre={genre}&sort={sort}&pageSize=100");
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        return data.EnumerateArray().Select(t => t.GetProperty("title").GetString()!).ToArray();
    }

    private async Task SeedCatalogTrackAsync(string creatorId, string genre, string title, DateTime createdAt)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
            Title = title,
            Price = 9.99m,
            LicenseType = "standard",
            AudioUrl = "tracks/test.mp3",
            CreatorId = creatorId,
            Genre = genre,
            CreatedAt = createdAt,
            // Visibility="public" / Status="available" are entity defaults → included in catalog.
        });
        await db.SaveChangesAsync();
    }
}
