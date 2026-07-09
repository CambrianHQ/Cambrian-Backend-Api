using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.CreatorProfile;

/// <summary>
/// "What's in my studio" — optional, creator-authored gear/workflow section.
/// Every field is free text or free-text tags by design: niche DAWs, plugins,
/// and hardware must never be blocked by a dropdown taxonomy. Stored on
/// CreatorProfile as a JSON string (SocialLinks precedent), hidden publicly
/// when empty.
/// </summary>
public class StudioSetupDto
{
    /// <summary>
    /// DAW tags — same chip/tag-list shape and validation as AiTools/Instruments/
    /// Hardware/Plugins/Gear (was previously a single free-text string, which is
    /// why the editor UI didn't render it as a chip field like its siblings).
    /// <see cref="FlexibleStringListConverter"/> still accepts a legacy plain
    /// JSON string on read (wraps it as a single-item list) so profiles saved
    /// before this change don't fail to deserialize; every save from now on
    /// writes the array shape.
    /// </summary>
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? Daw { get; set; }

    /// <summary>AI tools tags (e.g. "Suno v5.5", "Udio", "RVC").</summary>
    public List<string>? AiTools { get; set; }

    /// <summary>Instrument tags (e.g. "Fender Jazz Bass", "kalimba").</summary>
    public List<string>? Instruments { get; set; }

    /// <summary>Hardware tags (interfaces, synths, controllers, monitors).</summary>
    public List<string>? Hardware { get; set; }

    /// <summary>Plugin tags (e.g. "FabFilter Pro-Q 4", "Valhalla VintageVerb").</summary>
    public List<string>? Plugins { get; set; }

    /// <summary>Anything-else gear tags (mics, pedals, field recorders…).</summary>
    public List<string>? Gear { get; set; }

    /// <summary>Creative chain / workflow description, free text.</summary>
    [StringLength(2000)]
    [SafeMetadata]
    public string? WorkflowNotes { get; set; }
}

/// <summary>
/// Deserializes <see cref="StudioSetupDto.Daw"/> from either the current tag-list
/// array shape or the legacy single free-text string (profiles saved before Daw
/// became a tag list) — wraps a legacy string as a single-item list. Always
/// writes the array shape, so every save going forward migrates the field.
/// </summary>
public class FlexibleStringListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : new List<string> { value };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = JsonSerializer.Deserialize<List<string>>(ref reader, options);
            return list is null || list.Count == 0 ? null : list;
        }

        // Unrecognized shape (number, object, etc.) — skip rather than throw so one
        // malformed legacy field never breaks deserializing the whole profile.
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}
