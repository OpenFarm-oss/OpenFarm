using RabbitMQHelper;

namespace EmailService.Constants;

/// <summary>
/// Default values and constants for email processing.
/// </summary>
public static class EmailDefaults
{
    /// Default subject text when an email has no subject.
    public const string DefaultSubject = "(No subject)";

    /// Default body text when an email has no readable content.
    public const string DefaultBodyContent = "(No body content)";

    /// <summary>
    /// Converts a rejection reason enum to a human-readable string.
    /// </summary>
    /// <param name="reason">The rejection reason.</param>
    /// <returns>A user-friendly description of the rejection reason.</returns>
    public static string GetRejectReasonText(RejectReason reason) => reason switch
    {
        RejectReason.CancelledByUser => "Cancelled by user",
        RejectReason.FailedValidation => "Failed system validation",
        RejectReason.RejectedByApprover => "Rejected by approver",
        RejectReason.FailedToPrint => "Failed to print",
        RejectReason.FailedDownload => "Failed to download file",
        _ => "Rejected for unknown reason"
    };
}

