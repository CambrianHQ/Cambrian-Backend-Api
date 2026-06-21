using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Cambrian.Application.Validation;

public static class MetadataSanitizer
{
    private static readonly SafeMetadataAttribute Validator = new();

    public static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{fieldName} is required.");
        Validate(normalized, fieldName);
        return normalized;
    }

    public static string? NormalizeOptional(string? value, string fieldName)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        Validate(normalized, fieldName);
        return normalized;
    }

    public static string NormalizeAllowEmpty(string? value, string fieldName)
    {
        var normalized = Normalize(value) ?? "";
        if (normalized.Length > 0)
            Validate(normalized, fieldName);
        return normalized;
    }

    private static string? Normalize(string? value) =>
        value?.Normalize(NormalizationForm.FormKC).Trim();

    private static void Validate(string value, string fieldName)
    {
        if (!Validator.IsValid(value))
            throw new ValidationException($"{fieldName} contains unsafe characters.");
    }
}
