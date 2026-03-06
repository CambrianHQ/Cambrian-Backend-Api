using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.Storage;

public sealed class S3ObjectStorage : IObjectStorage
{
    private readonly StorageOptions _options;

    public S3ObjectStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;
    }

    public Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
    {
        // TODO: Implement S3 upload via AWS SDK
        var baseUrl = _options.Endpoint.TrimEnd('/');
        var url = $"{baseUrl}/{_options.Bucket}/{key}";
        return Task.FromResult(url);
    }

    public string GenerateSignedUrl(string key)
    {
        // TODO: Implement signed URL generation via S3 pre-signed URLs
        var baseUrl = _options.Endpoint.TrimEnd('/');
        return $"{baseUrl}/{_options.Bucket}/{key}?signed=dev";
    }

    public Task DeleteAsync(string key)
    {
        // TODO: Implement S3 delete
        return Task.CompletedTask;
    }
}
