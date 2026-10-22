using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Mobile.Presentation
{
    /// <summary>One line of a photo's Info: what it is, and the archive's value.</summary>
    /// <param name="Label">The line's name, such as "Date".</param>
    /// <param name="Value">The value as shown.</param>
    public sealed record MediaDetail(string Label, string Value);

    /// <summary>
    /// The full-screen photo viewer's Info (CH-34): the message's date and time, its sender, and the
    /// attachment's and media's recorded facts.
    /// </summary>
    /// <remarks>
    /// <para>Everything comes from the archive's rows; nothing is computed from the file or guessed. A
    /// value the archive does not hold is left out, never shown as unknown.</para>
    /// <para>Forwarded follows the desktop's rule: a line only when the attachment stores 1 (null means not
    /// known and shows nothing), "Many times" from <see cref="MessageRow.ForwardedManyTimesScore"/>.</para>
    /// </remarks>
    public static class MediaDetails
    {
        /// <summary>The Info lines, in the order the panel shows them.</summary>
        /// <param name="messageTime">The message's stored local time.</param>
        /// <param name="senderName">The sender's display name, or <see langword="null"/> when there is none.</param>
        /// <param name="attachment">The attachment row.</param>
        /// <param name="media">The media row, or <see langword="null"/> when the archive has none for it.</param>
        public static IReadOnlyList<MediaDetail> Of(DateTime messageTime, string? senderName, ArchiveAttachment attachment, ArchiveMedia? media)
        {
            ArgumentNullException.ThrowIfNull(attachment);

            var details = new List<MediaDetail>(8)
            {
                new("Date", DateSeparatorRow.DayText(DateOnly.FromDateTime(messageTime))),
                new("Time", MessageRow.TimeTextOf(messageTime)),
            };

            if (!string.IsNullOrWhiteSpace(senderName))
            {
                details.Add(new("Sender", senderName));
            }

            if (!string.IsNullOrWhiteSpace(media?.MediaType))
            {
                details.Add(new("Type", media.MediaType));
            }

            if (media is { Width: int width and > 0, Height: int height and > 0 })
            {
                details.Add(new("Dimensions", string.Format(CultureInfo.CurrentCulture, "{0} × {1}", width, height)));
            }

            if (media is { SizeBytes: >= 0 })
            {
                details.Add(new("Size", SizeText(media.SizeBytes)));
            }

            if (attachment.IsForwarded == true)
            {
                details.Add(new("Forwarded", attachment.ForwardScore >= MessageRow.ForwardedManyTimesScore ? "Many times" : "Yes"));
            }

            if (!string.IsNullOrWhiteSpace(attachment.OriginalFileName))
            {
                details.Add(new("File name", attachment.OriginalFileName));
            }

            return details;
        }

        /// <summary>A size in bytes as the panel shows it: bytes, then KB, MB and GB in steps of 1000, as Android does.</summary>
        public static string SizeText(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);

            var culture = CultureInfo.CurrentCulture;

            return bytes switch
            {
                1 => "1 byte",
                < 1000 => bytes.ToString(culture) + " bytes",
                < 999_950 => (bytes / 1e3).ToString("0.#", culture) + " KB",
                < 999_950_000 => (bytes / 1e6).ToString("0.#", culture) + " MB",
                _ => (bytes / 1e9).ToString("0.#", culture) + " GB",
            };
        }
    }
}
