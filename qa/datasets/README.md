# Synthetic Creator Datasets

This folder contains genre-oriented synthetic creator packs for staging, demos, and manual QA.

Primary file:

- `synthetic-creators.genre-pack.json`

Suggested uses:

- seed visually and musically distinct creator storefronts in staging
- populate sales, library, and catalog demos with believable metadata
- give QA fixed personas to reference during acceptance testing

Example C# seeder mapping:

```csharp
public static class GenreSeedData
{
    public static IEnumerable<(string Name, string Genre, string Bio)> Creators =>
        new[]
        {
            ("EchoPulse", "Ambient", "Atmospheric cinematic soundscapes."),
            ("NeonVortex", "EDM", "Festival-ready electronic anthems."),
            ("UrbanCipher", "Hip-Hop", "Modern AI-generated beats."),
            ("OrionScores", "Cinematic", "Epic orchestral compositions."),
            ("LoFiHarbor", "Lo-Fi", "Relaxing study and chill beats."),
        };
}
```

Implementation note:

The repo already performs substantial staging/demo seeding in `StartupExtensions`. If we decide to wire this JSON into application startup, the safest next step is to add a dedicated importer that runs only when an explicit demo-seed flag is enabled.
