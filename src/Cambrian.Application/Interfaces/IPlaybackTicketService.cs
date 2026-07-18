namespace Cambrian.Application.Interfaces;

public sealed record PlaybackTicketIssue(
    string Ticket,
    string TicketId,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc);

public sealed record PlaybackTicketValidation(
    bool IsValid,
    string? FailureCode,
    Guid TrackId,
    string? AuthorizedUserId,
    string? TicketId,
    DateTime? ExpiresAtUtc);

public interface IPlaybackTicketService
{
    PlaybackTicketIssue Issue(Guid trackId, string? authorizedUserId = null);
    PlaybackTicketValidation Validate(string? ticket, Guid expectedTrackId);
}
