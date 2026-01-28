using System.Net;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Text;

namespace EmailService.Utilities;

/// <summary>
/// Provides utilities for extracting and cleaning email body content from MIME messages.
/// </summary>
/// <remarks>
/// <para>
/// This class handles:
/// </para>
/// <list type="bullet">
/// <item><description>Extracting plain text from MIME messages, preferring text/plain over HTML</description></item>
/// <item><description>Stripping HTML tags, scripts, and styles from HTML content</description></item>
/// <item><description>Removing quoted reply content (e.g., "On ... wrote:" blocks)</description></item>
/// </list>
/// </remarks>
public static class EmailBodyParser
{
    private static readonly Regex OnWrotePattern = new(
        @"^On .+ wrote:$", 
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex FromHeaderPattern = new(
        @"^From:\s", 
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex OriginalMessagePattern = new(
        @"^-+\s*Original Message\s*-+$", 
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex UnderscoreSeparatorPattern = new(
        "^_{2,}$", 
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex HtmlStripPattern = new(
        @"<script[\s\S]*?</script>|<style[\s\S]*?</style>|<[^>]+>", 
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    /// <summary>Patterns that indicate the start of quoted reply content in email bodies.</summary>
    private static readonly IReadOnlyList<Regex> ReplySeparatorPatterns =
    [
        OnWrotePattern,
        FromHeaderPattern,
        OriginalMessagePattern,
        UnderscoreSeparatorPattern
    ];

    /// <summary>
    /// Extracts the body text from a MIME message, removing quoted reply content.
    /// </summary>
    /// <param name="message">The MIME message to extract from.</param>
    /// <returns>The cleaned body text with quoted content removed, or an empty string if no body is available.</returns>
    public static string GetCleanBodyText(MimeMessage message)
    {
        /// Get the plain text body from the message if available
        var textBody = message.GetTextBody(TextFormat.Plain) ?? message.TextBody;
        if (!string.IsNullOrWhiteSpace(textBody))
        {
            var cleaned = RemoveQuotedContent(textBody);
            return string.IsNullOrWhiteSpace(cleaned) ? textBody.Trim() : cleaned;
        }

        /// Get the HTML body from the message if available
        var htmlBody = message.HtmlBody;
        if (string.IsNullOrWhiteSpace(htmlBody))
            return string.Empty;

        var plainText = StripHtmlTags(htmlBody);
        var cleanedHtml = RemoveQuotedContent(plainText);
        return string.IsNullOrWhiteSpace(cleanedHtml) ? plainText : cleanedHtml;
    }

    /// <summary>
    /// Removes quoted reply content from email text, keeping only the new content.
    /// </summary>
    /// <param name="text">The email text to process.</param>
    /// <returns>The text with quoted content removed.</returns>
    private static string RemoveQuotedContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) 
            return string.Empty;

        using var reader = new StringReader(text);
        var collectedLines = new List<string>();
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();

            /// If the line is not empty and starts with '>' or is a separator, break the loop
            if (!string.IsNullOrEmpty(trimmed) && 
                (trimmed.StartsWith('>') || ReplySeparatorPatterns.Any(pattern => pattern.IsMatch(trimmed))))
                break;

            collectedLines.Add(line);
        }

        if (collectedLines.Count == 0) 
            return string.Empty;

        /// Remove any trailing empty lines
        int lastNonEmptyIndex = collectedLines.Count - 1;
        while (lastNonEmptyIndex >= 0 && string.IsNullOrWhiteSpace(collectedLines[lastNonEmptyIndex]))
            lastNonEmptyIndex--;

        if (lastNonEmptyIndex < collectedLines.Count - 1)
            collectedLines.RemoveRange(lastNonEmptyIndex + 1, collectedLines.Count - lastNonEmptyIndex - 1);

        return string.Join(Environment.NewLine, collectedLines).Trim();
    }

    /// <summary>
    /// Converts HTML to plain text by removing tags and decoding entities.
    /// </summary>
    /// <param name="html">The HTML content to strip.</param>
    /// <returns>Plain text with HTML tags removed.</returns>
    private static string StripHtmlTags(string html) => 
        WebUtility.HtmlDecode(HtmlStripPattern.Replace(html, string.Empty)).Trim();
}
