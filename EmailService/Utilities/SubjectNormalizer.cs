using System.Text.RegularExpressions;

namespace EmailService.Utilities;

/// <summary>
/// Provides utilities for normalizing and parsing email subject lines.
/// </summary>
public static class SubjectNormalizer
{
    /// Compiled regex for extracting job numbers from subject lines (e.g., "#12345").
    private static readonly Regex JobNumberPattern = new(
        @"#(?<digits>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Normalizes an email subject by removing reply/forward prefixes and trimming whitespace.
    /// </summary>
    /// <param name="subject">The original email subject.</param>
    /// <returns>
    /// The normalized subject with all Re:, RE:, Fw:, FW:, and Fwd: prefixes removed.
    /// Returns an empty string if the input is null or whitespace.
    /// </returns>
    /// <example>
    /// <code>
    /// Normalize("Re: Fw: Hello World") // Returns "Hello World"
    /// Normalize("RE: RE: RE: Test")    // Returns "Test"
    /// </code>
    /// </example>
    public static string Normalize(ReadOnlySpan<char> subject)
    {
        var normalized = subject.Trim();

        while (normalized.Length > 0)
        {
            if (normalized.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) || 
                normalized.StartsWith("Fw:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[3..].Trim();
            else if (normalized.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[4..].Trim();
            else
                break;
        }

        return normalized.ToString();
    }

    /// <summary>
    /// Attempts to extract a job ID from an email subject.
    /// </summary>
    /// <param name="subject">The email subject to parse.</param>
    /// <returns>The extracted job ID, or <c>null</c> if no job ID was found.</returns>
    /// <remarks>
    /// Job IDs are expected to be in the format "#12345" (hash followed by digits).
    /// </remarks>
    public static long? ExtractJobId(string subject)
    {
        var match = JobNumberPattern.Match(subject);
        return match.Success && long.TryParse(match.Groups["digits"].Value, out var jobId) ? jobId : null;
    }

    /// <summary>
    /// Determines whether a subject line indicates a reply.
    /// </summary>
    /// <param name="subject">The subject to check.</param>
    /// <returns><c>true</c> if the subject starts with "Re:"; otherwise, <c>false</c>.</returns>
    public static bool IsReplySubject(ReadOnlySpan<char> subject) => 
        subject.Trim().StartsWith("Re:", StringComparison.OrdinalIgnoreCase);
}
