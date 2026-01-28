using System.Net;

namespace EmailService.Utilities;

/// <summary>
/// Provides utilities for formatting and converting text for email content.
/// </summary>
public static class TextFormatting
{
    /// Gmail search URL format for constructing direct message links.
    private const string GmailSearchUrlFormat = $"https://mail.google.com/mail/u/0/#search/rfc822msgid:{{0}}";

    /// Attachment notification text without a Gmail link.
    private const string AttachmentNotePlain = "[Attachments detected - view original email in Gmail: No message ID provided.]";

    /// Attachment notification format string with a Gmail link placeholder.
    private const string AttachmentNoteWithLink = $"[Attachments detected - view original email: {GmailSearchUrlFormat}].";

    /// <summary>
    /// Converts plain text to HTML paragraphs with proper styling.
    /// </summary>
    /// <param name="text">The plain text to convert.</param>
    /// <returns>HTML string with each line wrapped in styled paragraph tags.</returns>
    /// <remarks>
    /// Empty lines are converted to non-breaking space paragraphs to preserve spacing.
    /// All text is HTML-encoded to prevent XSS vulnerabilities.
    /// </remarks>
    public static string ConvertTextToHtmlParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) 
            return string.Empty;

        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        return string.Concat(lines.Select(line =>
            string.IsNullOrWhiteSpace(line)
                ? "<p style=\"margin:0 0 12px 0;\">&nbsp;</p>"
                : $"<p style=\"margin:0 0 12px 0;\">{WebUtility.HtmlEncode(line)}</p>"));
    }

    /// <summary>
    /// Builds an attachment notification message with an optional Gmail search link.
    /// </summary>
    /// <param name="messageId">The Message-ID for constructing the Gmail link.</param>
    /// <returns>A notification string for the user.</returns>
    public static string BuildAttachmentNote(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) 
            return AttachmentNotePlain;
        return string.Format(AttachmentNoteWithLink, messageId);
    }
}
