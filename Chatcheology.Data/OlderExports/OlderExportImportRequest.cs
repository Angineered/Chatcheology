namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// Everything one older-export import needs: the export, the role of each of its two senders,
    /// where it sits in the timeline, and whether it is shown.
    /// </summary>
    public sealed class OlderExportImportRequest
    {
        /// <summary>The export, read from its folder by <see cref="OlderExportSource.Load"/>.</summary>
        public required OlderExportSource Export { get; init; }

        /// <summary>
        /// The role of each sender name, keyed by the name exactly as the export writes it
        /// (compared ordinally). Must name exactly the export's two senders, one
        /// <see cref="OlderExportRole.Local"/> and one <see cref="OlderExportRole.Other"/>.
        /// </summary>
        public required IReadOnlyDictionary<string, OlderExportRole> Roles { get; init; }

        /// <summary>
        /// The export's place in the timeline of older exports, 1-based and unique in the file.
        /// </summary>
        public required int Position { get; init; }

        /// <summary>
        /// Whether the export is shown. An export kept only as evidence (one wholly inside another,
        /// for example) is registered as not shown; only shown exports may not overlap in time.
        /// </summary>
        public required bool Shown { get; init; }

        /// <summary>
        /// For the one export configured as the superset of another: the other export's folder, in
        /// which a <c>(file attached)</c> name that is absent from this export's own folder is also
        /// looked for, by exact name. Null for every other export.
        /// </summary>
        public string? FallbackMediaFolder { get; init; }

        /// <summary>
        /// The revised D2 cut: the inner export this one contains, already imported and shown.
        /// When set, this export must be shown, and it is shown only from the message after the
        /// one line that exactly matches the inner export's last message (see
        /// <see cref="OlderExportCut"/>). Its position must be later than the inner's, and no shown
        /// line may exactly duplicate an inner line. Null for every export that is shown whole or
        /// not at all.
        /// </summary>
        public OlderExportSource? ShownAfter { get; init; }

        /// <summary>When the import is performed. Must be <see cref="DateTimeKind.Utc"/>.</summary>
        public required DateTime ImportedDateTimeUtc { get; init; }

        /// <summary>
        /// Optional timezone context for the export's wall-clock timestamps, stored as given and
        /// never applied.
        /// </summary>
        public string? SourceTimeZoneID { get; init; }
    }
}
