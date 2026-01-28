using System.Diagnostics;

using DatabaseAccess;

using EmailService.Constants;
using EmailService.Interfaces;
using EmailService.Models;
using EmailService.Utilities;

using RabbitMQHelper;
using RabbitMQHelper.MessageTypes;
using Serilog;

namespace EmailService.Services;

/// <summary>
/// Hosted service that consumes job notification messages from RabbitMQ and sends corresponding emails.
/// Uses wide event logging for comprehensive observability.
/// </summary>
/// <param name="logger">Logger for diagnostic output.</param>
/// <param name="rmq">RabbitMQ helper for message queue operations.</param>
/// <param name="scopeFactory">Factory for creating service scopes.</param>
/// <param name="renderer">Email template rendering service.</param>
/// <param name="sender">Email delivery service.</param>
public sealed class EmailNotificationQueueWorker(
    Serilog.ILogger logger,
    IRmqHelper rmq,
    IServiceScopeFactory scopeFactory,
    IEmailTemplateService renderer,
    IEmailDeliveryService sender) : IHostedService
{
    private readonly Serilog.ILogger logger = logger.ForContext<EmailNotificationQueueWorker>();
    /// <summary>
    /// Starts the worker by connecting to RabbitMQ and registering async message listeners.
    /// </summary>
    /// <param name="cancellationToken">Token for cancelling the startup operation.</param>
    /// <returns>A task representing the asynchronous startup operation.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // ═══════════════════════════════════════════════════════════════════════
        // STARTUP: Connect to RabbitMQ and register message listeners
        // Each listener handles a specific job lifecycle event
        // ═══════════════════════════════════════════════════════════════════════
        bool connected = await rmq.Connect();
        if (!connected)
            throw new ApplicationException("Failed to connect to RabbitMQ");

        // Job lifecycle event listeners - each triggers a templated email notification
        connected = rmq.AddListenerAsync<AcceptMessage>(QueueNames.EmailJobAccepted, OnJobReceived);
        if (!connected)
            throw new ApplicationException("Failed to add listener to EmailJobAccepted queue");

        connected = rmq.AddListenerAsync<PrintStartedMessage>(QueueNames.EmailPrintStarted, OnPrintStarted);
        if (!connected)
            throw new ApplicationException("Failed to add listener to EmailPrintStarted queue");

        connected = rmq.AddListenerAsync<PrintClearedMessage>(QueueNames.EmailPrintCleared, OnJobCompleted);
        if (!connected)
            throw new ApplicationException("Failed to add listener to EmailPrintCleared queue");

        connected = rmq.AddListenerAsync<Message>(QueueNames.EmailJobPaid, OnPaymentAccepted);
        if (!connected)
            throw new ApplicationException("Failed to add listener to EmailJobPaid queue");

        connected = rmq.AddListenerAsync<Message>(QueueNames.EmailJobApproved, OnJobApproved);
        if (!connected)
            throw new ApplicationException("Failed to add listener to EmailJobApproved queue");

        connected = rmq.AddListenerAsync<RejectMessage>(QueueNames.EmailJobRejected, OnJobRejected);
        if (!connected)
            throw new ApplicationException("Failed to add listener to EmailJobRejected queue");

        // Operator reply listener - handles manual responses from operators
        connected = rmq.AddListenerAsync<OperatorReplyMessage>(QueueNames.EmailOperatorReply, OnOperatorReply);
        if (!connected)
            throw new ApplicationException("Failed to add listener to EmailOperatorReply queue");

        logger.Information("EmailNotificationQueueWorker started with all listeners registered");
    }

    /// <summary>
    /// Stops the worker gracefully.
    /// </summary>
    /// <param name="cancellationToken">Token for cancelling the shutdown operation.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.Information("EmailNotificationQueueWorker shutting down");
        rmq.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the job received/accepted event.
    /// </summary>
    private Task<bool> OnJobReceived(AcceptMessage message) =>
        SendJobEmail(message.JobId, EmailTemplates.JobReceived, "Print Request Received", "AcceptMessage");

    /// <summary>
    /// Handles the job approved/verified event.
    /// </summary>
    private Task<bool> OnJobApproved(Message message) =>
        SendJobEmail(message.JobId, EmailTemplates.JobVerified, "Print Verified", "JobApproved");

    /// <summary>
    /// Handles the payment accepted event.
    /// </summary>
    private Task<bool> OnPaymentAccepted(Message message) =>
        SendJobEmail(message.JobId, EmailTemplates.PaymentAccepted, "Payment Accepted", "PaymentAccepted");

    /// <summary>
    /// Handles the print started event.
    /// </summary>
    private Task<bool> OnPrintStarted(Message message) =>
        SendJobEmail(message.JobId, EmailTemplates.JobPrinting, "Job Printing", "PrintStarted");

    /// <summary>
    /// Handles the job completed (print cleared) event.
    /// </summary>
    private Task<bool> OnJobCompleted(PrintClearedMessage message) =>
        SendJobEmail(message.JobId, EmailTemplates.JobCompleted, "Job Completed", "PrintCleared");

    /// <summary>
    /// Handles the job rejected event with the rejection reason.
    /// </summary>
    private Task<bool> OnJobRejected(RejectMessage message) =>
        SendJobEmail(
            message.JobId,
            EmailTemplates.JobRejected,
            "Job Rejected",
            "JobRejected",
            new Dictionary<string, string>
            {
                ["[REJECTION_REASON]"] = EmailDefaults.GetRejectReasonText(message.RejectReason)
            }
        );

    /// <summary>
    /// Handles operator reply messages, sending the reply as a threaded email.
    /// Emits a wide event with all context and timing.
    /// </summary>
    private async Task<bool> OnOperatorReply(OperatorReplyMessage message)
    {
        var evt = new OperatorReplyEvent
        {
            ThreadId = message.ThreadId,
            Recipient = message.CustomerEmail,
            Subject = message.Subject
        };
        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        try
        {
            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 1: Validation - Check message has content to send
            // ═══════════════════════════════════════════════════════════════════════
            var bodyHtml = TextFormatting.ConvertTextToHtmlParagraphs(message.Body);
            if (string.IsNullOrWhiteSpace(bodyHtml))
            {
                // Permanent failure - empty body won't change on retry, ACK to remove
                evt.Outcome = "empty_body";
                return true;
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseAccessHelper>();

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 2: Thread Lookup - Get previous messages for threading headers
            // Builds In-Reply-To, References, and Thread-Index for email clients
            // ═══════════════════════════════════════════════════════════════════════
            var threadMessages = await db.Message.GetMessagesByThreadAsync(message.ThreadId);
            evt.Timing.ThreadLookupMs = phaseStopwatch.ElapsedMilliseconds;

            // Find the most recent message with an Internet Message-ID for threading
            var lastMessageWithId = threadMessages
                .Where(m => !string.IsNullOrWhiteSpace(m.InternetMessageId))
                .MaxBy(m => m.CreatedAt);

            string? inReplyTo = null;
            string? references = null;
            string? parentThreadIndex = null;

            if (lastMessageWithId is not null)
            {
                // Set In-Reply-To to the most recent message
                inReplyTo = lastMessageWithId.InternetMessageId;
                parentThreadIndex = lastMessageWithId.ThreadIndex;

                // Build References header with all message IDs in the thread
                var referenceIds = threadMessages
                    .Where(m => !string.IsNullOrWhiteSpace(m.InternetMessageId))
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => m.InternetMessageId!)
                    .ToList();

                if (referenceIds.Count > 0)
                {
                    references = string.Join(" ", referenceIds);
                    evt.ReferencesCount = referenceIds.Count;
                }

                evt.HasThreading = true;
            }

            // Ensure subject starts with "Re:" for email client threading compatibility
            var subject = message.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                ? message.Subject
                : $"Re: {message.Subject}";

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 3: Template Rendering - Build HTML email from operator message
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var html = renderer.Render(EmailTemplates.OperatorReply, new Dictionary<string, string>
            {
                ["[SUBJECT]"] = message.Subject,
                ["[MESSAGE_BODY]"] = bodyHtml
            });
            evt.Timing.RenderMs = phaseStopwatch.ElapsedMilliseconds;

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 4: Send Email - Deliver operator reply via SMTP
            // Includes all threading headers for conversation continuity
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var (sentMessageId, sentThreadIndex) = await sender.SendAsync(
                message.CustomerEmail,
                subject,
                html,
                inReplyTo,
                references,
                parentThreadIndex
            );
            evt.Timing.SendMs = phaseStopwatch.ElapsedMilliseconds;

            if (sentMessageId is null)
            {
                evt.Outcome = "send_failed";
                return false;
            }

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 5: Persist - Store operator reply in database for thread history
            // Preserves Message-ID and Thread-Index for future threading
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var (result, _) = await db.Message.CreateEmailMessageAsync(
                threadId: message.ThreadId,
                fromEmailAddress: sender.SenderEmail,
                messageSubject: message.Subject,
                messageContent: message.Body,
                senderType: "operator",
                messageStatus: "acknowledged",
                internetMessageId: sentMessageId,
                threadIndex: sentThreadIndex,
                createdAtUtc: DateTime.UtcNow
            );
            evt.Timing.DbPersistMs = phaseStopwatch.ElapsedMilliseconds;

            evt.Persisted = result == TransactionResult.Succeeded;

            // Mark thread as active (resolved) since operator responded
            if (evt.Persisted)
            {
                await db.Thread.ActivateThreadAsync(message.ThreadId);
            }

            evt.Outcome = "success";
            return true;
        }
        catch (Exception ex)
        {
            evt.Outcome = "failed";
            evt.ErrorType = ex.GetType().Name;
            evt.ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            evt.Timing.TotalMs = totalStopwatch.ElapsedMilliseconds;
            LogOperatorReply(evt);
        }
    }

    /// <summary>
    /// Sends a job-related email notification to the job owner.
    /// Emits a wide event with all context and timing.
    /// </summary>
    private async Task<bool> SendJobEmail(
        long jobId,
        string templateName,
        string emailType,
        string messageType,
        Dictionary<string, string>? additionalReplacements = null)
    {
        var evt = new JobNotificationEvent
        {
            JobId = jobId,
            MessageType = messageType,
            EmailType = emailType,
            Template = templateName
        };
        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        try
        {
            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 1: Recipient Lookup - Find primary email for job owner
            // ═══════════════════════════════════════════════════════════════════════
            var to = await GetPrimaryEmailForJob(jobId);
            evt.Timing.DbLookupMs = phaseStopwatch.ElapsedMilliseconds;

            if (to is null)
            {
                // Permanent failure - no point retrying, ACK to remove from queue
                evt.Outcome = "no_recipient";
                return true;
            }

            evt.Recipient = to;

            // Build template replacements (job ID + any additional fields like rejection reason)
            var replacements = new Dictionary<string, string> { ["[JOB_ID]"] = jobId.ToString() };

            if (additionalReplacements is not null)
            {
                foreach (var (key, value) in additionalReplacements)
                {
                    replacements[key] = value;
                }
            }

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 2: Template Rendering - Generate HTML email from template
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var html = renderer.Render(templateName, replacements);
            evt.Timing.RenderMs = phaseStopwatch.ElapsedMilliseconds;

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 3: Send Email - Deliver notification via SMTP
            // Subject includes email type and job ID for easy identification
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var (messageId, _) = await sender.SendAsync(to, $"{emailType} - Job #{jobId}", html);
            evt.Timing.SendMs = phaseStopwatch.ElapsedMilliseconds;

            if (messageId is null)
            {
                evt.Outcome = "send_failed";
                return false;
            }

            evt.Outcome = "success";
            return true;
        }
        catch (Exception ex)
        {
            evt.Outcome = "failed";
            evt.ErrorType = ex.GetType().Name;
            evt.ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            evt.Timing.TotalMs = totalStopwatch.ElapsedMilliseconds;
            LogJobNotification(evt);
        }
    }

    /// <summary>
    /// Gets the primary email address for the owner of a print job.
    /// </summary>
    private async Task<string?> GetPrimaryEmailForJob(long jobId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseAccessHelper>();
        return await db.Emails.GetPrimaryEmailForJobAsync(jobId);
    }

    /// <summary>
    /// Emits a wide event for job notification processing.
    /// </summary>
    private void LogJobNotification(JobNotificationEvent evt)
    {
        var level = evt.Outcome == "success" ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Error;

        var log = logger.ForContext("JobNotificationEvent", evt, destructureObjects: true);

        if (evt.Outcome == "success")
        {
            log.Write(level, 
                "Job Notification {Outcome}: {MessageType} for Job #{JobId} to {Recipient} [Timing: {TotalMs}ms]", 
                evt.Outcome, evt.MessageType, evt.JobId, evt.Recipient, evt.Timing.TotalMs);
        }
        else
        {
            log.Write(level, 
                "Job Notification {Outcome}: {MessageType} for Job #{JobId} - {ErrorType}: {ErrorMessage}", 
                evt.Outcome, evt.MessageType, evt.JobId, evt.ErrorType, evt.ErrorMessage);
        }
    }

    /// <summary>
    /// Emits a wide event for operator reply processing.
    /// </summary>
    private void LogOperatorReply(OperatorReplyEvent evt)
    {
        var level = evt.Outcome == "success" ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Error;

        var log = logger.ForContext("OperatorReplyEvent", evt, destructureObjects: true);

        if (evt.Outcome == "success")
        {
            log.Write(level, 
                "Operator Reply {Outcome} to {Recipient}: {Subject} [Timing: {TotalMs}ms]", 
                evt.Outcome, evt.Recipient, evt.Subject, evt.Timing.TotalMs);
        }
        else
        {
            log.Write(level, 
                "Operator Reply {Outcome} to {Recipient}: {Subject} - {ErrorType}: {ErrorMessage}", 
                evt.Outcome, evt.Recipient, evt.Subject, evt.ErrorType, evt.ErrorMessage);
        }
    }
}
