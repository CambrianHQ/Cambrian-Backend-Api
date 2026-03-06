namespace Cambrian.Infrastructure.Options;

public sealed class StorageOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "auto";
    public bool UsePathStyle { get; set; } = true;
}
