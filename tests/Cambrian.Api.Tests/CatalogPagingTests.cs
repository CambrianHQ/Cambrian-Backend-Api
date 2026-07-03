using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// F3: /catalog supports real offset/limit paging with a `total` in the envelope, and
/// stable tie-break ordering so pages don't overlap or shuffle. Verifies the full
/// catalogue is reachable (the bug was a hard cap at 60 of 134 tracks).
/// </summary>
public sealed class CatalogPagingTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CatalogPagingTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Catalog_OffsetLimit_PagesEntireCatalogue_NoOverlap_WithTotal()
    {
        // Seed a creator with N public tracks under a unique genre so `total` is exact.
        var email = $"paging-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email);
        var creatorId = await _fixture.GetUserIdAsync(email);
        await _fixture.SetUserRoleAsync(email, "Creator");
        await _fixture.SetUsernameAsync(email, $"pg{Guid.NewGuid():N}"[..12]);

        var genre = $"pgtest{Guid.NewGuid():N}"[..16];
        const int total = 7;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            for (var i = 0; i < total; i++)
            {
                var id = Guid.NewGuid();
                db.Tracks.Add(new Track
                {
                    Id = id,
                    CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
                    Title = $"Page Beat {i}",
                    Price = 9.99m,
                    LicenseType = "standard",
                    AudioUrl = "tracks/pg.mp3",
                    CreatorId = creatorId,
                    Genre = genre,
                    Visibility = "public",
                    Status = "public",
                });
            }
            await db.SaveChangesAsync();
        }

        var client = _fixture.CreateClient();
        var seen = new HashSet<string>();
        const int limit = 3;

        for (var offset = 0; offset < total; offset += limit)
        {
            var res = await client.GetAsync($"/catalog?genre={genre}&offset={offset}&limit={limit}");
            res.EnsureSuccessStatusCode();
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(total, root.GetProperty("total").GetInt32());
            Assert.Equal(offset, root.GetProperty("offset").GetInt32());
            Assert.Equal(limit, root.GetProperty("limit").GetInt32());

            foreach (var item in root.GetProperty("data").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                Assert.True(seen.Add(id), $"Track {id} appeared on more than one page (unstable ordering).");
            }
        }

        // Every seeded track was reachable across pages — nothing hard-capped away.
        Assert.Equal(total, seen.Count);
    }

    [Fact]
    public async Task Catalog_LimitIsCappedAtSixty()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/catalog?limit=500");
        res.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(60, root.GetProperty("limit").GetInt32());
    }
}
