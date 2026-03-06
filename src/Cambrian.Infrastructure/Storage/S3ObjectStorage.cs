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

    public Task<string> CreateUploadUrlAsync(string key, CancellationToken cancellationToken = default)
    {
        var baseUrl = _options.Endpoint.TrimEnd('/');
        return Task.FromResult($"{baseUrl}/{_options.Bucket}/{key}");
    }
}
