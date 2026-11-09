using Chatcheology.Archive;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>One entry of the Starred list: a starred message, or a starred photo.</summary>
    /// <param name="SequenceNumber">The message, to open the chat at.</param>
    /// <param name="DateText">Its day, as separators show it.</param>
    /// <param name="TimeText">Its time, as bubbles show it.</param>
    /// <param name="SenderText">Its sender, or "System".</param>
    /// <param name="Snippet">The text on one line, cut with "…"; for a message without text, its first card's heading; empty for a photo.</param>
    /// <param name="Thumbnail">The photo to show small: the starred photo, or a starred message's first shown photo; otherwise none.</param>
    /// <param name="IsPhoto">True for a starred photo (a tap opens it full screen), false for a starred message (a tap opens the chat).</param>
    public sealed record StarredEntry(
        int SequenceNumber,
        string DateText,
        string TimeText,
        string SenderText,
        string Snippet,
        AttachmentCard? Thumbnail,
        bool IsPhoto);

    /// <summary>
    /// The Starred list (CH-38): every starred message and photo, newest first, each with its date,
    /// sender and a text snippet or a thumbnail.
    /// </summary>
    /// <remarks>
    /// <para>Only the starred messages are read, one by one by sequence number, so the list opens quickly
    /// however long the conversation is. A star whose message the archive does not hold, or whose photo
    /// is no longer a shown photo, is left out of the list (and stays stored).</para>
    /// <para>Order: by sequence number, newest first; within one message, the message before its photos,
    /// photos in attachment order.</para>
    /// </remarks>
    public static class StarredList
    {
        /// <summary>The longest a snippet is, before the "…" mark.</summary>
        public const int SnippetLength = 120;

        /// <summary>Builds the list by reading the starred messages through <paramref name="reader"/>.</summary>
        public static IReadOnlyList<StarredEntry> Build(
            ArchiveReader reader, long localParticipantID, LowQualityIndex lowQuality, Stars stars, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(stars);

            var timeline = new ConversationTimeline(reader, localParticipantID, lowQuality, stars);
            var messages = stars.Messages.ToHashSet();
            var photos = stars.Attachments.ToLookup(star => star.SequenceNumber, star => star.AttachmentID);
            var entries = new List<StarredEntry>();

            foreach (var sequence in messages.Concat(photos.Select(group => group.Key)).Distinct().OrderDescending())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = reader.ReadPageFrom(sequence, 1);

                if (page.Messages is not [var message] || message.SequenceNumber != sequence)
                {
                    continue;
                }

                var row = timeline.BuildMessageRow(message, page.Media);
                var date = DateSeparatorRow.DayText(row.Day);

                if (messages.Contains(sequence))
                {
                    var snippet = row.HasText ? Snippet(row.Text)
                        : row.HasNotShownText ? row.NotShownText
                        : row.ShownAttachments.FirstOrDefault()?.Heading ?? string.Empty;
                    var thumbnail = row.ShownAttachments.FirstOrDefault(card => card.Kind == AttachmentCardKind.Image);

                    entries.Add(new StarredEntry(sequence, date, row.TimeText, row.SenderText, snippet, thumbnail, IsPhoto: false));
                }

                var starredPhotos = photos[sequence].ToHashSet();

                foreach (var card in row.ShownAttachments.Where(card => card.Kind == AttachmentCardKind.Image && starredPhotos.Contains(card.AttachmentID)))
                {
                    entries.Add(new StarredEntry(sequence, date, row.TimeText, row.SenderText, string.Empty, card, IsPhoto: true));
                }
            }

            return entries;
        }

        /// <summary>The photos of the list, in its order: what the viewer swipes through when opened from it.</summary>
        public static IReadOnlyList<AttachmentCard> Photos(IReadOnlyList<StarredEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);

            return entries.Where(entry => entry.IsPhoto && entry.Thumbnail is not null).Select(entry => entry.Thumbnail!).ToList();
        }

        /// <summary>The text on one line, cut at <see cref="SnippetLength"/> without splitting a surrogate pair.</summary>
        public static string Snippet(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            var line = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

            if (line.Length <= SnippetLength)
            {
                return line;
            }

            var end = char.IsHighSurrogate(line[SnippetLength - 1]) ? SnippetLength - 1 : SnippetLength;

            return line[..end] + "…";
        }
    }
}
