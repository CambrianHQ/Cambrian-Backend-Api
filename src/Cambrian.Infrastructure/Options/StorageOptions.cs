namespace Cambrian.Infrastructure.Options;

public sealed class StorageOptions
{
    /// <summary>Storage provider: "local", "s3", or "r2".</summary>
    public string Provider { get; set; } = "local";

    /// <summary>S3/R2 endpoint URL.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>S3/R2 bucket name.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>S3/R2 access key.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>S3/R2 secret key.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>AWS region (use "auto" for R2).</summary>
    public string Region { get; set; } = "auto";

    /// <summary>Force path-style addressing (required for R2).</summary>
    public bool UsePathStyle { get; set; } = true;

    /// <summary>Local disk path for dev storage.</summary>
    public string? LocalPath { get; set; } = "wwwroot/uploads";

    /// <summary>Public base URL for the bucket (e.g. R2 public bucket URL). Used for cover art.</summary>
    public string? PublicUrl { get; set; }
}
