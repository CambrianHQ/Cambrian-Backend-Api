using System.Text.Json;
using System.Text.RegularExpressions;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Regression;

/// <summary>
/// F18 guard: no anonymous/public route may serialize a financial field — platform
/// fee, creator earnings, revenue, payouts, or Stripe identifiers. The public catalog
/// previously exposed per-track creator earnings and the platform fee, letting any
/// visitor scrape exactly what every creator makes.
///
/// This walks the full JSON tree of each public response and fails if any property
/// name matches <c>(earning|fee|revenue|payout|stripe)</c> with a DATA value (number
/// or string). Boolean display toggles such as <c>showEarnings</c> are permitted —
/// they are UI preferences, not financial data.
///
/// Scope: the entity-derived discovery surface (catalog, track detail, trending,
/// public v1 search, activity), where accidental leaks creep in. Curated pricing
/// surfaces (/tiers/config, /api/public/pricing) intentionally publish the plan
/// take-rate as pricing transparency and are covered by their own contract tests.
/// </summary>
[Trait("Category", "Critical")]
public sealed class PublicRouteFinancialLeakTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public PublicRouteFinancialLeakTests(CambrianApiFixture fixture) => _fixture = fixture;

    private static readonly Regex FinancialKey =
        new("(earning|fee|revenue|payout|stripe)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task<Guid> SeedPublicTrackAsync()
    {
        var email = $"leakfin-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var creatorId = await _fixture.GetUserIdAsync(email);
        await _fixture.SetUserRoleAsync(email, "Creator");
        await _fixture.SetUsernameAsync(email, $"lf{Guid.NewGuid():N}"[..12]);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
            Title = "Financial Leak Probe",
            Price = 19.99m,
            NonExclusivePriceCents = 1999,
            // Real, non-zero legacy licensing amounts on the entity — the test proves
            // NONE of the derived fee/earnings values reach an anonymous response.
            ExclusivePriceCents = 49900,
            CopyrightBuyoutPriceCents = 249900,
            LicenseType = "standard",
            AudioUrl = "tracks/leak-fin.mp3",
            CreatorId = creatorId,
            Genre = "Electronic",
            Visibility = "public",
            Status = "public",
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task PublicRoutes_DoNotLeakFinancialFields()
    {
        var trackId = await SeedPublicTrackAsync();
        using var client = _fixture.CreateClient();

        string[] routes =
        {
            "/catalog?pageSize=50",
            "/discover",
            "/tracks",
            $"/tracks/{trackId}",
            $"/catalog/{trackId}",
            "/trending",
            "/api/v1/tracks/search",
            "/activity/sales",
        };

        foreach (var route in routes)
        {
            var res = await client.GetAsync(route);
            var body = await res.Content.ReadAsStringAsync();
            Assert.True(res.IsSuccessStatusCode, $"{route} → {(int)res.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            AssertNoFinancialData(doc.RootElement, route, "$");
        }
    }

    private static void AssertNoFinancialData(JsonElement el, string route, string path)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (FinancialKey.IsMatch(prop.Name))
                    {
                        // A matching key is only acceptable as a boolean UI toggle
                        // (e.g. showEarnings). Any number/string value is a leak.
                        Assert.True(
                            prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                            $"Public route {route} leaked financial field '{path}.{prop.Name}' = {prop.Value.GetRawText()}");
                    }

                    AssertNoFinancialData(prop.Value, route, $"{path}.{prop.Name}");
                }
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    AssertNoFinancialData(item, route, $"{path}[{i++}]");
                break;
        }
    }
}
