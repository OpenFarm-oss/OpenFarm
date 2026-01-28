using System.Linq;
using DatabaseAccess.Models;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAccess.Helpers;

/// <summary>
/// Provides helper methods for accessing and modifying <see cref="EmailMessage"/> entities in the database.
/// </summary>
/// <param name="context">The database context to use for operations.</param>
public class MessageHelper(OpenFarmContext context) : BaseHelper(context)
{
    private const string StatusUnseen = "unseen";
    private const string StatusSeen = "seen";
    private const string StatusAcknowledged = "acknowledged";

    private const string SenderUser = "user";
    private const string SenderSystem = "system";
    private const string SenderOperator = "operator";

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        StatusUnseen,
        StatusSeen,
        StatusAcknowledged
    };

    private static readonly HashSet<string> AllowedSenderTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        SenderUser,
        SenderSystem,
        SenderOperator
    };

    private IQueryable<EmailMessage> EmailMessagesAsNoTracking => _context.EmailMessages.AsNoTracking();

    private IQueryable<EmailMessage> OrderedEmailMessages => EmailMessagesAsNoTracking
        .OrderBy(message => message.CreatedAt);

    /// <summary>
    /// Retrieves all messages for a specific thread ordered by creation time.
    /// </summary>
    /// <param name="threadId">The thread identifier to filter by.</param>
    /// <returns>A task that resolves to a list of messages in the specified thread.</returns>
    public async Task<List<EmailMessage>> GetMessagesByThreadAsync(long threadId) =>
        await OrderedEmailMessages
            .Where(message => message.ThreadId == threadId)
            .ToListAsync();

    /// <summary>
    /// Retrieves the message with the specified identifier.
    /// </summary>
    /// <param name="messageId">The identifier of the message to retrieve.</param>
    /// <returns>A task that resolves to the message, or null if not found.</returns>
    public async Task<EmailMessage?> GetMessageAsync(long messageId) =>
        await EmailMessagesAsNoTracking
            .Include(m => m.Thread)
            .FirstOrDefaultAsync(message => message.Id == messageId);

    /// <summary>
    /// Retrieves messages filtered by status.
    /// </summary>
    /// <param name="messageStatus">The message status to filter by.</param>
    /// <returns>A task that resolves to a list of messages with the specified status.</returns>
    public async Task<List<EmailMessage>> GetMessagesByStatusAsync(string messageStatus)
    {
        var normalizedStatus = NormalizeStatus(messageStatus);

        if (normalizedStatus == null)
            return [];

        return await OrderedEmailMessages
            .Where(message => message.MessageStatus == normalizedStatus)
            .ToListAsync();
    }

    /// <summary>
    /// Retrieves messages from a specific sender email address.
    /// </summary>
    /// <param name="fromEmailAddress">The sender email address to filter by.</param>
    /// <returns>A task that resolves to a list of email messages from the specified sender.</returns>
    public async Task<List<EmailMessage>> GetEmailMessagesFromAsync(string fromEmailAddress)
    {
        if (string.IsNullOrWhiteSpace(fromEmailAddress))
            return [];

        var sanitizedAddress = TrimEmailAddress(fromEmailAddress);

        return await OrderedEmailMessages
            .Where(message => message.FromEmailAddress == sanitizedAddress)
            .ToListAsync();
    }

    /// <summary>
    /// Retrieves all unseen messages.
    /// </summary>
    /// <returns>A task that resolves to a list of all unseen messages.</returns>
    public Task<List<EmailMessage>> GetUnseenMessagesAsync() =>
        GetMessagesByStatusAsync(StatusUnseen);

    /// <summary>
    /// Creates a new email message in a thread.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="fromEmailAddress">The sender email address.</param>
    /// <param name="messageSubject">The message subject.</param>
    /// <param name="messageContent">The message content.</param>
    /// <param name="senderType">The sender type (user, system, or operator).</param>
    /// <param name="messageStatus">The message status.</param>
    /// <param name="internetMessageId">The Internet Message-ID for email threading.</param>
    /// <param name="threadIndex">The Outlook Thread-Index header for conversation threading.</param>
    /// <param name="createdAtUtc">The UTC date/time when the message was created.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public async Task<(TransactionResult Result, EmailMessage? Message)> CreateEmailMessageAsync(
        long threadId,
        string fromEmailAddress,
        string messageSubject,
        string messageContent,
        string? senderType = null,
        string? messageStatus = null,
        string? internetMessageId = null,
        string? threadIndex = null,
        DateTime? createdAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(fromEmailAddress) || string.IsNullOrWhiteSpace(messageContent))
            return (TransactionResult.Failed, null);

        try
        {
            // Validate thread exists
            var threadExists = await _context.Threads
                .AsNoTracking()
                .AnyAsync(thread => thread.Id == threadId);

            if (!threadExists)
                return (TransactionResult.Failed, null);

            var sanitizedAddress = TrimEmailAddress(fromEmailAddress);
            var sanitizedContent = messageContent.Trim();
            var sanitizedSubject = string.IsNullOrWhiteSpace(messageSubject)
                ? "(No subject)"
                : messageSubject.Trim();

            if (sanitizedContent.Length == 0)
                return (TransactionResult.Failed, null);

            var normalizedStatus = NormalizeStatus(messageStatus) ?? StatusUnseen;
            var normalizedSenderType = NormalizeSenderType(senderType) ?? SenderUser;

            var message = new EmailMessage
            {
                ThreadId = threadId,
                MessageContent = sanitizedContent,
                MessageSubject = sanitizedSubject,
                SenderType = normalizedSenderType,
                FromEmailAddress = sanitizedAddress,
                InternetMessageId = string.IsNullOrWhiteSpace(internetMessageId) ? null : internetMessageId.Trim(),
                ThreadIndex = string.IsNullOrWhiteSpace(threadIndex) ? null : threadIndex.Trim(),
                MessageStatus = normalizedStatus,
                CreatedAt = (createdAtUtc ?? DateTime.UtcNow).ToUniversalTime()
            };

            await _context.EmailMessages.AddAsync(message);

            // Update thread's updated_at timestamp
            await UpdateThreadTimestampAsync(threadId);

            await _context.SaveChangesAsync();

            return (TransactionResult.Succeeded, message);
        }
        catch
        {
            _context.ChangeTracker.Clear();
            return (TransactionResult.Failed, null);
        }
    }

    /// <summary>
    /// Updates the status for a message.
    /// </summary>
    /// <param name="messageId">The identifier of the message to update.</param>
    /// <param name="messageStatus">The new message status.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public async Task<TransactionResult> UpdateMessageStatusAsync(long messageId, string messageStatus)
    {
        var normalizedStatus = NormalizeStatus(messageStatus);

        if (normalizedStatus == null)
            return TransactionResult.Failed;

        try
        {
            var message = await _context.EmailMessages
                .FirstOrDefaultAsync(m => m.Id == messageId);

            if (message == null)
                return TransactionResult.NotFound;

            if (string.Equals(message.MessageStatus, normalizedStatus, StringComparison.OrdinalIgnoreCase))
                return TransactionResult.NoAction;

            message.MessageStatus = normalizedStatus;
            await _context.SaveChangesAsync();
            return TransactionResult.Succeeded;
        }
        catch
        {
            _context.ChangeTracker.Clear();
            return TransactionResult.Failed;
        }
    }

    /// <summary>
    /// Marks the message as seen.
    /// </summary>
    /// <param name="messageId">The identifier of the message to mark as seen.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public Task<TransactionResult> MarkMessageSeenAsync(long messageId) =>
        UpdateMessageStatusAsync(messageId, StatusSeen);

    /// <summary>
    /// Marks the message as acknowledged.
    /// </summary>
    /// <param name="messageId">The identifier of the message to mark as acknowledged.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public Task<TransactionResult> MarkMessageAcknowledgedAsync(long messageId) =>
        UpdateMessageStatusAsync(messageId, StatusAcknowledged);

    /// <summary>
    /// Marks the message as unseen.
    /// </summary>
    /// <param name="messageId">The identifier of the message to mark as unseen.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public Task<TransactionResult> MarkMessageUnseenAsync(long messageId) =>
        UpdateMessageStatusAsync(messageId, StatusUnseen);

    /// <summary>
    /// Marks all messages in a thread as acknowledged.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public async Task<TransactionResult> MarkAllThreadMessagesAcknowledgedAsync(long threadId)
    {
        try
        {
            var messages = await _context.EmailMessages
                .Where(m => m.ThreadId == threadId && m.MessageStatus != StatusAcknowledged)
                .ToListAsync();

            if (messages.Count == 0)
                return TransactionResult.NoAction;

            foreach (var message in messages)
            {
                message.MessageStatus = StatusAcknowledged;
            }

            await _context.SaveChangesAsync();
            return TransactionResult.Succeeded;
        }
        catch
        {
            _context.ChangeTracker.Clear();
            return TransactionResult.Failed;
        }
    }

    /// <summary>
    /// Marks all unseen messages in a thread as seen.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public async Task<TransactionResult> MarkUnseenMessagesSeenAsync(long threadId)
    {
        try
        {
            var messages = await _context.EmailMessages
                .Where(m => m.ThreadId == threadId && m.MessageStatus == StatusUnseen)
                .ToListAsync();

            if (messages.Count == 0)
                return TransactionResult.NoAction;

            foreach (var message in messages)
            {
                message.MessageStatus = StatusSeen;
            }

            await _context.SaveChangesAsync();
            return TransactionResult.Succeeded;
        }
        catch
        {
            _context.ChangeTracker.Clear();
            return TransactionResult.Failed;
        }
    }

    /// <summary>
    /// Deletes the specified message.
    /// </summary>
    /// <param name="messageId">The identifier of the message to delete.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public async Task<TransactionResult> DeleteMessageAsync(long messageId)
    {
        if (messageId <= 0)
            return TransactionResult.Failed;

        try
        {
            var message = await _context.EmailMessages
                .FirstOrDefaultAsync(m => m.Id == messageId);

            if (message == null)
                return TransactionResult.NotFound;

            _context.EmailMessages.Remove(message);
            await _context.SaveChangesAsync();
            return TransactionResult.Succeeded;
        }
        catch
        {
            _context.ChangeTracker.Clear();
            return TransactionResult.Failed;
        }
    }

    private async Task UpdateThreadTimestampAsync(long threadId)
    {
        var thread = await _context.Threads.FirstOrDefaultAsync(t => t.Id == threadId);
        if (thread != null)
        {
            thread.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static string? NormalizeStatus(string? messageStatus)
    {
        if (string.IsNullOrWhiteSpace(messageStatus))
            return null;

        var normalized = messageStatus.Trim().ToLowerInvariant();
        return AllowedStatuses.Contains(normalized) ? normalized : null;
    }

    private static string? NormalizeSenderType(string? senderType)
    {
        if (string.IsNullOrWhiteSpace(senderType))
            return null;

        var normalized = senderType.Trim().ToLowerInvariant();
        return AllowedSenderTypes.Contains(normalized) ? normalized : null;
    }

    private static string TrimEmailAddress(string emailAddress) =>
        emailAddress.Trim();
}
