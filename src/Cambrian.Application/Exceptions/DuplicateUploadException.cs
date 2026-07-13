namespace Cambrian.Application.Exceptions;

public sealed class DuplicateUploadException : InvalidOperationException
{
    public DuplicateUploadException(Guid existingTrackId)
        : base("This audio already exists in your active catalog. Confirm duplicate upload to create another track.")
        => ExistingTrackId = existingTrackId;

    public Guid ExistingTrackId { get; }
}
