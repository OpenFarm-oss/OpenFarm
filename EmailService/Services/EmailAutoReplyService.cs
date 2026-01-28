using System.Diagnostics;
using DatabaseAccess;
using DatabaseAccess.Models;
using EmailService.Constants;
using EmailService.Interfaces;
using EmailService.Models;

namespace EmailService.Services;

/// <summary>
/// Evaluates saved auto-reply rules and sends an email when one matches the current date/time.
/// Uses wide event logging for comprehensive observability.
/// </summary>
/// <remarks>
/// <para>
/// This service checks configured auto-reply rules against the current local time and sends
/// automatic responses when a rule matches. Rules can be configured for:
/// </para>
/// <list type="bullet">
/// <item><description>Out-of-office messages (always active within date range)</description></item>
/// <item><description>Scheduled responses (specific days and time windows)</description></item>
/// </list>
/// <para>
/// When multiple rules match, the one with the lowest numeric priority value is selected.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Factory for creating service scopes to access scoped services.</param>
/// <param name="emailSender">Service for sending emails.</param>
/// <param name="templateService">Service for rendering email templates.</param>
/// <param name="configuration">Application configuration containing email settings.</param>
/// <param name="logger">Logger for diagnostic output.</param>
public sealed class EmailAutoReplyService(
    IServiceScopeFactory scopeFactory,
    IEmailDeliveryService emailSender,
    IEmailTemplateService templateService,
    IConfiguration configuration,
    ILogger<EmailAutoReplyService> logger) : IEmailAutoReplyService
{
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.Local;
    private readonly string _senderEmail = configuration["GMAIL_EMAIL"]
        ?? throw new InvalidOperationException("GMAIL_EMAIL configuration is required");

    /// <inheritdoc />
    public async Task SendAutoReplyIfNeededAsync(
        string toAddress,
        DatabaseAccess.Models.Thread thread,
        string originalSubject,
        string? inReplyTo = null,
        CancellationToken ct = default)
    {
        // ═══════════════════════════════════════════════════════════════════════
        // PHASE 1: Initialization - Set up event tracking and timestamps
        // ═══════════════════════════════════════════════════════════════════════
        var evt = new AutoReplyEvent
        {
            Recipient = toAddress,
            ThreadId = thread.Id
        };
        var totalStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        try
        {
            var nowUtc = DateTimeOffset.UtcNow;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseAccessHelper>();

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 2: Rule Evaluation - Find matching auto-reply rule
            // Checks date range, day of week, and time window against local time
            // Selects highest priority rule (lowest Priority value wins)
            // ═══════════════════════════════════════════════════════════════════════
            var allRules = await db.EmailAutoReplyRules.GetAllAsync();
            var enabledRules = allRules.Where(r => r.Isenabled);
            evt.RulesEvaluatedCount = enabledRules.Count();

            // Select the highest-priority matching rule.
            // Tie-breaker: lowest Priority value wins, then lowest rule ID for determinism.
            var matchingRule = enabledRules
                .Where(r => RuleMatches(r, nowUtc, _timeZone))
                .MinBy(r => (r.Priority, r.Emailautoreplyruleid));

            evt.Timing.RuleEvalMs = phaseStopwatch.ElapsedMilliseconds;

            if (matchingRule is null)
            {
                evt.RuleMatched = false;
                evt.Outcome = "no_match";
                return;
            }

            evt.RuleMatched = true;
            evt.MatchedRuleId = matchingRule.Emailautoreplyruleid;

            // Skip if rule has no content to send
            if (string.IsNullOrWhiteSpace(matchingRule.Body))
            {
                evt.Outcome = "skipped_empty_content";
                return;
            }

            // Format subject as reply to original for proper email threading
            var replySubject = originalSubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                ? originalSubject
                : $"Re: {originalSubject}";

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 3: Template Rendering - Build HTML email from rule content
            // Uses the AutoReply template with subject and body placeholders
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var replacements = new Dictionary<string, string>
            {
                ["[SUBJECT]"] = replySubject,
                ["[MESSAGE_BODY]"] = matchingRule.Body
            };

            string htmlBody;
            try
            {
                htmlBody = templateService.Render(EmailTemplates.AutoReply, replacements);
            }
            catch (Exception ex)
            {
                evt.Outcome = "render_failed";
                evt.ErrorType = ex.GetType().Name;
                evt.ErrorMessage = ex.Message;
                return;
            }
            evt.Timing.RenderMs = phaseStopwatch.ElapsedMilliseconds;

            // Get threading headers for proper email conversation threading
            var threadMessages = await db.Message.GetMessagesByThreadAsync(thread.Id);
            var parentThreadIndex = threadMessages
                .Where(m => !string.IsNullOrWhiteSpace(m.ThreadIndex))
                .MaxBy(m => m.CreatedAt)?.ThreadIndex;

            // Build References header from all message IDs in thread
            var referenceIds = threadMessages
                .Where(m => !string.IsNullOrWhiteSpace(m.InternetMessageId))
                .OrderBy(m => m.CreatedAt)
                .Select(m => m.InternetMessageId!)
                .ToList();
            string? references = referenceIds.Count > 0 ? string.Join(" ", referenceIds) : null;

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 4: Send Email - Deliver auto-reply via SMTP
            // Includes threading headers for conversation continuity
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            var (sentMessageId, sentThreadIndex) = await emailSender.SendAsync(
                to: toAddress,
                subject: replySubject,
                htmlBody: htmlBody,
                inReplyTo: inReplyTo,
                references: references,
                parentThreadIndex: parentThreadIndex,
                ct: ct);
            evt.Timing.SendMs = phaseStopwatch.ElapsedMilliseconds;

            if (sentMessageId is null)
            {
                evt.Outcome = "send_failed";
                return;
            }

            // ═══════════════════════════════════════════════════════════════════════
            // PHASE 5: Persist - Store auto-reply in database for thread history
            // Allows operators to see what was sent automatically
            // ═══════════════════════════════════════════════════════════════════════
            phaseStopwatch.Restart();
            evt.Persisted = await PersistAutoReplyToDatabase(
                db, thread.Id, replySubject, matchingRule.Body, sentMessageId, sentThreadIndex);
            evt.Timing.PersistMs = phaseStopwatch.ElapsedMilliseconds;

            // Ensure thread stays unresolved after system auto-reply
            if (evt.Persisted)
            {
                await db.Thread.MarkThreadUnresolvedAsync(thread.Id);
            }

            evt.Outcome = "sent";
        }
        catch (Exception ex)
        {
            evt.Outcome = "failed";
            evt.ErrorType = ex.GetType().Name;
            evt.ErrorMessage = ex.Message;
        }
        finally
        {
            evt.Timing.TotalMs = totalStopwatch.ElapsedMilliseconds;
            LogAutoReply(evt);
        }
    }

    /// <summary>
    /// Persists the sent auto-reply message to the database for thread history.
    /// </summary>
    /// <returns>True if persistence succeeded, false otherwise.</returns>
    private async Task<bool> PersistAutoReplyToDatabase(
        DatabaseAccessHelper db,
        long threadId,
        string subject,
        string body,
        string messageId,
        string? threadIndex)
    {
        try
        {
            var (result, _) = await db.Message.CreateEmailMessageAsync(
                threadId: threadId,
                messageContent: body,
                messageSubject: subject,
                fromEmailAddress: _senderEmail,
                senderType: "system",
                messageStatus: "sent",
                internetMessageId: messageId,
                threadIndex: threadIndex,
                createdAtUtc: DateTime.UtcNow);

            return result == TransactionResult.Succeeded;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Emits a wide event for auto-reply processing.
    /// </summary>
    private void LogAutoReply(AutoReplyEvent evt)
    {
        // Only log at Info level if a reply was sent, otherwise Debug
        var level = evt.Outcome switch
        {
            "sent" => LogLevel.Information,
            "failed" or "send_failed" or "render_failed" => LogLevel.Error,
            _ => LogLevel.Debug
        };

        logger.Log(level,
            "{@AutoReplyEvent}",
            new
            {
                evt.Operation,
                evt.Recipient,
                evt.ThreadId,
                evt.RulesEvaluatedCount,
                evt.RuleMatched,
                evt.MatchedRuleId,
                evt.Outcome,
                evt.Persisted,
                evt.ErrorType,
                evt.ErrorMessage,
                Timing = new
                {
                    evt.Timing.RuleEvalMs,
                    evt.Timing.RenderMs,
                    evt.Timing.SendMs,
                    evt.Timing.PersistMs,
                    evt.Timing.TotalMs
                }
            });
    }

    /// <summary>
    /// Determines whether an auto-reply rule matches the current date and time.
    /// </summary>
    private static bool RuleMatches(Emailautoreplyrule rule, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var today = DateOnly.FromDateTime(localNow.Date);
        var localTime = TimeOnly.FromTimeSpan(localNow.TimeOfDay);

        // Check date range
        if (rule.Startdate.HasValue && today < rule.Startdate.Value)
            return false;

        if (rule.Enddate.HasValue && today > rule.Enddate.Value)
            return false;

        if (rule.RuleTypeEnum == Emailautoreplyrule.EmailRuleType.OutOfOffice)
            return true;

        var todayFlag = DayOfWeekToFlag(localNow.DayOfWeek);

        if ((rule.DaysOfWeekFlags & todayFlag) == Emailautoreplyrule.DayOfWeekFlags.None)
            return false;

        if (rule.Starttime is null && rule.Endtime is null)
            return true;

        if (rule.Starttime is null || rule.Endtime is null)
            return false;

        var start = rule.Starttime.Value;
        var end = rule.Endtime.Value;

        return start <= end
            ? localTime >= start && localTime <= end
            : localTime >= start || localTime <= end;
    }

    /// <summary>
    /// Converts a DayOfWeek to the corresponding flag.
    /// </summary>
    private static Emailautoreplyrule.DayOfWeekFlags DayOfWeekToFlag(DayOfWeek day) =>
        day switch
        {
            DayOfWeek.Sunday => Emailautoreplyrule.DayOfWeekFlags.Sunday,
            DayOfWeek.Monday => Emailautoreplyrule.DayOfWeekFlags.Monday,
            DayOfWeek.Tuesday => Emailautoreplyrule.DayOfWeekFlags.Tuesday,
            DayOfWeek.Wednesday => Emailautoreplyrule.DayOfWeekFlags.Wednesday,
            DayOfWeek.Thursday => Emailautoreplyrule.DayOfWeekFlags.Thursday,
            DayOfWeek.Friday => Emailautoreplyrule.DayOfWeekFlags.Friday,
            DayOfWeek.Saturday => Emailautoreplyrule.DayOfWeekFlags.Saturday,
            _ => Emailautoreplyrule.DayOfWeekFlags.None
        };
}
