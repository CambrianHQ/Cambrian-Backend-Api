using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Represents a unique Cambrian track identifier (format: CAMB-TRK-XXXX).
/// </summary>
public class TrackIdDto
{
    /// <summary>Unique Cambrian track identifier (e.g. CAMB-TRK-A1B2).</summary>
    [Required]
    [RegularExpression(@"^CAMB-TRK-[A-Z0-9]{4,12}$",
        ErrorMessage = "TrackId must match format CAMB-TRK-XXXX (4-12 alphanumeric chars).")]
    public string TrackId { get; set; } = string.Empty;

    /// <summary>
    /// Generate a new unique track identifier.
    /// </summary>
    public static string Generate()
    {
        // 8 uppercase alpha-numeric chars → ~2.8 trillion combinations
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        var suffix = new char[8];
        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = chars[random.Next(chars.Length)];
        return $"CAMB-TRK-{new string(suffix)}";
    }

    /// <summary>
    /// Validate that a string matches the CAMB-TRK-XXXX pattern.
    /// </summary>
    public static bool IsValid(string? trackId)
        => !string.IsNullOrWhiteSpace(trackId)
           && Regex.IsMatch(trackId, @"^CAMB-TRK-[A-Z0-9]{4,12}$");
}
