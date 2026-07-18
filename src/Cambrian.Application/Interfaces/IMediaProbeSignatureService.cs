namespace Cambrian.Application.Interfaces;

public interface IMediaProbeSignatureService
{
    string Create(Guid trackId);
    bool Validate(string? signature, Guid trackId);
}
