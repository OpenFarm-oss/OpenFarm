using System.Diagnostics;
using System.Text.RegularExpressions;

using EmailService.Interfaces;
using EmailService.Models;
using EmailService.Utilities;

using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Polly;

namespace EmailService.Services;

/// <summary>
/// Sends emails using MailKit SMTP with proper threading support for all major email clients.
/// Uses wide event logging for comprehensive observability.
/// </summary>
/// <remarks>
/// <para>
/// This service uses implicit SSL/TLS (SMTPS) for secure email transmission.
/// It implements connection pooling by reusing the SMTP client across multiple sends.
/// </para>
/// <para>
/// For email threading compatibility across clients:
/// <list type="bullet">
/// <item><description>Gmail/Apple Mail: Uses standard In-Reply-To and References headers (RFC 2822)</description></item>
/// <item><description>Outlook: Uses Thread-Topic and Thread-Index headers (Microsoft proprietary)</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class EmailDeliveryService : IEmailDeliveryService, IDisposable
{
    private readonly Serilog.ILogger _logger;
    private readonly SmtpClient _smtpClient;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// Compiled regex for stripping HTML tags from text.
    private static readonly Regex StripHtmlTagsRegex = new(
        @"<.*?>",
        RegexOptions.Compiled
    );

    private readonly string _smtpServer;
    private readonly int _smtpPort;
    private readonly string _senderName;
    private readonly string _senderPassword;

    /// <inheritdoc />
    public string SenderEmail { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailDeliveryService"/> class.
    /// </summary>
    /// <param name="logger">The logger for diagnostic output.</param>
    /// <param name="configuration">The configuration containing SMTP settings.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when required configuration values (SMTP_SERVER, SMTP_PORT, COMPANY_NAME,
    /// GMAIL_EMAIL, or GMAIL_APP_PASSWORD) are missing or invalid.
    /// </exception>
    public EmailDeliveryService(Serilog.ILogger logger, IConfiguration configuration)
    {
        _logger = logger.ForContext<EmailDeliveryService>();
        _smtpClient = new SmtpClient();

        _smtpServer = configuration["SMTP_SERVER"] ?? throw new ArgumentException("SMTP_SERVER not set");
        var portStr = configuration["SMTP_PORT"] ?? throw new ArgumentException("SMTP_PORT not set");
        if (!int.TryParse(portStr, out _smtpPort)) throw new ArgumentException("SMTP_PORT invalid");

        _senderName = configuration["COMPANY_NAME"] ?? throw new ArgumentException("COMPANY_NAME not set");
        SenderEmail = configuration["GMAIL_EMAIL"] ?? throw new ArgumentException("GMAIL_EMAIL not set");
        _senderPassword = configuration["GMAIL_APP_PASSWORD"] ?? throw new ArgumentException("GMAIL_APP_PASSWORD not set");
    }

    /// <inheritdoc />
    /// <remarks>
    /// This method constructs a MIME message with both plain text and HTML alternatives,
    /// adds appropriate threading headers for all major email clients, and sends the email
    /// via SMTP with automatic retry on transient failures.
    /// Emits a wide event with all context and timing on completion.
    /// </remarks>
    public async Task<(string? MessageId, string? ThreadIndex)> SendAsync(
        string to,
        string subject,
        string htmlBody,
        string? inReplyTo = null,
        string? references = null,
        string? parentThreadIndex = null,
        CancellationToken ct = default)
    {
        // ═══════════════════════════════════════════════════════════════════════
        // PHASE 1: Initialization - Set up event tracking and generate message ID
        // ═══════════════════════════════════════════════════════════════════════
        var evt = new EmailSendEvent
        {
            Recipient = to,
            Subject = subject,
            HasInReplyTo = !string.IsNullOrEmpty(inReplyTo),
            HasReferences = !string.IsNullOrEmpty(references)
        };
        var totalStopwatch = Stopwatch.StartNew();

        // Generate a unique Message-ID using sender's domain for RFC 2822 compliance
        var domain = SenderEmail.Split('@').LastOrDefault() ?? "localhost";
        var messageId = $"<{Guid.NewGuid()}@{domain}>";
        evt.MessageId = messageId;
        string? threadIndex = null;

        var retryCount = 0;

        // ═══════════════════════════════════════════════════════════════════════
        // PHASE 2: Retry Policy - Configure exponential backoff for transient failures
        // Handles SMTP errors, protocol issues, and network I/O failures
        // ═══════════════════════════════════════════════════════════════════════
        var retryPolicy = Policy
            .Handle<SmtpCommandException>()   // SMTP-level errors (e.g., rejected, rate limited)
            .Or<SmtpProtocolException>()      // Protocol violations or unexpected responses
            .Or<IOException>()                // Network failures, connection drops
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (_, _, attemptNumber, _) =>
                {
                    retryCount = attemptNumber;
                });

        try
        {
            await retryPolicy.ExecuteAsync(async () =>
            {
                // ═══════════════════════════════════════════════════════════════
                // PHASE 3: Message Construction - Build the MIME message
                // ═══════════════════════════════════════════════════════════════
                var message = new MimeMessage();

                // Set sender (with display name) and recipient
                message.From.Add(new MailboxAddress(_senderName, SenderEmail));
                message.To.Add(MailboxAddress.Parse(to));
                message.Subject = subject;
                message.MessageId = messageId.Trim('<', '>');

                // ═══════════════════════════════════════════════════════════════
                // PHASE 4: Threading Headers - Enable email conversation threading
                // RFC 2822 headers for Gmail/Apple Mail compatibility
                // ═══════════════════════════════════════════════════════════════
                if (!string.IsNullOrEmpty(inReplyTo))
                {
                    // In-Reply-To points to the immediate parent message
                    message.InReplyTo = inReplyTo.Trim('<', '>');
                }

                if (!string.IsNullOrEmpty(references))
                {
                    // References contains the full thread chain (all ancestor message IDs)
                    foreach (var refId in references.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        message.References.Add(refId.Trim('<', '>'));
                    }
                }
                else if (!string.IsNullOrEmpty(inReplyTo))
                {
                    // Fallback: use In-Reply-To as sole reference if no chain provided
                    message.References.Add(inReplyTo.Trim('<', '>'));
                }

                // Microsoft Outlook uses proprietary Thread-Topic and Thread-Index headers
                threadIndex = OutlookThreadingHelper.AddThreadingHeaders(message, subject, parentThreadIndex);
                evt.HasThreadIndex = threadIndex is not null;

                // ═══════════════════════════════════════════════════════════════
                // PHASE 5: Body Construction - Create multipart/alternative body
                // Includes both plain text (for basic clients) and HTML versions
                // ═══════════════════════════════════════════════════════════════
                var plainText = StripHtmlTagsRegex.Replace(htmlBody, string.Empty);
                var builder = new BodyBuilder
                {
                    TextBody = plainText,   // Fallback for text-only email clients
                    HtmlBody = htmlBody     // Rich HTML version for modern clients
                };
                message.Body = builder.ToMessageBody();

                // ═══════════════════════════════════════════════════════════════
                // PHASE 6: SMTP Transmission - Connect and send via SMTPS
                // Uses connection pooling (reuses existing authenticated connection)
                // ═══════════════════════════════════════════════════════════════
                var connectStopwatch = Stopwatch.StartNew();
                await EnsureConnectedAsync(ct);
                evt.Timing.ConnectMs = connectStopwatch.ElapsedMilliseconds;

                // Serialize access to SMTP client (not thread-safe for concurrent sends)
                var sendStopwatch = Stopwatch.StartNew();
                await _sendLock.WaitAsync(ct);
                try
                {
                    await _smtpClient.SendAsync(message, ct);
                }
                finally
                {
                    _sendLock.Release();
                }
                evt.Timing.SendMs = sendStopwatch.ElapsedMilliseconds;
            });

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 7: Success - Return message ID and thread index for database storage
            // ═══════════════════════════════════════════════════════════════════════
            evt.RetryCount = retryCount;
            evt.Outcome = "success";
            return (messageId, threadIndex);
        }
        catch (Exception ex)
        {
            // All retries exhausted - log failure details for diagnostics
            evt.RetryCount = retryCount;
            evt.Outcome = "failed";
            evt.ErrorType = ex.GetType().Name;
            evt.ErrorMessage = ex.Message;
            return (null, null);
        }
        finally
        {
            evt.Timing.TotalMs = totalStopwatch.ElapsedMilliseconds;
            LogEmailSend(evt);
        }
    }

    /// <summary>
    /// Ensures the SMTP client is connected and authenticated, reusing existing connections.
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_smtpClient.IsConnected && _smtpClient.IsAuthenticated)
            return;

        if (_smtpClient.IsConnected)
        {
            await _smtpClient.DisconnectAsync(true, ct);
        }

        await _smtpClient.ConnectAsync(_smtpServer, _smtpPort, SecureSocketOptions.Auto, ct);
        await _smtpClient.AuthenticateAsync(SenderEmail, _senderPassword, ct);
    }

    /// <summary>
    /// Releases all resources used by the <see cref="EmailDeliveryService"/>.
    /// </summary>
    public void Dispose()
    {
        if (_smtpClient.IsConnected)
        {
            _smtpClient.Disconnect(true);
        }
        _smtpClient.Dispose();
        _sendLock.Dispose();
    }

    /// <summary>
    /// Emits a wide event for email send operations.
    /// </summary>
    private void LogEmailSend(EmailSendEvent evt)
    {
        var level = evt.Outcome == "success" ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Error;

        var log = _logger.ForContext("EmailSendEvent", evt, destructureObjects: true);

        if (evt.Outcome == "success")
        {
            log.Write(level, 
                "Email sent to {Recipient}: {Subject} (Ref: {MessageId}) [Timing: {TotalMs}ms]", 
                evt.Recipient, evt.Subject, evt.MessageId, evt.Timing.TotalMs);
        }
        else
        {
            log.Write(level, 
                "Email failed to {Recipient}: {Subject} - {ErrorType}: {ErrorMessage}", 
                evt.Recipient, evt.Subject, evt.ErrorType, evt.ErrorMessage);
        }
    }
}
