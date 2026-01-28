namespace EmailService.Models;

/// <summary>
/// Container for extracted email information from a MIME message.
/// </summary>
public sealed class EmailInfo
{
    /// Gets the sender's email address.
    public required string SenderAddress { get; init; }

    /// Gets the email subject.
    public required string Subject { get; init; }

    /// Gets the extracted body text.
    public required string Body { get; init; }

    /// Gets the RFC 2822 Message-ID.
    public required string MessageId { get; init; }

    /// Gets the Outlook Thread-Index header for conversation threading, if present.
    public string? ThreadIndex { get; init; }

    /// Gets the timestamp when the email was received.
    public required DateTime ReceivedAt { get; init; }

    /// Gets the job ID extracted from the subject, if any.
    public long? JobId { get; init; }

    /// Gets whether this email appears to be a reply.
    public bool IsReply { get; init; }
}

