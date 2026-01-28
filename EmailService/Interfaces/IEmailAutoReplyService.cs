namespace EmailService.Interfaces;

/// <summary>
/// Interface for email auto-reply service.
/// </summary>
public interface IEmailAutoReplyService
{
    /// <summary>
    /// Checks auto-reply rules and sends a reply if needed.
    /// </summary>
    /// <param name="toAddress">The recipient email address.</param>
    /// <param name="thread">The thread to associate the reply with.</param>
    /// <param name="originalSubject">The original email subject for proper threading.</param>
    /// <param name="inReplyTo">Optional Message-ID to reply to.</param>
    /// <param name="ct">The cancellation token.</param>
    Task SendAutoReplyIfNeededAsync(
        string toAddress,
        DatabaseAccess.Models.Thread thread,
        string originalSubject,
        string? inReplyTo = null,
        CancellationToken ct = default
    ) => throw new NotImplementedException();
}
