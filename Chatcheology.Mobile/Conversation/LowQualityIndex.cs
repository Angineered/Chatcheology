using Chatcheology.Archive;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>
    /// The conversation's low-quality copy links, read once, so a page can decide which copies to hide
    /// without its full copies being loaded.
    /// </summary>
    /// <remarks>
    /// <para><strong>The rule</strong> (the desktop's, with no toggle, plus Angie's exception of
    /// 2026-10-21): an attachment that is a low-quality copy of another is hidden, and its full copy
    /// counts it as a hidden copy (no note is shown). <strong>Except</strong> a Restored copy whose
    /// full copy is not Restored: it is the only copy of that photo the archive has, so it is shown,
    /// labelled "Low-quality copy", and it is not counted in the full copy's hidden copies.</para>
    /// <para>A copy and its full copy can sit in different messages, and so on different pages, which
    /// is why this is built from the whole conversation rather than page by page. Only links and states
    /// are kept: a few numbers per linked attachment.</para>
    /// <para>The archive writes a link to a full copy outside the conversation as null (format v1), so
    /// such a copy is not known to be a copy at all: it is shown as an ordinary attachment, without the
    /// label. Nothing here can tell it apart.</para>
    /// </remarks>
    public sealed class LowQualityIndex
    {
        private readonly HashSet<long> _shownCopies;
        private readonly Dictionary<long, int> _hiddenCopies;

        private LowQualityIndex(HashSet<long> shownCopies, Dictionary<long, int> hiddenCopies)
        {
            _shownCopies = shownCopies;
            _hiddenCopies = hiddenCopies;
        }

        /// <summary>An index with no links.</summary>
        public static LowQualityIndex Empty { get; } = new([], []);

        /// <summary>Builds the index from every page of the conversation.</summary>
        public static LowQualityIndex Build(IEnumerable<ArchivePage> pages, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(pages);

            var states = new Dictionary<long, ArchiveAttachmentState>();
            var links = new List<(long Copy, ArchiveAttachmentState CopyState, long Full)>();

            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var message in page.Messages)
                {
                    foreach (var attachment in message.Attachments)
                    {
                        states[attachment.AttachmentID] = attachment.State;

                        if (attachment.LowQualityCopyOfAttachmentID is { } full)
                        {
                            links.Add((attachment.AttachmentID, attachment.State, full));
                        }
                    }
                }
            }

            var shown = new HashSet<long>();
            var hidden = new Dictionary<long, int>();

            foreach (var (copy, copyState, full) in links)
            {
                // A link the validator would refuse (no such attachment) is treated as a full copy that
                // is not Restored: the copy is then the only one there is.
                var fullRestored = states.TryGetValue(full, out var fullState) && fullState == ArchiveAttachmentState.Restored;

                if (copyState == ArchiveAttachmentState.Restored && !fullRestored)
                {
                    shown.Add(copy);
                }
                else
                {
                    hidden[full] = hidden.GetValueOrDefault(full) + 1;
                }
            }

            return new LowQualityIndex(shown, hidden);
        }

        /// <summary>Builds the index by reading the whole conversation through <paramref name="reader"/>.</summary>
        public static LowQualityIndex Build(ArchiveReader reader, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(reader);

            return Build(reader.ReadAllPages(), cancellationToken);
        }

        /// <summary>
        /// Whether <paramref name="attachment"/> is shown: anything that is not a low-quality copy, and a
        /// copy that is the archive's only restored copy of its media.
        /// </summary>
        public bool IsShown(ArchiveAttachment attachment)
        {
            ArgumentNullException.ThrowIfNull(attachment);

            return attachment.LowQualityCopyOfAttachmentID is null || _shownCopies.Contains(attachment.AttachmentID);
        }

        /// <summary>How many hidden low-quality copies the attachment <paramref name="attachmentID"/> has.</summary>
        public int HiddenCopiesOf(long attachmentID) => _hiddenCopies.GetValueOrDefault(attachmentID);
    }
}
