using System.Net;

namespace Cambrian.Infrastructure.Email;

public static class EmailTemplateEncoding
{
    public static string Text(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static string Subject(string? value) =>
        (value ?? "").Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    public static string Href(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Email link is required.");

        if (value.StartsWith("/", StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal))
            return WebUtility.HtmlEncode(value);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Email link must use http or https.");
        }

        return WebUtility.HtmlEncode(uri.AbsoluteUri);
    }
}
