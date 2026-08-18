namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Everything registering one media source needs: which directory it is, and what to record
    /// about it.
    /// </summary>
    public sealed class MediaSourceRequest
    {
        /// <summary>A human-readable name for this source, shown to the user.</summary>
        public required string DisplayName { get; init; }

        /// <summary>
        /// What kind of media tree this is, in the caller's own vocabulary — for example
        /// <see cref="MediaSourceTypes.WhatsAppMediaDirectory"/>.
        /// </summary>
        /// <remarks>
        /// Deliberately not constrained to a fixed set in the database, so a new kind of source
        /// needs no schema change. A value this build recognises additionally unlocks the naming
        /// conventions of that layout; any other value is inventoried in full, simply without them.
        /// </remarks>
        public required string SourceType { get; init; }

        /// <summary>
        /// The physical directory to inventory. Read-only throughout, and stored so the files
        /// recorded beneath it can be found again.
        /// </summary>
        public required string RootPath { get; init; }

        /// <summary>
        /// Optional free text describing which device this media came from.
        /// </summary>
        /// <remarks>
        /// Stored exactly as supplied. Nothing parses it or matches on it.
        /// </remarks>
        public string? DeviceDescription { get; init; }

        /// <summary>
        /// When this source is being registered. Must be <see cref="DateTimeKind.Utc"/>.
        /// </summary>
        /// <remarks>
        /// Supplied by the caller rather than read from the clock inside the service, so one
        /// operation stamps every row it creates with a single value and tests need no clock
        /// abstraction. It says nothing about when any file was created or sent.
        /// </remarks>
        public required DateTime ImportedDateTimeUtc { get; init; }
    }
}
