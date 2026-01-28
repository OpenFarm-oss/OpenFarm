using System.Diagnostics;

using DatabaseAccess;

using EmailService.Constants;
using EmailService.Interfaces;
using EmailService.Models;
using EmailService.Utilities;

using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Serilog;

namespace EmailService.Services;

/// <summary>
/// Background service responsible for polling and processing incoming emails from Gmail via IMAP.
/// </summary>
/// <remarks>
/// <para>
/// This service connects to Gmail using IMAPS (implicit SSL/TLS) and periodically polls for
/// unread messages. Each message is processed by:
/// </para>
/// <list type="number">
/// <item><description>Extracting sender, subject, body, and metadata</description></item>
/// <item><description>Finding or creating a conversation thread in the database</description></item>
/// <item><description>Storing the message and triggering auto-replies if configured</description></item>
/// <item><description>Marking the message as read in Gmail</description></item>
/// </list>
/// <para>
/// Uses wide event logging pattern: emits ONE comprehensive structured event per email
/// with all context, timing, and outcome information for observability.
/// </para>
/// </remarks>
public sealed class IncomingEmailService : BackgroundService
{
    private readonly Serilog.ILogger _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _imapServer;
    private readonly int _imapPort;
    private readonly string _emailAccount;
    private readonly string _emailPassword;
    private readonly TimeSpan _pollInterval;

    /// Default polling interval in minutes when not configured.
    private const int DefaultPollIntervalMinutes = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncomingEmailService"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="scopeFactory">Factory for creating service scopes to access scoped services.</param>
    /// <param name="configuration">Application configuration containing IMAP settings.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when required configuration values (IMAP_SERVER, IMAP_PORT, GMAIL_EMAIL,
    /// or GMAIL_APP_PASSWORD) are missing or invalid.
    /// </exception>
    public IncomingEmailService(
        Serilog.ILogger logger,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _logger = logger.ForContext<IncomingEmailService>();
        _scopeFactory = scopeFactory;

        _imapServer = configuration["IMAP_SERVER"] ?? throw new ArgumentException("IMAP_SERVER not set");
        var portStr = configuration["IMAP_PORT"] ?? throw new ArgumentException("IMAP_PORT not set");
        if (!int.TryParse(portStr, out _imapPort)) throw new ArgumentException("IMAP_PORT invalid");

        _emailAccount = configuration["GMAIL_EMAIL"] ?? throw new ArgumentException("GMAIL_EMAIL not set");
        _emailPassword = configuration["GMAIL_APP_PASSWORD"] ?? throw new ArgumentException("GMAIL_APP_PASSWORD not set");

        var intervalStr = configuration["IMAP_POLL_INTERVAL_MINUTES"];
        _pollInterval = TimeSpan.FromMinutes(
            int.TryParse(intervalStr, out var interval) ? interval : DefaultPollIntervalMinutes);
    }

    /// <summary>
    /// Executes the background polling loop for incoming emails.
    /// </summary>
    /// <param name="stoppingToken">Token that signals when the service should stop.</param>
    /// <returns>A task representing the background operation.</returns>
    /// <remarks>
    /// The service continues polling at the configured interval until cancellation is requested.
    /// Exceptions during processing are logged but do not stop the service.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information(
            "IncomingEmailService started with {PollIntervalMinutes} minute poll interval on {ImapServer}:{ImapPort}",
            _pollInterval.TotalMinutes, _imapServer, _imapPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessIncomingEmailsAsync(stoppingToken);

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.Information("IncomingEmailService shutting down");
    }

    /// <summary>
    /// Connects to the IMAP server and processes all unread emails.
    /// Emits a poll cycle summary event with aggregate statistics.
    /// </summary>
    /// <param name="cancellationToken">Token for cancelling the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ProcessIncomingEmailsAsync(CancellationToken cancellationToken)
    {
        var cycleEvent = new PollCycleEvent();
        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        using var imapClient = new ImapClient();

        try
        {
            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 1: IMAP Connection - Connect and authenticate to Gmail
            // Uses IMAPS (implicit TLS) for secure communication
            // ═══════════════════════════════════════════════════════════════════════
            await imapClient.ConnectAsync(_imapServer, _imapPort, true, cancellationToken);
            await imapClient.AuthenticateAsync(_emailAccount, _emailPassword, cancellationToken);
            cycleEvent.ImapConnectMs = phaseStopwatch.ElapsedMilliseconds;

            // Open inbox with read/write access (needed to mark messages as read)
            var mailFolder = imapClient.Inbox;
            await mailFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 2: Search - Find all unread (unseen) messages in inbox
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var unreadIds = await mailFolder.SearchAsync(SearchQuery.NotSeen, cancellationToken);
            cycleEvent.SearchMs = phaseStopwatch.ElapsedMilliseconds;
            cycleEvent.EmailsFound = unreadIds.Count;

            // Early exit if no new emails to process
            if (unreadIds.Count == 0)
            {
                cycleEvent.TotalMs = totalStopwatch.ElapsedMilliseconds;
                LogPollCycle(cycleEvent);
                return;
            }

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 3: Process Loop - Handle each unread email sequentially
            // Each message goes through: fetch → extract → store → auto-reply → mark read
            // ═══════════════════════════════════════════════════════════════════════
            foreach (var messageUid in unreadIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ProcessSingleMessageAsync(mailFolder, messageUid, cancellationToken);

                // Track outcomes for poll cycle summary logging
                switch (result)
                {
                    case "success":
                        cycleEvent.EmailsSucceeded++;
                        break;
                    case "skipped":
                        cycleEvent.EmailsSkipped++;
                        break;
                    default:
                        cycleEvent.EmailsFailed++;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Let the caller handle shutdown
        }
        catch (Exception ex)
        {
            cycleEvent.Error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            await SafeDisconnectAsync(imapClient, cancellationToken);
            cycleEvent.TotalMs = totalStopwatch.ElapsedMilliseconds;
            LogPollCycle(cycleEvent);
        }
    }

    /// <summary>
    /// Downloads and processes a single email message, emitting a wide event with all context.
    /// </summary>
    /// <param name="mailFolder">The IMAP folder containing the message.</param>
    /// <param name="messageUid">The unique identifier of the message.</param>
    /// <param name="cancellationToken">Token for cancelling the operation.</param>
    /// <returns>The processing outcome: "success", "skipped", or "failed".</returns>
    private async Task<string> ProcessSingleMessageAsync(
        IMailFolder mailFolder,
        UniqueId messageUid,
        CancellationToken cancellationToken)
    {
        var evt = new EmailProcessingEvent { MessageUid = messageUid.Id };
        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        try
        {
            // ═══════════════════════════════════════════════════════════════════════
            // PHASE A: Fetch - Download the full message from IMAP server
            // ═══════════════════════════════════════════════════════════════════════
            var message = await mailFolder.GetMessageAsync(messageUid, cancellationToken);
            evt.Timing.ImapFetchMs = phaseStopwatch.ElapsedMilliseconds;

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE B: Extract - Parse sender, subject, body, and metadata
            // Handles attachments, threading info, and job ID extraction
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var emailInfo = ExtractEmailInformation(message);
            evt.Timing.ExtractMs = phaseStopwatch.ElapsedMilliseconds;

            // Populate event with email metadata
            evt.Sender = emailInfo.SenderAddress;
            evt.Subject = emailInfo.Subject;
            evt.MessageId = emailInfo.MessageId;
            evt.HasThreadIndex = !string.IsNullOrEmpty(emailInfo.ThreadIndex);
            evt.ReceivedAt = emailInfo.ReceivedAt;
            evt.JobId = emailInfo.JobId;
            evt.IsReply = emailInfo.IsReply;

            // Skip emails without a valid sender (spam, malformed, etc.)
            if (string.IsNullOrWhiteSpace(emailInfo.SenderAddress))
            {
                evt.Outcome = "skipped";
                evt.ErrorMessage = "No valid sender address";
                await MarkAsReadAsync(mailFolder, messageUid, evt, cancellationToken);
                return "skipped";
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseAccessHelper>();

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE C: Thread Management - Find or create conversation thread
            // Links emails by: job ID > normalized subject > sender address
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var normalizedSubject = SubjectNormalizer.Normalize(emailInfo.Subject).ToLowerInvariant();
            evt.NormalizedSubject = normalizedSubject;

            var (threadResult, thread) = await db.Thread.FindOrCreateThreadForEmailAsync(
                emailInfo.SenderAddress, emailInfo.JobId, normalizedSubject, emailInfo.IsReply);
            evt.Timing.ThreadLookupMs = phaseStopwatch.ElapsedMilliseconds;

            if (threadResult != TransactionResult.Succeeded || thread is null)
            {
                evt.Thread.Error = $"Thread lookup failed: {threadResult}";
                evt.Outcome = "failed";
                return "failed";
            }

            evt.Thread.Id = thread.Id;
            evt.Thread.Created = threadResult == TransactionResult.Succeeded && thread.EmailMessages.Count == 0;

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE D: Database Storage - Persist message to thread history
            // Stores content, headers (Message-ID, Thread-Index) for future threading
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var (messageResult, _) = await db.Message.CreateEmailMessageAsync(
                threadId: thread.Id,
                fromEmailAddress: emailInfo.SenderAddress,
                messageSubject: emailInfo.Subject,
                messageContent: emailInfo.Body,
                senderType: "user",
                messageStatus: "unseen",
                internetMessageId: emailInfo.MessageId,
                threadIndex: emailInfo.ThreadIndex,
                createdAtUtc: emailInfo.ReceivedAt);
            evt.Timing.DbStoreMs = phaseStopwatch.ElapsedMilliseconds;

            evt.MessageStored = messageResult is TransactionResult.Succeeded or TransactionResult.NoAction;

            if (evt.MessageStored)
            {
                // Mark thread as unresolved since user sent a message
                await db.Thread.MarkThreadUnresolvedAsync(thread.Id);

                // ═══════════════════════════════════════════════════════════════════════
                // PHASE E: Auto-Reply - Check rules and send automatic response if needed
                // Skips no-reply addresses and our own outgoing emails
                // ═══════════════════════════════════════════════════════════════════════
                phaseStopwatch.Restart();
                await ProcessAutoReplyAsync(scope, emailInfo.SenderAddress, thread, emailInfo.Subject, emailInfo.MessageId, evt, cancellationToken);
                evt.Timing.AutoReplyMs = phaseStopwatch.ElapsedMilliseconds;

                // ═══════════════════════════════════════════════════════════════════════
                // PHASE F: Cleanup - Mark email as read in Gmail
                // Prevents re-processing on next poll cycle
                // ═══════════════════════════════════════════════════════════════════════
                await MarkAsReadAsync(mailFolder, messageUid, evt, cancellationToken);
            }

            evt.Outcome = "success";
            return "success";
        }
        catch (Exception ex)
        {
            evt.Outcome = "failed";
            evt.ErrorType = ex.GetType().Name;
            evt.ErrorMessage = ex.Message;
            return "failed";
        }
        finally
        {
            evt.Timing.TotalMs = totalStopwatch.ElapsedMilliseconds;
            LogEmailProcessed(evt);
        }
    }

    /// <summary>
    /// Attempts to send an auto-reply if conditions are met. Updates the event with outcome.
    /// </summary>
    private async Task ProcessAutoReplyAsync(
        IServiceScope scope,
        string senderAddress,
        DatabaseAccess.Models.Thread thread,
        string originalSubject,
        string messageId,
        EmailProcessingEvent evt,
        CancellationToken cancellationToken)
    {
        var skipReason = GetAutoReplySkipReason(senderAddress);
        if (skipReason != null)
        {
            evt.AutoReply.Attempted = false;
            evt.AutoReply.SkipReason = skipReason;
            return;
        }

        evt.AutoReply.Attempted = true;

        try
        {
            var autoReply = scope.ServiceProvider.GetRequiredService<IEmailAutoReplyService>();
            await autoReply.SendAutoReplyIfNeededAsync(senderAddress, thread, originalSubject, messageId, cancellationToken);
            evt.AutoReply.Sent = true;
        }
        catch (Exception ex)
        {
            evt.AutoReply.Error = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the reason an auto-reply should be skipped, or null if auto-reply should be attempted.
    /// </summary>
    private string? GetAutoReplySkipReason(string senderAddress)
    {
        if (string.IsNullOrWhiteSpace(senderAddress))
            return "empty_sender";

        var normalized = senderAddress.Trim().ToLowerInvariant();

        if (string.Equals(normalized, _emailAccount.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            return "own_address";

        if (normalized.Contains("no-reply") || normalized.Contains("noreply"))
            return "noreply_address";

        return null;
    }

    /// <summary>
    /// Extracts all relevant information from a MIME message.
    /// </summary>
    private static EmailInfo ExtractEmailInformation(MimeMessage message)
    {
        var sender = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
        var subject = string.IsNullOrWhiteSpace(message.Subject)
            ? EmailDefaults.DefaultSubject
            : message.Subject.Trim();
        var receivedAt = message.Date.UtcDateTime == DateTime.MinValue ? DateTime.UtcNow : message.Date.UtcDateTime;

        var body = EmailBodyParser.GetCleanBodyText(message);

        if (message.Attachments?.Any() is true)
        {
            var attachmentNote = TextFormatting.BuildAttachmentNote(message.MessageId);
            body = string.IsNullOrWhiteSpace(body)
                ? attachmentNote
                : $"{body}{Environment.NewLine}{Environment.NewLine}{attachmentNote}";
        }

        if (string.IsNullOrWhiteSpace(body)) body = EmailDefaults.DefaultBodyContent;

        var jobId = SubjectNormalizer.ExtractJobId(subject);
        var isReply = !string.IsNullOrWhiteSpace(message.InReplyTo) ||
                      SubjectNormalizer.IsReplySubject(subject);

        var threadIndex = message.Headers["Thread-Index"];

        return new EmailInfo
        {
            SenderAddress = sender,
            Subject = subject,
            Body = body,
            MessageId = message.MessageId ?? string.Empty,
            ThreadIndex = threadIndex,
            ReceivedAt = receivedAt,
            JobId = jobId,
            IsReply = isReply
        };
    }

    /// <summary>
    /// Marks an email as read (seen) in the IMAP folder. Updates the event with outcome.
    /// </summary>
    private async Task MarkAsReadAsync(
        IMailFolder folder,
        UniqueId messageUid,
        EmailProcessingEvent evt,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await folder.AddFlagsAsync(messageUid, MessageFlags.Seen, true, cancellationToken);
            evt.MarkedAsRead = true;
        }
        catch
        {
            evt.MarkedAsRead = false;
        }
        finally
        {
            evt.Timing.MarkReadMs = sw.ElapsedMilliseconds;
        }
    }

    /// <summary>
    /// Best-effort disconnect from the IMAP server.
    /// </summary>
    /// <remarks>
    /// Failures are harmless and not retried because:
    /// - If the server closed the connection, there's nothing to disconnect from
    /// - If the network failed, the TCP connection is dead anyway
    /// - The ImapClient is disposed by the using statement regardless
    /// - The next poll cycle creates a fresh connection
    /// </remarks>
    private static async Task SafeDisconnectAsync(ImapClient client, CancellationToken cancellationToken)
    {
        if (!client.IsConnected)
            return;

        try
        {
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch
        {
            // Best-effort cleanup - failures are self-resolving
        }
    }

    /// <summary>
    /// Emits a wide event for a single email processing operation.
    /// </summary>
    private void LogEmailProcessed(EmailProcessingEvent evt)
    {
        var level = evt.Outcome == "failed" ? Serilog.Events.LogEventLevel.Error : Serilog.Events.LogEventLevel.Information;
        var log = _logger.ForContext("EmailEvent", evt, destructureObjects: true);

        if (evt.Outcome == "success")
        {
             log.Write(level, 
                "Email Processed {Outcome}: {Subject} from {Sender} (Ref: {MessageId}) [Timing: {TotalMs}ms]", 
                evt.Outcome, evt.Subject, evt.Sender, evt.MessageId, evt.Timing.TotalMs);
        }
        else if (evt.Outcome == "skipped")
        {
            log.Write(level, 
                "Email Skipped: {Subject} from {Sender} - {ErrorMessage}", 
                evt.Subject, evt.Sender, evt.ErrorMessage);
        }
        else
        {
             log.Write(level, 
                "Email Processing {Outcome}: {Subject} from {Sender} - {ErrorType}: {ErrorMessage}", 
                evt.Outcome, evt.Subject, evt.Sender, evt.ErrorType, evt.ErrorMessage);
        }
    }

    /// <summary>
    /// Emits a wide event for a polling cycle summary.
    /// </summary>
    private void LogPollCycle(PollCycleEvent evt)
    {
        var level = evt.Error != null ? Serilog.Events.LogEventLevel.Error : Serilog.Events.LogEventLevel.Information;
        
        // Only log at Info level if emails were found, otherwise use Debug to reduce noise
        if (evt.Error == null && evt.EmailsFound == 0)
        {
            level = Serilog.Events.LogEventLevel.Debug;
        }

        var log = _logger.ForContext("PollCycleEvent", evt, destructureObjects: true);

        if (evt.Error != null)
        {
            log.Write(level, 
                "Poll Cycle Error: {Error} [Timing: {TotalMs}ms]", 
                evt.Error, evt.TotalMs);
        }
        else
        {
            log.Write(level, 
                "Poll Cycle: Found {EmailsFound} emails ({EmailsSucceeded} processed, {EmailsFailed} failed, {EmailsSkipped} skipped) [Timing: {TotalMs}ms]", 
                evt.EmailsFound, evt.EmailsSucceeded, evt.EmailsFailed, evt.EmailsSkipped, evt.TotalMs);
        }
    }
}
