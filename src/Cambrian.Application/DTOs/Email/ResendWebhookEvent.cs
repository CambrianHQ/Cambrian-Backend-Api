using System.Text.Json.Serialization;

namespace Cambrian.Application.DTOs.Email;

public class ResendWebhookEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("data")]
    public ResendEmailData? Data { get; set; }
}

public class ResendEmailData
{
    [JsonPropertyName("email_id")]
    public string EmailId { get; set; } = "";

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public List<string> To { get; set; } = [];

    [JsonPropertyName("cc")]
    public List<string> Cc { get; set; } = [];

    [JsonPropertyName("bcc")]
    public List<string> Bcc { get; set; } = [];

    [JsonPropertyName("message_id")]
    public string MessageId { get; set; } = "";

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("attachments")]
    public List<ResendEmailAttachment> Attachments { get; set; } = [];
}

public class ResendEmailAttachment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "";

    [JsonPropertyName("content_disposition")]
    public string ContentDisposition { get; set; } = "";

    [JsonPropertyName("content_id")]
    public string ContentId { get; set; } = "";
}
