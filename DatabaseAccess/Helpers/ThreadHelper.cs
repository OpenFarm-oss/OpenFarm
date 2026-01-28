using System.Linq;
using DatabaseAccess.Models;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAccess.Helpers;

/// <summary>
/// Provides helper methods for accessing and modifying <see cref="Thread"/> entities in the database.
/// </summary>
/// <param name="context">The database context to use for operations.</param>
public class ThreadHelper(OpenFarmContext context) : BaseHelper(context)
{
    private const string StatusActive = "active";
    private const string StatusUnresolved = "unresolved";
    private const string StatusArchived = "archived";

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        StatusActive,
        StatusUnresolved,
        StatusArchived
    };

    private IQueryable<Models.Thread> ThreadsAsNoTracking => _context.Threads.AsNoTracking();

    private IQueryable<Models.Thread> OrderedThreads => ThreadsAsNoTracking
        .OrderByDescending(thread => thread.UpdatedAt);

    /// <summary>
    /// Retrieves all threads ordered by most recently updated first.
    /// </summary>
    /// <returns>A task that resolves to a list of all threads ordered by most recently updated first.</returns>
    public async Task<List<Models.Thread>> GetAllThreadsAsync() =>
        await OrderedThreads.ToListAsync();

    /// <summary>
    /// Retrieves the thread with the specified identifier.
    /// </summary>
    /// <param name="threadId">The identifier of the thread to retrieve.</param>
    /// <returns>A task that resolves to the thread, or null if not found.</returns>
    /// <param name="threadId">The unique identifier of the thread.</param>
    /// <returns>A task that resolves to the thread with the specified ID, or null if not found.</returns>
    public async Task<Models.Thread?> GetThreadByIdAsync(long threadId) =>
        await ThreadsAsNoTracking
            .FirstOrDefaultAsync(thread => thread.Id == threadId);

    /// <summary>
    /// Retrieves threads associated with a specific user, ordered by most recently updated first.
    /// </summary>
    /// <param name="userId">The user identifier to filter by.</param>
    /// <returns>A task that resolves to a list of threads for the specified user.</returns>
    public async Task<List<Models.Thread>> GetThreadsByUserIdAsync(long userId) =>
        await OrderedThreads
            .Where(thread => thread.UserId == userId)
            .ToListAsync();

    /// <summary>
    /// Retrieves threads with a specific status, ordered by most recently updated first.
    /// </summary>
    /// <param name="status">The thread status to filter by.</param>
    /// <returns>A task that resolves to a list of threads with the specified status.</returns>
    public async Task<List<Models.Thread>> GetThreadsByStatusAsync(string status)
    {
        var normalizedStatus = NormalizeStatus(status);

        if (normalizedStatus == null)
            return [];

        return await OrderedThreads
            .Where(thread => thread.ThreadStatus == normalizedStatus)
            .ToListAsync();
    }

    /// <summary>
    /// Retrieves threads associated with a specific job ID.
    /// </summary>
    /// <param name="jobId">The job identifier to filter by.</param>
    /// <returns>A task that resolves to a list of threads associated with the specified job ID.</returns>
    public async Task<List<Models.Thread>> GetThreadsByJobIdAsync(long jobId) =>
        await OrderedThreads
            .Where(thread => thread.JobId == jobId)
            .ToListAsync();

    /// <summary>
    /// Retrieves threads that are not associated with any job.
    /// </summary>
    /// <returns>A task that resolves to a list of threads that are not associated with any job.</returns>
    public async Task<List<Models.Thread>> GetUnassociatedThreadsAsync() =>
        await OrderedThreads
            .Where(thread => thread.JobId == null)
            .ToListAsync();

    /// <summary>
    /// Retrieves all active threads.
    /// </summary>
    /// <returns>A task that resolves to a list of all active threads.</returns>
    public Task<List<Models.Thread>> GetActiveThreadsAsync() =>
        GetThreadsByStatusAsync(StatusActive);

    /// <summary>
    /// Creates a new thread for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="jobId">The optional associated job identifier.</param>
    /// <param name="threadStatus">The thread status.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public async Task<(TransactionResult Result, Models.Thread? Thread)> CreateThreadAsync(
        long userId,
        long? jobId = null,
        string? threadStatus = null)
    {
        try
        {
            // Validate user exists
            var userExists = await _context.Users
                .AsNoTracking()
                .AnyAsync(user => user.Id == userId);

            if (!userExists)
                return (TransactionResult.Failed, null);

            // Validate job ID exists if provided
            long? validatedJobId = null;
            if (jobId.HasValue)
            {
                var jobExists = await _context.PrintJobs
                    .AsNoTracking()
                    .AnyAsync(job => job.Id == jobId.Value);

                if (jobExists)
                {
                    validatedJobId = jobId.Value;
                }
            }

            var normalizedStatus = NormalizeStatus(threadStatus) ?? StatusActive;
            var now = DateTime.UtcNow;

            var thread = new Models.Thread
            {
                UserId = userId,
                JobId = validatedJobId,
                ThreadStatus = normalizedStatus,
                CreatedAt = now,
                UpdatedAt = now
            };

            await _context.Threads.AddAsync(thread);
            await _context.SaveChangesAsync();

            return (TransactionResult.Succeeded, thread);
        }
        catch
        {
            _context.ChangeTracker.Clear();
            return (TransactionResult.Failed, null);
        }
    }

    /// <summary>
    /// Updates the status for a thread.
    /// </summary>
    /// <param name="threadId">The identifier of the thread to update.</param>
    /// <param name="threadStatus">The new thread status.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public async Task<TransactionResult> UpdateThreadStatusAsync(long threadId, string threadStatus)
    {
        var normalizedStatus = NormalizeStatus(threadStatus);

        if (normalizedStatus == null)
            return TransactionResult.Failed;

        try
        {
            var thread = await _context.Threads
                .FirstOrDefaultAsync(t => t.Id == threadId);

            if (thread == null)
                return TransactionResult.NotFound;

            if (string.Equals(thread.ThreadStatus, normalizedStatus, StringComparison.OrdinalIgnoreCase))
                return TransactionResult.NoAction;

            thread.ThreadStatus = normalizedStatus;
            thread.UpdatedAt = DateTime.UtcNow;
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
    /// Associates a thread with a job.
    /// </summary>
    /// <param name="threadId">The identifier of the thread to update.</param>
    /// <param name="jobId">The job identifier to associate with the thread.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public async Task<TransactionResult> AssociateThreadWithJobAsync(long threadId, long jobId)
    {
        try
        {
            var thread = await _context.Threads
                .FirstOrDefaultAsync(t => t.Id == threadId);

            if (thread == null)
                return TransactionResult.NotFound;

            // Validate job exists
            var jobExists = await _context.PrintJobs
                .AsNoTracking()
                .AnyAsync(job => job.Id == jobId);

            if (!jobExists)
                return TransactionResult.Failed;

            if (thread.JobId == jobId)
                return TransactionResult.NoAction;

            thread.JobId = jobId;
            thread.UpdatedAt = DateTime.UtcNow;
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
    /// Updates the thread's last updated timestamp.
    /// </summary>
    /// <param name="threadId">The identifier of the thread to update.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public async Task<TransactionResult> TouchThreadAsync(long threadId)
    {
        try
        {
            var thread = await _context.Threads
                .FirstOrDefaultAsync(t => t.Id == threadId);

            if (thread == null)
                return TransactionResult.NotFound;

            thread.UpdatedAt = DateTime.UtcNow;
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
    /// Marks the thread as active (resolved - operator has responded).
    /// </summary>
    /// <param name="threadId">The identifier of the thread to mark as active.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public Task<TransactionResult> ActivateThreadAsync(long threadId) =>
        UpdateThreadStatusAsync(threadId, StatusActive);

    /// <summary>
    /// Marks the thread as unresolved (needs operator attention).
    /// </summary>
    /// <param name="threadId">The identifier of the thread to mark as unresolved.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public Task<TransactionResult> MarkThreadUnresolvedAsync(long threadId) =>
        UpdateThreadStatusAsync(threadId, StatusUnresolved);

    /// <summary>
    /// Marks the thread as archived.
    /// </summary>
    /// <param name="threadId">The identifier of the thread to mark as archived.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public Task<TransactionResult> ArchiveThreadAsync(long threadId) =>
        UpdateThreadStatusAsync(threadId, StatusArchived);

    /// <summary>
    /// Deletes archived threads older than the specified number of days.
    /// </summary>
    /// <param name="daysOld">Threads archived longer than this will be deleted (default: 30).</param>
    /// <returns>Number of threads deleted.</returns>
    public async Task<int> DeleteOldArchivedThreadsAsync(int daysOld = 30)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);
            var oldThreads = await _context.Threads
                .Where(t => t.ThreadStatus == StatusArchived && t.UpdatedAt < cutoffDate)
                .ToListAsync();

            if (oldThreads.Count == 0)
                return 0;

            _context.Threads.RemoveRange(oldThreads);
            await _context.SaveChangesAsync();
            return oldThreads.Count;
        }
        catch
        {
            _context.ChangeTracker.Clear();
            return 0;
        }
    }

    /// <summary>
    /// Deletes the specified thread and all its messages.
    /// </summary>
    /// <param name="threadId">The identifier of the thread to delete.</param>
    /// <returns>A task that resolves to a TransactionResult indicating the outcome of the operation.</returns>
    public async Task<TransactionResult> DeleteThreadAsync(long threadId)
    {
        if (threadId <= 0)
            return TransactionResult.Failed;

        try
        {
            var thread = await _context.Threads
                .Include(t => t.EmailMessages)
                .FirstOrDefaultAsync(t => t.Id == threadId);

            if (thread == null)
                return TransactionResult.NotFound;

            _context.Threads.Remove(thread);
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
    /// Finds the most recently updated thread for a user and job ID.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="jobId">The job ID to search for.</param>
    /// <returns>The matching thread, or <c>null</c> if not found.</returns>
    public async Task<Models.Thread?> GetThreadByUserAndJobAsync(long userId, long jobId) =>
        await OrderedThreads
            .Where(t => t.UserId == userId && t.JobId == jobId)
            .FirstOrDefaultAsync();

    /// <summary>
    /// Finds a thread by matching the normalized subject against existing thread messages.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="normalizedSubject">The lowercase normalized subject to match.</param>
    /// <returns>The matching thread, or <c>null</c> if not found.</returns>
    public async Task<Models.Thread?> FindThreadBySubjectAsync(long userId, string normalizedSubject)
    {
        if (string.IsNullOrWhiteSpace(normalizedSubject))
            return null;

        var threads = await GetThreadsByUserIdAsync(userId);

        foreach (var thread in threads)
        {
            var hasMatchingSubject = await _context.EmailMessages
                .AsNoTracking()
                .Where(m => m.ThreadId == thread.Id && m.MessageSubject != null)
                .AnyAsync(m => m.MessageSubject!.ToLower() == normalizedSubject);

            if (hasMatchingSubject)
                return thread;
        }

        return null;
    }

    /// <summary>
    /// Finds an existing conversation thread or creates a new one for email processing.
    /// </summary>
    /// <param name="email">The sender's email address.</param>
    /// <param name="jobId">Optional job ID extracted from the subject.</param>
    /// <param name="normalizedSubject">The lowercase normalized subject for matching.</param>
    /// <param name="isReply">Whether the email appears to be a reply.</param>
    /// <returns>A tuple containing the result and the thread (if successful).</returns>
    /// <remarks>
    /// Thread matching priority:
    /// <list type="number">
    /// <item><description>Match by job ID if present in subject</description></item>
    /// <item><description>Match by normalized subject if this is a reply</description></item>
    /// <item><description>Create a new thread if no match found</description></item>
    /// </list>
    /// </remarks>
    public async Task<(TransactionResult Result, Models.Thread? Thread)> FindOrCreateThreadForEmailAsync(
        string email,
        long? jobId,
        string normalizedSubject,
        bool isReply)
    {
        try
        {
            // Get or create user
            var user = await GetOrCreateUserByEmailAsync(email);
            if (user is null)
                return (TransactionResult.Failed, null);

            Models.Thread? thread = null;

            // Try to find existing thread by job ID
            if (jobId.HasValue)
            {
                thread = await GetThreadByUserAndJobAsync(user.Id, jobId.Value);
            }
            // Try to find existing thread by subject if this is a reply
            else if (isReply && !string.IsNullOrWhiteSpace(normalizedSubject))
            {
                thread = await FindThreadBySubjectAsync(user.Id, normalizedSubject);
            }

            // If found, ensure it's active
            if (thread is not null)
            {
                if (thread.ThreadStatus != StatusActive)
                {
                    await UpdateThreadStatusAsync(thread.Id, StatusActive);
                    thread.ThreadStatus = StatusActive;
                }
                return (TransactionResult.Succeeded, thread);
            }

            // Create a new thread
            return await CreateThreadAsync(user.Id, jobId, StatusActive);
        }
        catch
        {
            _context.ChangeTracker.Clear();
            return (TransactionResult.Failed, null);
        }
    }

    /// <summary>
    /// Gets an existing user by email or creates a new user if not found.
    /// </summary>
    /// <param name="email">The email address to look up or create.</param>
    /// <returns>The user, or <c>null</c> if creation failed.</returns>
    private async Task<Models.User?> GetOrCreateUserByEmailAsync(string email)
    {
        var userId = await _context.Emails
            .AsNoTracking()
            .Where(e => e.EmailAddress == email && e.IsPrimary)
            .Select(e => e.UserId)
            .FirstOrDefaultAsync();

        if (userId > 0)
            return await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);

        // Create new user with email
        var userName = ExtractNameFromEmail(email);

        var user = new Models.User
        {
            Name = userName,
            Verified = false,
            Suspended = false,
            CreatedAt = DateTime.UtcNow
        };

        await _context.Users.AddAsync(user);

        var emailEntity = new Models.Email
        {
            EmailAddress = email,
            User = user,
            IsPrimary = true
        };

        await _context.Emails.AddAsync(emailEntity);
        await _context.SaveChangesAsync();

        return user;
    }

    /// <summary>
    /// Extracts a display name from an email address by capitalizing the local part.
    /// </summary>
    private static string ExtractNameFromEmail(string emailAddress)
    {
        var localPart = emailAddress.AsSpan();
        var atIndex = localPart.IndexOf('@');
        if (atIndex > 0)
            localPart = localPart[..atIndex];

        if (localPart.Length == 0)
            return "User";

        return $"{char.ToUpper(localPart[0])}{localPart[1..]}";
    }

    private static string? NormalizeStatus(string? threadStatus)
    {
        if (string.IsNullOrWhiteSpace(threadStatus))
            return null;

        var normalized = threadStatus.Trim().ToLowerInvariant();
        return AllowedStatuses.Contains(normalized) ? normalized : null;
    }
}
