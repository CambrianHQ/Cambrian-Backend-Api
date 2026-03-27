namespace Cambrian.Application.Interfaces;

public interface IActivityBackfillService
{
    Task BackfillAsync(CancellationToken ct);
}
