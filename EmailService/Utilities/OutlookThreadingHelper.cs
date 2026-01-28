using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MimeKit;

namespace EmailService.Utilities;

/// <summary>
/// Provides utilities for adding Microsoft Outlook-specific email threading headers. Since Outlook ignores standard RFC 2822 threading headers, we need to use proprietary headers.
/// </summary>
/// <remarks>
/// <para>
/// Outlook ignores standard RFC 2822 threading headers (In-Reply-To, References) and instead
/// relies on proprietary headers:
/// </para>
/// <list type="bullet">
/// <item>
/// <term>Thread-Topic</term>
/// <description>The normalized subject without Re:/Fwd: prefixes</description>
/// </item>
/// <item>
/// <term>Thread-Index</term>
/// <description>A base64-encoded identifier linking messages in a conversation</description>
/// </item>
/// </list>
/// </remarks>
public static class OutlookThreadingHelper
{
    /// The size in bytes of the root Thread-Index (timestamp + GUID).
    private const int RootIndexSize = 22;

    /// The size in bytes of each child block appended for replies.
    private const int ChildBlockSize = 5;

    /// <summary>
    /// Adds Microsoft Outlook-specific threading headers to enable conversation grouping.
    /// </summary>
    /// <param name="message">The MIME message to add headers to.</param>
    /// <param name="subject">The email subject (used to derive Thread-Topic).</param>
    /// <param name="parentThreadIndex">
    /// The Thread-Index of the parent email for replies, or <c>null</c> for new conversations.
    /// </param>
    /// <returns>The generated Thread-Index value (base64-encoded).</returns>
    public static string AddThreadingHeaders(MimeMessage message, string subject, string? parentThreadIndex)
    {
        var threadTopic = SubjectNormalizer.Normalize(subject);
        message.Headers.Add("Thread-Topic", threadTopic);

        var threadIndex = GenerateThreadIndex(threadTopic, parentThreadIndex);
        message.Headers.Add("Thread-Index", threadIndex);

        return threadIndex;
    }

    /// <summary>
    /// Generates a Thread-Index header value for Outlook conversation threading.
    /// </summary>
    /// <param name="threadTopic">The normalized conversation topic (subject without prefixes).</param>
    /// <param name="parentThreadIndex">
    /// The base64-encoded Thread-Index of the parent email, or <c>null</c> for new conversations.
    /// </param>
    /// <returns>A base64-encoded Thread-Index value.</returns>
    /// <remarks>
    /// <para>
    /// The Thread-Index format is a Microsoft proprietary blob:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <term>Root message (22 bytes)</term>
    /// <description>6-byte FILETIME timestamp + 16-byte GUID</description>
    /// </item>
    /// <item>
    /// <term>Reply (parent length + 5 bytes)</term>
    /// <description>Parent's Thread-Index + 5-byte child identifier</description>
    /// </item>
    /// </list>
    /// <para>
    /// When we have the parent's Thread-Index, we append a 5-byte child block to maintain
    /// the conversation chain. When we don't have it (new conversation or missing parent),
    /// we generate a fresh root index using a deterministic GUID from the topic.
    /// </para>
    /// </remarks>
    private static string GenerateThreadIndex(string threadTopic, string? parentThreadIndex)
    {
        if (!string.IsNullOrEmpty(parentThreadIndex))
        {
            try
            {
                var parentBytes = Convert.FromBase64String(parentThreadIndex);
                if (parentBytes.Length >= RootIndexSize)
                    return AppendChildBlock(parentBytes);
            }
            catch (FormatException)
            {
                return GenerateRootIndex(threadTopic);
            }
        }

        return GenerateRootIndex(threadTopic);
    }

    /// <summary>
    /// Generates a root Thread-Index for a new conversation.
    /// </summary>
    /// <param name="threadTopic">The normalized conversation topic.</param>
    /// <returns>A base64-encoded 22-byte root Thread-Index.</returns>
    private static string GenerateRootIndex(string threadTopic)
    {
        Span<byte> rootIndex = stackalloc byte[RootIndexSize];

        /// Write the timestamp to the root index
        WriteTimestamp(rootIndex[..6]);

        /// Generate deterministic GUID from topic for consistency across messages
        Span<byte> topicBytes = stackalloc byte[threadTopic.Length];
        Encoding.UTF8.GetBytes(threadTopic.ToLowerInvariant(), topicBytes);
        Span<byte> topicHash = stackalloc byte[32];
        SHA256.HashData(topicBytes, topicHash);
        Guid topicGuid = new(topicHash[..16]);
        topicGuid.ToByteArray().CopyTo(rootIndex[6..]);
        return Convert.ToBase64String(rootIndex);
    }

    /// <summary>
    /// Appends a 5-byte child block to an existing Thread-Index.
    /// </summary>
    /// <param name="parentBytes">The parent's Thread-Index bytes.</param>
    /// <returns>A base64-encoded Thread-Index with the child block appended.</returns>
    private static string AppendChildBlock(ReadOnlySpan<byte> parentBytes)
    {
        Span<byte> replyIndex = stackalloc byte[parentBytes.Length + ChildBlockSize];
        parentBytes.CopyTo(replyIndex[..parentBytes.Length]);
        WriteChildBlock(replyIndex[parentBytes.Length..]);
        return Convert.ToBase64String(replyIndex);
    }

    /// <summary>
    /// Writes a 6-byte timestamp to the destination span.
    /// </summary>
    /// <param name="destination">The span to write the timestamp to.</param>
    /// <remarks>
    /// FILETIME is an 8-byte value representing 100-nanosecond intervals since 1601.
    /// Thread-Index uses only the high 6 bytes in big-endian order, giving ~42-second
    /// resolution with a range of ~582 years.
    /// </remarks>
    private static void WriteTimestamp(Span<byte> destination)
    {
        var filetime = DateTime.UtcNow.ToFileTimeUtc();

        // Write as big-endian and take high 6 bytes
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, filetime);
        buffer[..6].CopyTo(destination);
    }

    /// <summary>
    /// Writes a 5-byte child block to the destination span.
    /// </summary>
    /// <param name="destination">The span to write the child block to.</param>
    /// <remarks>
    /// The child block format per Microsoft spec is a compressed time delta and flags.
    /// We generate it from the current timestamp to provide uniqueness and ordering.
    /// </remarks>
    private static void WriteChildBlock(Span<byte> destination)
    {
        var filetime = DateTime.UtcNow.ToFileTimeUtc();

        // Use a portion of the timestamp to ensure uniqueness and rough chronological ordering
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, filetime);
        buffer.Slice(2, 5).CopyTo(destination);
    }
}
