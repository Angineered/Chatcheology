namespace Chatcheology.Archive
{
    /// <summary>A run of messages in chat order, with the media their attachments name.</summary>
    public sealed class ArchivePage
    {
        /// <summary>The messages, ascending by sequence number.</summary>
        public required IReadOnlyList<ArchiveMessage> Messages { get; init; }

        /// <summary>Every media payload a restored attachment on the page names, by SHA-256.</summary>
        public required IReadOnlyDictionary<string, ArchiveMedia> Media { get; init; }

        /// <summary>The first message's sequence number, or <see langword="null"/> for an empty page.</summary>
        public required int? FirstSequenceNumber { get; init; }

        /// <summary>The last message's sequence number, or <see langword="null"/> for an empty page.</summary>
        public required int? LastSequenceNumber { get; init; }

        /// <summary>Whether the archive holds messages before this page.</summary>
        public required bool HasEarlier { get; init; }

        /// <summary>Whether the archive holds messages after this page.</summary>
        public required bool HasLater { get; init; }
    }
}
