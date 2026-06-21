using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.Creators;

/// <summary>
/// Request body for PUT /api/creator/me — update own creator profile.
/// Email changes are explicitly forbidden through this endpoint.
/// </summary>
public class UpdateCreatorProfileRequest
{
    [StringLength(40, MinimumLength = 3)]
    [RegularExpression(@"^[a-z0-9][a-z0-9\-]*[a-z0-9]$",
        ErrorMessage = "Username must be lowercase alphanumeric with optional hyphens, no leading/trailing hyphens.")]
    public string? Username { get; set; }

    [StringLength(100)]
    [SafeMetadata]
    public string? DisplayName { get; set; }

    [StringLength(2000)]
    [SafeMetadata]
    public string? Bio { get; set; }

    [StringLength(500)]
    public string? ProfileImageUrl { get; set; }

    [StringLength(500)]
    public string? CoverImageUrl { get; set; }

    public List<string>? Genres { get; set; }

    /// <summary>
    /// Accepts either an array of {platform,url} objects OR a {platform:url} dictionary.
    /// The frontend sends a dictionary; the backend stores as array.
    /// </summary>
    [JsonConverter(typeof(FlexibleSocialLinksConverter))]
    public List<SocialLinkItemDto>? SocialLinks { get; set; }
}

/// <summary>
/// Deserializes social links from either [{platform,url}] array or {platform:url} dictionary.
/// </summary>
public class FlexibleSocialLinksConverter : JsonConverter<List<SocialLinkItemDto>?>
{
    public override List<SocialLinkItemDto>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            // Standard array format: [{platform, url}]
            var list = new List<SocialLinkItemDto>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    var item = JsonSerializer.Deserialize<SocialLinkItemDto>(ref reader, options);
                    if (item is not null) list.Add(item);
                }
            }
            return list;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Dictionary format: { "spotify": "url", ... }
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options);
            if (dict is null) return null;
            return dict
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => new SocialLinkItemDto { Platform = kv.Key, Url = kv.Value })
                .ToList();
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, List<SocialLinkItemDto>? value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}

/// <summary>
/// Response for GET /api/creators/username-availability?username=...
/// </summary>
public class UsernameAvailabilityResponse
{
    public string Username { get; set; } = "";
    public bool Available { get; set; }
}

/// <summary>
/// Response for POST /api/uploads/creator-image-url
/// </summary>
public class CreatorImageUploadResponse
{
    public string UploadUrl { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// Request body for POST /api/uploads/creator-image-url (JSON presigned URL flow).
/// </summary>
public class CreateImageUploadRequest
{
    [RegularExpression("(?i)^(profile|cover|profile-image|cover-image)$")]
    public string? Type { get; set; }

    [RegularExpression("(?i)^image/(jpeg|png|webp)$")]
    public string? ContentType { get; set; }

    [StringLength(255)]
    public string? FileName { get; set; }
}
