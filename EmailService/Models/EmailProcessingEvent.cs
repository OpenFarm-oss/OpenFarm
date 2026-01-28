namespace EmailService.Models;

/// <summary>
/// Wide event model capturing all context for a single email processing operation.
/// Designed for structured logging - one comprehensive event per email processed.
/// </summary>
public sealed class EmailProcessingEvent
{
    /// Operation type identifier for log filtering.
    public string Operation => "process_email";

    /// IMAP unique identifier for the message.
    public uint MessageUid { get; set; }

    /// Sender's email address.
    public string? Sender { get; set; }

    /// Original email subject line.
    public string? Subject { get; set; }

    /// Normalized subject used for thread matching.
    public string? NormalizedSubject { get; set; }

    /// RFC 2822 Message-ID header.
    public string? MessageId { get; set; }

    /// Whether the email has an Outlook Thread-Index header.
    public bool HasThreadIndex { get; set; }

    /// Timestamp when the email was received (from headers).
    public DateTime? ReceivedAt { get; set; }

    /// Job ID extracted from subject, if any.
    public long? JobId { get; set; }

    /// Whether this email is a reply to a previous message.
    public bool IsReply { get; set; }

    /// Thread information after database lookup/creation.
    public ThreadInfo Thread { get; set; } = new();

    /// Auto-reply processing details.
    public AutoReplyInfo AutoReply { get; set; } = new();

    /// Final processing outcome.
    public string Outcome { get; set; } = "pending";

    /// Error type if processing failed.
    public string? ErrorType { get; set; }

    /// Error message if processing failed.
    public string? ErrorMessage { get; set; }

    /// Timing measurements for each processing phase.
    public TimingInfo Timing { get; set; } = new();

    /// Whether the message was marked as read in IMAP.
    public bool MarkedAsRead { get; set; }

    /// Whether the message was successfully stored in the database.
    public bool MessageStored { get; set; }
}

/// <summary>Thread-related context for the wide event.</summary>
public sealed class ThreadInfo
{
    /// Database thread ID.
    public long? Id { get; set; }

    /// Whether this is a newly created thread (vs existing).
    public bool Created { get; set; }

    /// Error during thread lookup/creation, if any.
    public string? Error { get; set; }
}

/// <summary>Auto-reply processing context for the wide event.</summary>
public sealed class AutoReplyInfo
{
    /// Whether auto-reply was attempted.
    public bool Attempted { get; set; }

    /// Whether auto-reply was successfully sent.
    public bool Sent { get; set; }

    /// Reason auto-reply was skipped, if applicable.
    public string? SkipReason { get; set; }

    /// Error during auto-reply, if any.
    public string? Error { get; set; }
}

/// <summary>Timing measurements for each processing phase in milliseconds.</summary>
public sealed class TimingInfo
{
    /// Time to fetch message from IMAP server.
    public long ImapFetchMs { get; set; }

    /// Time to extract and parse email information.
    public long ExtractMs { get; set; }

    /// Time for database thread lookup/creation.
    public long ThreadLookupMs { get; set; }

    /// Time to store message in database.
    public long DbStoreMs { get; set; }

    /// Time for auto-reply processing.
    public long AutoReplyMs { get; set; }

    /// Time to mark message as read in IMAP.
    public long MarkReadMs { get; set; }

    /// Total processing time for this email.
    public long TotalMs { get; set; }
}

/// <summary>
/// Wide event model for an entire polling cycle summary.
/// Emitted once per poll with aggregate statistics.
/// </summary>
public sealed class PollCycleEvent
{
    /// Operation type identifier for log filtering.
    public string Operation => "poll_cycle";

    /// Number of unread emails found.
    public int EmailsFound { get; set; }

    /// Number of emails successfully processed.
    public int EmailsSucceeded { get; set; }

    /// Number of emails that failed processing.
    public int EmailsFailed { get; set; }

    /// Number of emails skipped (e.g., no sender).
    public int EmailsSkipped { get; set; }

    /// Time to connect to IMAP server.
    public long ImapConnectMs { get; set; }

    /// Time to search for unread messages.
    public long SearchMs { get; set; }

    /// Total cycle duration.
    public long TotalMs { get; set; }

    /// Error if the entire cycle failed.
    public string? Error { get; set; }
}

/// <summary>
/// Wide event model for job notification emails sent via RabbitMQ queue.
/// One event per job notification message processed.
/// </summary>
public sealed class JobNotificationEvent
{
    /// Operation type identifier for log filtering.
    public string Operation => "job_notification";

    /// Type of RabbitMQ message received.
    public string? MessageType { get; set; }

    /// Job ID for the notification.
    public long JobId { get; set; }

    /// Human-readable email type (e.g., "Job Received").
    public string? EmailType { get; set; }

    /// Template file used for rendering.
    public string? Template { get; set; }

    /// Recipient email address.
    public string? Recipient { get; set; }

    /// Final outcome: success, failed, no_recipient.
    public string Outcome { get; set; } = "pending";

    /// Error type if processing failed.
    public string? ErrorType { get; set; }

    /// Error message if processing failed.
    public string? ErrorMessage { get; set; }

    /// Timing measurements.
    public JobNotificationTiming Timing { get; set; } = new();
}

/// <summary>Timing for job notification processing.</summary>
public sealed class JobNotificationTiming
{
    /// Time to look up recipient email from database.
    public long DbLookupMs { get; set; }

    /// Time to render the email template.
    public long RenderMs { get; set; }

    /// Time to send the email via SMTP.
    public long SendMs { get; set; }

    /// Total processing time.
    public long TotalMs { get; set; }
}

/// <summary>
/// Wide event model for operator reply emails.
/// One event per operator reply message processed.
/// </summary>
public sealed class OperatorReplyEvent
{
    /// Operation type identifier for log filtering.
    public string Operation => "operator_reply";

    /// Thread ID being replied to.
    public long ThreadId { get; set; }

    /// Recipient email address.
    public string? Recipient { get; set; }

    /// Email subject.
    public string? Subject { get; set; }

    /// Whether threading headers were added.
    public bool HasThreading { get; set; }

    /// Number of message references in the thread.
    public int ReferencesCount { get; set; }

    /// Final outcome: success, failed, empty_body.
    public string Outcome { get; set; } = "pending";

    /// Whether the sent message was persisted to the database.
    public bool Persisted { get; set; }

    /// Error type if processing failed.
    public string? ErrorType { get; set; }

    /// Error message if processing failed.
    public string? ErrorMessage { get; set; }

    /// Timing measurements.
    public OperatorReplyTiming Timing { get; set; } = new();
}

/// <summary>Timing for operator reply processing.</summary>
public sealed class OperatorReplyTiming
{
    /// Time to look up thread messages for threading.
    public long ThreadLookupMs { get; set; }

    /// Time to render the email template.
    public long RenderMs { get; set; }

    /// Time to send the email via SMTP.
    public long SendMs { get; set; }

    /// Time to persist the message to database.
    public long DbPersistMs { get; set; }

    /// Total processing time.
    public long TotalMs { get; set; }
}

/// <summary>
/// Wide event model for SMTP email send operations.
/// One event per email sent through the delivery service.
/// </summary>
public sealed class EmailSendEvent
{
    /// Operation type identifier for log filtering.
    public string Operation => "email_send";

    /// Recipient email address.
    public string? Recipient { get; set; }

    /// Email subject line.
    public string? Subject { get; set; }

    /// Generated Message-ID for the email.
    public string? MessageId { get; set; }

    /// Whether In-Reply-To header was set.
    public bool HasInReplyTo { get; set; }

    /// Whether References header was set.
    public bool HasReferences { get; set; }

    /// Whether Thread-Index was generated for Outlook.
    public bool HasThreadIndex { get; set; }

    /// Number of retry attempts made.
    public int RetryCount { get; set; }

    /// Final outcome: success, failed.
    public string Outcome { get; set; } = "pending";

    /// Error type if send failed.
    public string? ErrorType { get; set; }

    /// Error message if send failed.
    public string? ErrorMessage { get; set; }

    /// Timing measurements.
    public EmailSendTiming Timing { get; set; } = new();
}

/// <summary>Timing for email send operations.</summary>
public sealed class EmailSendTiming
{
    /// Time to connect to SMTP server (if reconnection needed).
    public long ConnectMs { get; set; }

    /// Time to send the email.
    public long SendMs { get; set; }

    /// Total operation time including retries.
    public long TotalMs { get; set; }
}

/// <summary>
/// Wide event model for auto-reply processing.
/// One event per auto-reply evaluation (whether sent or not).
/// </summary>
public sealed class AutoReplyEvent
{
    /// Operation type identifier for log filtering.
    public string Operation => "auto_reply";

    /// Recipient email address.
    public string? Recipient { get; set; }

    /// Thread ID for the conversation.
    public long ThreadId { get; set; }

    /// Number of auto-reply rules evaluated.
    public int RulesEvaluatedCount { get; set; }

    /// Whether a rule matched.
    public bool RuleMatched { get; set; }

    /// ID of the matched rule, if any.
    public long? MatchedRuleId { get; set; }

    /// Final outcome: sent, no_match, skipped, failed.
    public string Outcome { get; set; } = "pending";

    /// Whether the sent message was persisted to database.
    public bool Persisted { get; set; }

    /// Error type if processing failed.
    public string? ErrorType { get; set; }

    /// Error message if processing failed.
    public string? ErrorMessage { get; set; }

    /// Timing measurements.
    public AutoReplyTiming Timing { get; set; } = new();
}

/// <summary>Timing for auto-reply processing.</summary>
public sealed class AutoReplyTiming
{
    /// Time to evaluate auto-reply rules.
    public long RuleEvalMs { get; set; }

    /// Time to render the template.
    public long RenderMs { get; set; }

    /// Time to send the email.
    public long SendMs { get; set; }

    /// Time to persist to database.
    public long PersistMs { get; set; }

    /// Total processing time.
    public long TotalMs { get; set; }
}
