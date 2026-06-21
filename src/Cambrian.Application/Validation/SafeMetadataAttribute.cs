using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;

namespace Cambrian.Application.Validation;

/// <summary>
/// Rejects markup delimiters and unsafe control characters in stored public
/// metadata. Output encoding is still required at every rendering sink.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SafeMetadataAttribute : ValidationAttribute
{
    public SafeMetadataAttribute()
    {
        ErrorMessage = "Metadata contains unsafe characters.";
    }

    public override bool IsValid(object? value)
    {
        if (value is null) return true;
        if (value is not string text) return false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '<' or '>' or 0x2028 or 0x2029)
                return false;

            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control
                && rune.Value is not '\r' and not '\n' and not '\t')
            {
                return false;
            }
        }

        return true;
    }
}
