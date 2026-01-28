using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DatabaseAccess;
using DatabaseAccess.Models;
using RabbitMQHelper;
using RabbitMQHelper.MessageTypes;
using DbMessage = DatabaseAccess.Models.EmailMessage;

namespace native_desktop_app.ViewModels;

/// <summary>
///     Lightweight row model for the inbox grid.
///     Each item represents one sender and shows only their most recent email.
/// </summary>
public class ConversationSummary
{
    /// <summary>
    ///     Patron name to show in the grid
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    ///     Patron's email address. This is also the key we group on.
    /// </summary>
    public required string EmailAddress { get; init; }

    /// <summary>
    ///     The PR / print-job label the email is about (e.g. "PR-42").
    ///     If we can't resolve it from the email's JobId, we show "—".
    /// </summary>
    public string ActivePrLabel { get; init; } = "—";

    /// <summary>
    ///     UTC timestamp of the newest email for this sender.
    /// </summary>
    public DateTime MostRecentEmailUtc { get; init; }

    /// <summary>
    ///     If we were able to resolve the sender to a known user (via job → user),
    ///     this will carry their user id. Otherwise null.
    /// </summary>
    public long? UserId { get; init; }

    /// <summary>Thread identifier for this conversation.</summary>
    public long ThreadId { get; init; }

    /// <summary>
    ///     Original subject of the conversation (from the first message).
    /// </summary>
    public string OriginalSubject { get; init; } = string.Empty;

    /// <summary>
    ///     Whether the thread has an associated PR/job number.
    /// </summary>
    public bool HasPrNumber => ActivePrLabel != "—";

    /// <summary>
    ///     The thread status (active or archived).
    /// </summary>
    public string ThreadStatus { get; init; } = "active";

    /// <summary>
    ///     Whether the thread needs action (most recent message is from user or system).
    /// </summary>
    public bool IsUnresolved { get; init; }

    /// <summary>
    ///     Whether the operator reply is being sent (queued but not yet processed by email service).
    /// </summary>
    public bool IsSending { get; init; }

    /// <summary>
    ///     Operator readable "how long ago" text based on UTC time.
    ///     Returns empty string when the thread is resolved (most recent message is operator).
    ///     This is what the timer in the VM forces to re-evaluate.
    /// </summary>
    public string WaitingFor
    {
        get
        {
            if (!IsUnresolved) return "";

            var ts = DateTime.UtcNow - MostRecentEmailUtc;
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours} hr {ts.Minutes} min";
            return $"{(int)Math.Round(ts.TotalMinutes)} min";
        }
    }

    /// <summary>
    ///     Computed background color for the row based on thread status and resolution.
    ///     Sending: #458588 (blue - operator reply queued)
    ///     Archived: #3C3836 (dark gray, matches header)
    ///     Unresolved: #cc241d (Gruvbox red)
    ///     Active/Normal: #665c54 (lighter gray)
    /// </summary>
    public string RowBackgroundColor => IsSending ? "#458588"
        : ThreadStatus == "archived" ? "#3C3836"
        : IsUnresolved ? "#cc241d"
        : "#665c54";

    /// <summary>
    ///     Button label for archive/unarchive toggle.
    /// </summary>
    public string ArchiveButtonLabel => ThreadStatus == "archived" ? "Unarchive" : "Archive";

    /// <summary>
    ///     Number of active jobs for this user.
    /// </summary>
    public int ActiveJobs { get; init; }
}

/// <summary>
///     Detailed message model for the "Open Log" dialog.
///     This is per-email (not per-sender).
/// </summary>
public class ConversationEntry
{
    /// <summary>Primary key in email_communications.</summary>
    public required long EmailId { get; init; }

    /// <summary>Subject line stored on the email record.</summary>
    public required string Subject { get; init; }

    /// <summary>Plain-text body of the email.</summary>
    public required string Content { get; init; }

    /// <summary>
    ///     When the email was received, stored in UTC.
    /// </summary>
    public required DateTime ReceivedAtUtc { get; init; }

    /// <summary>The from-address of the message.</summary>
    public required string FromEmailAddress { get; init; }

    /// <summary>The sender type (user, operator, or system).</summary>
    public required string SenderType { get; init; }
}

/// <summary>
///     ViewModel that backs the Messages view.
///     Responsibilities:
///     <list type="bullet">
///         <item>Load unseen + seen emails from the DB.</item>
///         <item>Group them by sender so we only show one row per person.</item>
///         <item>Try to resolve that email to a job → user so we can show a nicer name and PR label.</item>
///         <item>Open a dialog to show the full conversation for that sender.</item>
///         <item>Allow marking the latest message as “closed”.</item>
///         <item>Allow replying right from the dialog.</item>
///         <item>Periodically force UI to refresh the “time since” text.</item>
///     </list>
/// </summary>
public class MessagesViewModel : ViewModelBase
{
    // Data access gateway for repositories used by this view model.
    private readonly DatabaseAccessHelper _db;
    // RabbitMQ helper for publishing operator replies
    private readonly IRmqHelper _rmq;
    // Timer to force bindings to re-evaluate computed durations without reloading data.
    private readonly Timer _tick;
    // Tracks thread IDs that are in "sending" state (operator reply queued but not yet processed)
    private readonly HashSet<long> _sendingThreadIds = new();

    /// <summary>
    ///     Backing store for the inbox rows.
    /// </summary>
    private ObservableCollection<ConversationSummary> _rows = new();

    /// <summary>
    ///     Creates a new MessagesViewModel.
    ///     Expects the app-level DatabaseAccessHelper (so we don't touch the EF context directly)
    ///     and the RabbitMQ helper in case we later want to publish outgoing mail.
    /// </summary>
    public MessagesViewModel(DatabaseAccessHelper databaseAccessHelper, IRmqHelper rmqHelper)
        : base(databaseAccessHelper, rmqHelper)
    {
        _db = databaseAccessHelper;
        _rmq = rmqHelper;

        // Wire commands to async handlers.
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        ShowConversationCommand = new AsyncRelayCommand<ConversationSummary?>(ShowConversationAsync);
        MarkClosedCommand = new AsyncRelayCommand<ConversationSummary?>(MarkConversationResolvedAsync);
        AssignPrCommand = new AsyncRelayCommand<ConversationSummary?>(ShowAssignPrDialogAsync);
        ToggleArchiveCommand = new AsyncRelayCommand<ConversationSummary?>(ToggleArchiveAsync);

        // Initialize RabbitMQ connection
        Task.Run(async () => await _rmq.Connect());

        // Timer: fires every 10s on a background thread.
        // Polls the database for updates and refreshes the view.
        _tick = new Timer(10_000);
        _tick.AutoReset = true;
        _tick.Elapsed += async (_, __) =>
        {
            await LoadAsync();
        };
        _tick.Start();

        // First load is fire-and-forget so ctor stays non-async.
        _ = LoadAsync();
    }

    /// <summary>
    ///     Collection of conversations shown in the UI.
    ///     Replaced wholesale after each refresh.
    /// </summary>
    public ObservableCollection<ConversationSummary> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    /// <summary>
    ///     Little status text for the top bar.
    /// </summary>
    public string CountText => $"Showing {Rows.Count} conversations";

    /// <summary>
    ///     Manual refresh button in the UI.
    /// </summary>
    public ICommand RefreshCommand { get; }

    /// <summary>
    ///     Opens the "log" / conversation dialog for a selected row.
    /// </summary>
    public ICommand ShowConversationCommand { get; }

    /// <summary>
    ///     Marks the latest email for that sender as "closed".
    /// </summary>
    public ICommand MarkClosedCommand { get; }

    /// <summary>
    ///     Opens a dialog to assign a PR/job number to a thread.
    /// </summary>
    public ICommand AssignPrCommand { get; }

    /// <summary>
    ///     Toggles the archive status of a thread (archive if active, activate if archived).
    /// </summary>
    public ICommand ToggleArchiveCommand { get; }

    /// <summary>
    /// Loads all conversations (both active and archived), determining resolution status
    /// based on the most recent message's sender type. Sorts by unresolved first,
    /// then by most recent message time.
    /// Updates <see cref="Rows"/> and notifies dependent bindings.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            Console.WriteLine("LoadAsync started.");
            // Get ALL threads (active and archived)
            var allThreads = await _db.Thread.GetAllThreadsAsync();
            Console.WriteLine($"Found {allThreads.Count} total threads.");

            var rows = new List<ConversationSummary>();

            foreach (var thread in allThreads)
            {
                var threadMessages = await _db.Message.GetMessagesByThreadAsync(thread.Id);
                Console.WriteLine($"Thread {thread.Id} has {threadMessages.Count} messages.");

                if (threadMessages.Count == 0) continue;

                // Get the most recent message for timestamp
                var mostRecentMessage = threadMessages.OrderByDescending(m => m.CreatedAt).First();

                // Use thread status from database (unresolved = needs operator attention)
                var isUnresolved = thread.ThreadStatus == "unresolved";

                // Check if thread is in "sending" state (operator reply queued)
                var isSending = _sendingThreadIds.Contains(thread.Id);
                // If thread is now "active" (resolved), the email was sent - clear sending state
                if (isSending && thread.ThreadStatus == "active")
                {
                    _sendingThreadIds.Remove(thread.Id);
                    isSending = false;
                }

                Console.WriteLine($"Thread {thread.Id}: status={thread.ThreadStatus}, isUnresolved={isUnresolved}, isSending={isSending}");

                var user = await _db.Users.GetUserAsync(thread.UserId);
                var primaryEmail = await _db.Emails.GetUserPrimaryEmailAddressAsync(thread.UserId);

                // Get the first message in the thread to get the original subject
                var firstMessage = threadMessages.OrderBy(m => m.CreatedAt).First();
                var originalSubject = firstMessage.MessageSubject ?? "";
                // Strip "Re:" prefix to get clean subject
                if (originalSubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
                    originalSubject = originalSubject[3..].TrimStart();

                rows.Add(new ConversationSummary
                {
                    DisplayName = user?.Name ?? primaryEmail ?? "Unknown User",
                    EmailAddress = primaryEmail ?? "",
                    ActiveJobs = thread.JobId.HasValue ? 1 : 0,
                    MostRecentEmailUtc = mostRecentMessage.CreatedAt,
                    UserId = thread.UserId,
                    ThreadId = thread.Id,
                    ActivePrLabel = thread.JobId.HasValue ? $"PR-{thread.JobId}" : "—",
                    OriginalSubject = originalSubject,
                    ThreadStatus = thread.ThreadStatus,
                    IsUnresolved = isUnresolved,
                    IsSending = isSending
                });
            }

            // Sort: 1) Unresolved (longest waiting first), 2) Active, 3) Archived
            var sortedRows = rows
                .OrderBy(r => r.ThreadStatus == "archived" ? 2 : r.IsUnresolved ? 0 : 1)
                .ThenBy(r => r.IsUnresolved ? r.MostRecentEmailUtc : DateTime.MaxValue)  // Longest waiting first for unresolved
                .ThenByDescending(r => r.MostRecentEmailUtc)  // Most recent first for others
                .ToList();

            Console.WriteLine($"Added {sortedRows.Count} rows to view.");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Rows = new ObservableCollection<ConversationSummary>(sortedRows);
                OnPropertyChanged(nameof(CountText));  // Ensure count text updates
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in LoadAsync: {ex}");
        }
    }

    /// <summary>
    ///     Starts the polling timer. Called when the view becomes visible.
    /// </summary>
    public void StartPolling()
    {
        _tick?.Start();
    }

    /// <summary>
    ///     Stops the polling timer. Called when the view is no longer visible.
    /// </summary>
    public void StopPolling()
    {
        _tick?.Stop();
    }

    /// <summary>
    ///     Opens a dialog window that shows the **full** email history
    ///     for the selected sender. Also wires the reply + mark buttons
    ///     that live inside that dialog.
    /// </summary>
    private async Task ShowConversationAsync(ConversationSummary? summary)
    {
        if (summary is null) return;

        // Mark all unseen messages in this thread as seen when dialog opens
        await _db.Message.MarkUnseenMessagesSeenAsync(summary.ThreadId);

        var messages = await _db.Message.GetMessagesByThreadAsync(summary.ThreadId);
        var items = messages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ConversationEntry
            {
                EmailId = m.Id,
                Subject = m.MessageSubject ?? "",
                Content = m.MessageContent,
                FromEmailAddress = m.FromEmailAddress ?? "System",
                ReceivedAtUtc = m.CreatedAt,
                SenderType = m.SenderType
            })
            .ToList();

        var dialog = BuildConversationDialog(summary, items);
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        if (lifetime.MainWindow != null)
        {
            await dialog.ShowDialog(lifetime.MainWindow);
        }
    }

    /// <summary>
    ///     Marks the current conversation as resolved by acknowledging all messages
    ///     and archiving the thread. After this, the row should appear as archived.
    /// </summary>
    private async Task MarkConversationResolvedAsync(ConversationSummary? summary)
    {
        if (summary is null)
            return;

        await _db.Message.MarkAllThreadMessagesAcknowledgedAsync(summary.ThreadId);
        await _db.Thread.ArchiveThreadAsync(summary.ThreadId);
        await LoadAsync();
    }

    /// <summary>
    ///     Shows a dialog to assign a PR/job number to a thread.
    /// </summary>
    private async Task ShowAssignPrDialogAsync(ConversationSummary? summary)
    {
        if (summary is null || summary.HasPrNumber) return;

        // Get user ID from email
        var userId = await _db.Emails.GetUserIdByEmailAsync(summary.EmailAddress);
        if (!userId.HasValue)
        {
            // Show error - no user found for this email
            await ShowErrorDialogAsync("No User Found", $"No user found for email: {summary.EmailAddress}");
            return;
        }

        // Get all jobs for this user
        var jobs = await _db.PrintJobs.GetUserPrintJobsAsync(userId.Value);
        if (jobs.Count == 0)
        {
            await ShowErrorDialogAsync("No Jobs Found", $"No jobs found for {summary.DisplayName}");
            return;
        }

        var dialog = new Window
        {
            Title = "Assign PR Number",
            Width = 450,
            Height = 350,
            CornerRadius = new CornerRadius(10),
            Background = Brush.Parse("#282828"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        // Create list items with job info
        var jobItems = jobs.Select(j => new
        {
            Job = j,
            Display = $"PR-{j.Id} | {j.JobStatus} | {j.CreatedAt:MM/dd/yyyy}"
        }).ToList();

        var jobsList = new ListBox
        {
            ItemsSource = jobItems,
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#3c3836")),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 10, 0, 10),
            MaxHeight = 200
        };

        // Set display template with Grid layout for proper column alignment
        jobsList.ItemTemplate = new FuncDataTemplate<object>((item, _) =>
        {
            var job = item?.GetType().GetProperty("Job")?.GetValue(item);
            var id = job?.GetType().GetProperty("Id")?.GetValue(job)?.ToString() ?? "";
            var status = job?.GetType().GetProperty("JobStatus")?.GetValue(job)?.ToString() ?? "";
            var date = job?.GetType().GetProperty("CreatedAt")?.GetValue(job);
            var dateStr = date is DateTime dt ? dt.ToString("MM/dd/yyyy") : "";

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                Margin = new Thickness(5)
            };

            var prText = new TextBlock { Text = $"PR-{id}", Foreground = Brushes.White };
            var statusText = new TextBlock { Text = status, Foreground = Brushes.White };
            var dateText = new TextBlock { Text = dateStr, Foreground = Brushes.White };

            Grid.SetColumn(statusText, 1);
            Grid.SetColumn(dateText, 2);

            grid.Children.Add(prText);
            grid.Children.Add(statusText);
            grid.Children.Add(dateText);

            return grid;
        });

        var assignButton = new Button
        {
            Content = "Assign PR",
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#b8bb26")),
            Foreground = Brushes.Black,
            Margin = new Thickness(0, 0, 10, 0)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#fabd2f")),
            Foreground = Brushes.Black
        };

        assignButton.Click += async (_, __) =>
        {
            var selected = jobsList.SelectedItem;
            if (selected != null)
            {
                var jobId = ((dynamic)selected).Job.Id;
                var result = await _db.Thread.AssociateThreadWithJobAsync(summary.ThreadId, (long)jobId);
                if (result == DatabaseAccess.TransactionResult.Succeeded ||
                    result == DatabaseAccess.TransactionResult.NoAction)
                {
                    dialog.Close();
                    await LoadAsync();
                }
            }
        };

        cancelButton.Click += (_, __) => dialog.Close();

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { assignButton, cancelButton }
        };

        // Column headers for the job list
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            Margin = new Thickness(5, 0, 5, 5)
        };
        var prHeader = new TextBlock { Text = "PR", FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#EBDBB2") };
        var statusHeader = new TextBlock { Text = "Status", FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#EBDBB2") };
        var dateHeader = new TextBlock { Text = "Submitted", FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#EBDBB2") };
        Grid.SetColumn(statusHeader, 1);
        Grid.SetColumn(dateHeader, 2);
        headerGrid.Children.Add(prHeader);
        headerGrid.Children.Add(statusHeader);
        headerGrid.Children.Add(dateHeader);

        var content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock
                {
                    Text = "Select a job to associate with this conversation:",
                    Foreground = Brush.Parse("#EBDBB2"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                },
                headerGrid,
                jobsList,
                buttonPanel
            }
        };

        dialog.Content = content;

        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        if (lifetime.MainWindow != null)
        {
            await dialog.ShowDialog(lifetime.MainWindow);
        }
    }

    /// <summary>
    /// Shows a simple error dialog with a message.
    /// </summary>
    private async Task ShowErrorDialogAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 350,
            Height = 150,
            CornerRadius = new CornerRadius(10),
            Background = Brush.Parse("#282828"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var okButton = new Button
        {
            Content = "OK",
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#fabd2f")),
            Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        okButton.Click += (_, __) => dialog.Close();

        var content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    Foreground = Brush.Parse("#EBDBB2"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 15)
                },
                okButton
            }
        };

        dialog.Content = content;

        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        if (lifetime.MainWindow != null)
        {
            await dialog.ShowDialog(lifetime.MainWindow);
        }
    }

    /// <summary>
    ///     Builds an iMessage-style conversation dialog with chat bubbles.
    /// </summary>
    /// <param name="summary">The conversation summary to display in the header.</param>
    /// <param name="messages">The ordered message entries to render in the body.</param>
    /// <returns>A configured <see cref="Window"/> ready to be shown.</returns>
    private Window BuildConversationDialog(ConversationSummary summary, IEnumerable<ConversationEntry> messages)
    {
        var window = new Window
        {
            Title = $"Messages — {summary.DisplayName}",
            Width = 700,
            Height = 700,
            CornerRadius = new CornerRadius(10),
            Background = Brush.Parse("#282828"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        // --- header (name, email, subject, PR) ---
        var header = new Border
        {
            Background = Brush.Parse("#3C3836"),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{summary.DisplayName} <{summary.EmailAddress}>",
                        Foreground = Brushes.White,
                        FontWeight = FontWeight.Bold,
                        FontSize = 16
                    },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(summary.OriginalSubject)
                            ? "No subject"
                            : summary.OriginalSubject,
                        Foreground = Brush.Parse("#EBDBB2"),
                        FontSize = 14,
                        FontStyle = FontStyle.Italic
                    },
                    new TextBlock
                    {
                        Text = summary.HasPrNumber ? summary.ActivePrLabel : "No PR assigned",
                        Foreground = Brush.Parse("#928374"),
                        FontSize = 12
                    }
                }
            }
        };

        // --- iMessage-style messages list ---
        var messageList = new StackPanel
        {
            Margin = new Thickness(12)
        };

        foreach (var m in messages)
        {
            var isUserMessage = m.SenderType.Equals("user", StringComparison.OrdinalIgnoreCase);
            var isSystemMessage = m.SenderType.Equals("system", StringComparison.OrdinalIgnoreCase);

            // Bubble colors based on sender type
            // User: gray (#504945), Operator: blue (#458588), System: aqua (#689d6a)
            var bubbleColor = isUserMessage ? "#504945"
                : isSystemMessage ? "#689d6a"
                : "#458588";
            var textColor = isSystemMessage ? "#282828" : "#EBDBB2";

            // Alignment: user messages left, operator/system messages right
            var alignment = isUserMessage ? HorizontalAlignment.Left : HorizontalAlignment.Right;

            var bubble = new Border
            {
                Background = Brush.Parse(bubbleColor),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 4, 0, 4),
                MaxWidth = 480, // roughly 70% of 700px window
                HorizontalAlignment = alignment,
                Child = new TextBlock
                {
                    Text = m.Content,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush.Parse(textColor),
                    FontSize = 14
                }
            };

            var timestamp = new TextBlock
            {
                Text = m.ReceivedAtUtc.ToLocalTime().ToString("MMM d, h:mm tt"),
                Foreground = Brush.Parse("#928374"),
                FontSize = 11,
                Margin = new Thickness(isUserMessage ? 16 : 0, 0, isUserMessage ? 0 : 16, 8),
                HorizontalAlignment = alignment
            };

            messageList.Children.Add(bubble);
            messageList.Children.Add(timestamp);
        }

        var messagesScrollViewer = new ScrollViewer
        {
            Content = messageList,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        // --- Reply area with text wrapping and auto-expand ---
        var replyBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            MaxHeight = 200,
            Margin = new Thickness(0, 10, 0, 10),
            Watermark = "Type your reply here..."
        };

        var sendButton = new Button
        {
            Content = "Send Reply",
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#b8bb26")),
            Foreground = Brushes.Black
        };

        sendButton.Click += async (_, __) =>
        {
            if (string.IsNullOrWhiteSpace(replyBox.Text)) return;

            // Publish to RabbitMQ for email sending
            // the email service will persist the message after it's actually sent
            try
            {
                var thread = await _db.Thread.GetThreadByIdAsync(summary.ThreadId);
                if (thread != null)
                {
                    // Get all messages in this thread to find the most recent customer message
                    var threadMessages = await _db.Message.GetMessagesByThreadAsync(summary.ThreadId);
                    var mostRecentCustomerMessage = threadMessages
                        .Where(m => m.SenderType == "user")
                        .OrderByDescending(m => m.CreatedAt)
                        .FirstOrDefault();

                    // Use the actual thread subject with "Re:" prefix
                    var threadSubject = mostRecentCustomerMessage?.MessageSubject ?? "Your OpenFarm Inquiry";
                    var replySubject = threadSubject.TrimStart().StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                        ? threadSubject
                        : $"Re: {threadSubject}";

                    var operatorReply = new OperatorReplyMessage
                    {
                        JobId = thread.JobId ?? 0,
                        CustomerEmail = summary.EmailAddress,
                        Subject = replySubject,
                        Body = replyBox.Text,
                        ThreadId = summary.ThreadId,
                        MessageId = mostRecentCustomerMessage?.Id ?? 0
                    };
                    await _rmq.QueueMessage(ExchangeNames.OperatorReply, operatorReply);

                    // Mark all messages in thread as acknowledged after sending reply
                    await _db.Message.MarkAllThreadMessagesAcknowledgedAsync(summary.ThreadId);

                    // Track as "sending" - row will show blue until email service processes
                    _sendingThreadIds.Add(summary.ThreadId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to publish operator reply to RabbitMQ: {ex.Message}");
            }

            window.Close();
            await LoadAsync();
        };

        var closeBtn = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Width = 100,
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#fabd2f")),
            Foreground = Brushes.Black
        };
        closeBtn.Click += (_, __) => window.Close();

        // Archive/Unarchive button (left side) - toggles based on thread status
        var isArchived = summary.ThreadStatus == "archived";
        var archiveBtn = new Button
        {
            Content = isArchived ? "Unarchive" : "Archive",
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#665c54")),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 10, 0)
        };
        archiveBtn.Click += async (_, __) =>
        {
            if (isArchived)
            {
                // Check last message to determine correct status
                var messages = await _db.Message.GetMessagesByThreadAsync(summary.ThreadId);
                var lastMessage = messages.OrderByDescending(m => m.CreatedAt).FirstOrDefault();

                if (lastMessage?.SenderType == "operator")
                    await _db.Thread.ActivateThreadAsync(summary.ThreadId);  // Resolved
                else
                    await _db.Thread.MarkThreadUnresolvedAsync(summary.ThreadId);  // Needs attention
            }
            else
            {
                await _db.Thread.ArchiveThreadAsync(summary.ThreadId);
            }
            window.Close();
            await LoadAsync();
        };

        // PR button (only if no PR assigned)
        Button? prBtn = null;
        if (!summary.HasPrNumber)
        {
            prBtn = new Button
            {
                Content = "Add PR",
                Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#665c54")),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 10, 0)
            };
            prBtn.Click += async (_, __) =>
            {
                await ShowAssignPrDialogAsync(summary);
            };
        }

        // Button panel with Archive/PR on left, other buttons on right
        var leftButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { archiveBtn }
        };
        if (prBtn != null) leftButtons.Children.Add(prBtn);

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { sendButton, closeBtn }
        };

        var buttonPanel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        buttonPanel.Children.Add(leftButtons);
        Grid.SetColumn(leftButtons, 0);
        buttonPanel.Children.Add(rightButtons);
        Grid.SetColumn(rightButtons, 1);

        var footer = new Border
        {
            Background = Brush.Parse("#3C3836"),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Children = { replyBox, buttonPanel }
            }
        };

        var root = new DockPanel();

        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(messagesScrollViewer);

        window.Content = root;

        // Auto-focus reply textbox when window opens
        window.Opened += (_, __) => replyBox.Focus();

        return window;
    }

    /// <summary>
    ///     Toggles the archive status of a thread.
    ///     When unarchiving, checks the last message to determine correct status:
    ///     - If last message was from operator: set to active (resolved)
    ///     - Otherwise: set to unresolved (needs attention)
    /// </summary>
    private async Task ToggleArchiveAsync(ConversationSummary? summary)
    {
        if (summary == null) return;

        if (summary.ThreadStatus == "archived")
        {
            // Check last message to determine correct status
            var messages = await _db.Message.GetMessagesByThreadAsync(summary.ThreadId);
            var lastMessage = messages.OrderByDescending(m => m.CreatedAt).FirstOrDefault();

            if (lastMessage?.SenderType == "operator")
                await _db.Thread.ActivateThreadAsync(summary.ThreadId);  // Resolved
            else
                await _db.Thread.MarkThreadUnresolvedAsync(summary.ThreadId);  // Needs attention
        }
        else
        {
            await _db.Thread.ArchiveThreadAsync(summary.ThreadId);
        }

        await LoadAsync();
    }
}
