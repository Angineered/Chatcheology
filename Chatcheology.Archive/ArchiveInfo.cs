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

            /// <summary>
            /// Format version 4 (CH-71): the archive's fixed id, made once for a workspace and kept for every later
            /// export of it, so a phone can tell a refreshed archive from a different one. Required from version 4;
            /// not in <see cref="All"/>, which lists the keys every version has.
            /// </summary>
            public const string ArchiveId = "ArchiveId";

            /// <summary>Every key every version has, in the order the writer stores them.</summary>
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

        /// <summary>
        /// Format version 2: the period of each shown older export, in timeline order, stored as one
        /// info row each (<c>ExportPeriod.1</c>, <c>ExportPeriod.2</c>, ...). Period only: no file name.
        /// Empty for a version 1 archive and for an archive without older exports.
        /// </summary>
        public IReadOnlyList<ArchiveExportPeriod> ExportPeriods { get; init; } = [];

        /// <summary>
        /// Format version 4 (CH-71): the archive's fixed id (<see cref="ArchiveFormat.IsArchiveId"/>), the same for
        /// every export of one workspace. Required from version 4 (the writer refuses to write and the reader refuses
        /// to open a version 4 archive without a valid one); <see langword="null"/> below version 4.
        /// </summary>
        public string? ArchiveId { get; init; }

        /// <summary>The info key of the <paramref name="position"/>th (1-based) export period.</summary>
        public static string ExportPeriodKey(int position) =>
            ExportPeriodKeyPrefix + position.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The prefix of every export period key.</summary>
        public const string ExportPeriodKeyPrefix = "ExportPeriod.";
    }

    /// <summary>
    /// The shown part of one older export: its first and last shown message times, local wall clock.
    /// Stored as <c>first/last</c> in the archive's local date-time form.
    /// </summary>
    /// <param name="FirstLocal">The first shown message's local time.</param>
    /// <param name="LastLocal">The last shown message's local time.</param>
    public sealed record ArchiveExportPeriod(DateTime FirstLocal, DateTime LastLocal)
    {
        /// <summary>The stored form.</summary>
        public string Format() => ArchiveFormat.FormatLocal(FirstLocal) + "/" + ArchiveFormat.FormatLocal(LastLocal);

        /// <summary>Reads the stored form back.</summary>
        /// <exception cref="ArchiveFormatException">Not in the stored form.</exception>
        public static ArchiveExportPeriod Parse(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            var parts = text.Split('/');

            if (parts.Length != 2)
            {
                throw new ArchiveFormatException("An export period in the archive's info table is not in its stored form.");
            }

            var first = ArchiveFormat.ParseLocal(parts[0]);
            var last = ArchiveFormat.ParseLocal(parts[1]);

            return last < first
                ? throw new ArchiveFormatException("An export period in the archive's info table ends before it starts.")
                : new ArchiveExportPeriod(first, last);
        }
    }
}
