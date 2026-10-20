namespace Chatcheology.Archive
{
    /// <summary>
    /// What the archive says about itself: the <c>ArchiveInfo</c> table, one key per member.
    /// </summary>
    public sealed class ArchiveInfo
    {
        /// <summary>The info keys, exactly as stored.</summary>
        public static class Keys
        {
            /// <summary>The format version, the same number as <c>PRAGMA user_version</c>.</summary>
            public const string FormatVersion = "FormatVersion";

            /// <summary>When the archive was written.</summary>
            public const string CreatedUtc = "CreatedUtc";

            /// <summary>What wrote it.</summary>
            public const string ProducerVersion = "ProducerVersion";

            /// <summary>The conversation's title.</summary>
            public const string ConversationTitle = "ConversationTitle";

            /// <summary>The SHA-256 of the chat export the messages came from.</summary>
            public const string SourceExportSha256 = "SourceExportSha256";

            /// <summary>The import's source time zone.</summary>
            public const string TimeZoneID = "TimeZoneID";

            /// <summary>Whose side of the conversation is "me".</summary>
            public const string LocalParticipantID = "LocalParticipantID";

            /// <summary>Every key, in the order the writer stores them.</summary>
            public static IReadOnlyList<string> All { get; } =
            [
                FormatVersion,
                CreatedUtc,
                ProducerVersion,
                ConversationTitle,
                SourceExportSha256,
                TimeZoneID,
                LocalParticipantID,
            ];
        }

        /// <summary>The format version the archive was written in.</summary>
        public required int FormatVersion { get; init; }

        /// <summary>When the archive was written, in UTC, to the second.</summary>
        public required DateTime CreatedUtc { get; init; }

        /// <summary>What wrote it, such as a program name and version.</summary>
        public required string ProducerVersion { get; init; }

        /// <summary>The conversation's title.</summary>
        public required string ConversationTitle { get; init; }

        /// <summary>
        /// The SHA-256 of the chat export the messages came from, lower-case hex, or empty when the
        /// producer had none recorded.
        /// </summary>
        public required string SourceExportSha256 { get; init; }

        /// <summary>The import's source time zone identifier, or empty when none was recorded.</summary>
        public required string TimeZoneID { get; init; }

        /// <summary>The participant whose side of the conversation is "me".</summary>
        public required long LocalParticipantID { get; init; }
    }
}
