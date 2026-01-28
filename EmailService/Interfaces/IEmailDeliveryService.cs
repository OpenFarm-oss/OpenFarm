namespace EmailService.Interfaces;

/// <summary>
/// Interface for sending emails.
/// </summary>
public interface IEmailDeliveryService
{
    /// Gets the configured sender email address.
    string SenderEmail { get; }

    /// <summary>
    /// Sends an email.
    /// </summary>
    /// <param name="to">The recipient of the email.</param>
    /// <param name="subject">The subject of the email.</param>
    /// <param name="htmlBody">The HTML body of the email.</param>
    /// <param name="inReplyTo">Optional Message-ID to reply to (used for In-Reply-To header).</param>
    /// <param name="references">Optional References header value (space separated Message-IDs).</param>
    /// <param name="parentThreadIndex">Optional parent Thread-Index for Outlook conversation threading.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>
    /// A tuple containing the Message-ID and Thread-Index of the sent email,
    /// or null values if sending failed.
    /// </returns>
    Task<(string? MessageId, string? ThreadIndex)> SendAsync(
        string to,
        string subject,
        string htmlBody,
        string? inReplyTo = null,
        string? references = null,
        string? parentThreadIndex = null,
        CancellationToken ct = default
    ) => throw new NotImplementedException();
}
