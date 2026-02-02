using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DatabaseAccess;
using DatabaseAccess.Models;
using DynamicData;
using RabbitMQHelper;

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

// TODO: Attach RMQ listener to appropriate chanel to listen for the PSPS marking a job "systemApproved"
// TODO: Write listener method to query new job, check it is systemApproved, and add it to _jobsAwaitingStaffReview

        // Retrieve PrintJobs that are ready to be reviewed by an Operator
        _jobsAwaitingStaffReview = new ObservableCollection<PrintJob>();
        JobsAwaitingStaffReview = new ReadOnlyObservableCollection<PrintJob>(_jobsAwaitingStaffReview);
        Task<List<PrintJob>> result =
           _databaseAccessHelper.PrintJobs.GetSystemApprovedPrintJobsAsync();
        result.ContinueWith(task => _jobsAwaitingStaffReview.AddRange(task.Result));
        
    }

    public async Task MarkStaffApprovedAsync(PrintJob job) {
        var result = await _databaseAccessHelper.PrintJobs.UpdatePrintJobStatusAsync(job.Id, "operatorApproved");
        if (result == TransactionResult.Succeeded)
            _jobsAwaitingStaffReview.Remove(job);
    }
    
    public async Task MarkNotApprovedAsync(PrintJob job) {
        TransactionResult result = await _databaseAccessHelper.PrintJobs.UpdatePrintJobStatusAsync(job.Id, "rejected");
        if (result == TransactionResult.Succeeded)
            _jobsAwaitingStaffReview.Remove(job);
        // TODO: check TransactionResult success; upon failure, show error in a modal popup within UI
    }

}