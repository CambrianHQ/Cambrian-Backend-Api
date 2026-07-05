using Cambrian.Application.DTOs.CreatorProfile;

namespace Cambrian.Application.Validation;

/// <summary>
/// Normalization + validation for the optional creator profile sections
/// ("What's in my studio" and "Artist Journey"). Lives in the Application
/// layer so controllers stay HTTP-only (architecture policy).
/// All fields are free text / free-text tags by design — niche gear must
/// never be blocked by a dropdown taxonomy.
/// </summary>
public static class CreatorProfileSectionValidator
{
    public const int MaxStudioTagsPerList = 30;
    public const int MaxStudioTagLength = 100;
    public const int MaxJourneyEntries = 20;

    private static readonly HashSet<string> JourneyEntryTypes = new(StringComparer.OrdinalIgnoreCase)
        { "update", "milestone", "photo", "event" };

    /// <summary>Trims/de-dupes free-text tag lists in place. Returns an error message or null.</summary>
    public static string? NormalizeStudioSetup(StudioSetupDto setup)
    {
        setup.Daw = NullIfEmpty(setup.Daw);
        setup.WorkflowNotes = NullIfEmpty(setup.WorkflowNotes);

        foreach (var (label, get, set) in new (string, Func<List<string>?>, Action<List<string>?>)[]
        {
            ("AI tools", () => setup.AiTools, v => setup.AiTools = v),
            ("Instruments", () => setup.Instruments, v => setup.Instruments = v),
            ("Hardware", () => setup.Hardware, v => setup.Hardware = v),
            ("Plugins", () => setup.Plugins, v => setup.Plugins = v),
            ("Gear", () => setup.Gear, v => setup.Gear = v),
        })
        {
            var list = get();
            if (list is null) continue;
            var cleaned = list
                .Select(t => t?.Trim() ?? "")
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (cleaned.Count > MaxStudioTagsPerList)
                return $"{label}: at most {MaxStudioTagsPerList} entries.";
            if (cleaned.Any(t => t.Length > MaxStudioTagLength))
                return $"{label}: each entry must be {MaxStudioTagLength} characters or less.";
            set(cleaned.Count > 0 ? cleaned : null);
        }
        return null;
    }

    /// <summary>Validates journey entries in place. Returns an error message or null.</summary>
    public static string? NormalizeJourneyEntries(List<JourneyEntryDto> entries)
    {
        if (entries.Count > MaxJourneyEntries)
            return $"Journey timeline: at most {MaxJourneyEntries} entries. Remove older ones.";

        foreach (var entry in entries)
        {
            entry.Type = entry.Type?.Trim().ToLowerInvariant() ?? "update";
            if (!JourneyEntryTypes.Contains(entry.Type))
                return "Journey entry type must be one of: update, milestone, photo, event.";

            entry.Title = entry.Title?.Trim() ?? "";
            if (entry.Title.Length == 0)
                return "Each journey entry needs a title.";

            entry.Body = NullIfEmpty(entry.Body);
            entry.Venue = NullIfEmpty(entry.Venue);
            entry.Date = NullIfEmpty(entry.Date);
            if (entry.Date is not null && !DateTime.TryParse(entry.Date, out _))
                return $"Journey entry \"{entry.Title}\": date must be a valid ISO date.";

            // Links/images: http(s) only, no embedded credentials (same rule as social links).
            foreach (var url in new[] { entry.Link, entry.ImageUrl })
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != "https" && uri.Scheme != "http")
                    || !string.IsNullOrEmpty(uri.UserInfo))
                    return $"Journey entry \"{entry.Title}\": links must be valid http(s) URLs.";
            }
            entry.Link = NullIfEmpty(entry.Link);
            entry.ImageUrl = NullIfEmpty(entry.ImageUrl);
        }
        return null;
    }

    private static string? NullIfEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
