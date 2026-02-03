using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DatabaseAccess;
using DatabaseAccess.Models;
using DynamicData;
using RabbitMQHelper;
using RabbitMQHelper.MessageTypes;

namespace NativeDesktopApp.Models;

public class AppStateModel
{
    private readonly DatabaseAccessHelper _databaseAccessHelper;
    private readonly IRmqHelper _rmqHelper;

    // private ObservableCollection<PrintJob> _activeJobsList;
    /// <summary>
    ///     Jobs requiring staff review (i.e., those with status <c>systemApproved</c>).
    /// </summary>
    private readonly ObservableCollection<PrintJob> _jobsAwaitingStaffReview;

    public ReadOnlyObservableCollection<PrintJob> JobsAwaitingStaffReview;

    public AppStateModel(DatabaseAccessHelper databaseAccessHelper, IRmqHelper rmqHelper)
    {
        // Initialize helper objects
        _rmqHelper = rmqHelper;
        _databaseAccessHelper = databaseAccessHelper;

        AttachRMQListeners(Task.CompletedTask);

        // Retrieve PrintJobs that are ready to be reviewed by an Operator
        _jobsAwaitingStaffReview = new ObservableCollection<PrintJob>();
        JobsAwaitingStaffReview = new ReadOnlyObservableCollection<PrintJob>(_jobsAwaitingStaffReview);
        Task<List<PrintJob>> result =
            _databaseAccessHelper.PrintJobs.GetSystemApprovedPrintJobsAsync();
        result.ContinueWith(task => _jobsAwaitingStaffReview.AddRange(task.Result));
    }

    private void AttachRMQListeners(Task obj)
    {
        if (_rmqHelper.IsConnected())
        {
            _rmqHelper.AddListener<Message>(QueueNames.DesktopNotification, m => ProcessRMQNotification(m).Result);
            // TODO: add listeners as required
        }
        else
        {
            Task.Delay(1000).ContinueWith(AttachRMQListeners);
        }
    }

    public async Task MarkStaffApprovedAsync(PrintJob job)
    {
        var result = await _databaseAccessHelper.PrintJobs.UpdatePrintJobStatusAsync(job.Id, "operatorApproved");
        if (result == TransactionResult.Succeeded)
            _jobsAwaitingStaffReview.Remove(job);
    }

    public async Task MarkNotApprovedAsync(PrintJob job)
    {
        TransactionResult result = await _databaseAccessHelper.PrintJobs.UpdatePrintJobStatusAsync(job.Id, "rejected");
        if (result == TransactionResult.Succeeded)
            _jobsAwaitingStaffReview.Remove(job);
        // TODO: check TransactionResult success; upon failure, show error in a modal popup within UI
    }

    private async Task<bool> ProcessRMQNotification(Message message)
    {
        try
        {
            PrintJob? job = await _databaseAccessHelper.PrintJobs.GetPrintJobAsync(message.JobId, true);

            if (job == null)
                throw new Exception("Job not found.");
            if (job.JobStatus == "systemApproved")
                _jobsAwaitingStaffReview.Add(job);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return false;
        }
    }
}