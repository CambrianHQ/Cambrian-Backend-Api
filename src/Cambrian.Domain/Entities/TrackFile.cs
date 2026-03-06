namespace Cambrian.Domain.Entities;

public sealed class TrackFile
{
    public Guid Id { get; set; }
    public Guid TrackId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
}
