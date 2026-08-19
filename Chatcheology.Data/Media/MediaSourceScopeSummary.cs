namespace Chatcheology.Data.Media
{
    /// <summary>What one acquisition source contributed, and which device group it was assigned to.</summary>
    /// <remarks>
    /// <see cref="SourceType"/> is reported rather than assumed. The committed classifier derives no date
    /// at all for a source that is not a WhatsApp media directory, so a source of another type would put
    /// all of its files among the undated for a reason that has nothing to do with how they are named, and
    /// a reader needs to see that before reading anything else.
    /// <para>
    /// <see cref="DeviceGroupID"/> is the caller's opaque identifier. No handset name, model or store
    /// label appears here.
    /// </para>
    /// </remarks>
    public sealed class MediaSourceScopeSummary
    {
        /// <summary>The source.</summary>
        public required long MediaSourceID { get; init; }

        /// <summary>Its recorded type.</summary>
        public required string SourceType { get; init; }

        /// <summary>Whether that type is one the committed classifier reads dates from.</summary>
        public required bool IsWhatsAppMediaDirectory { get; init; }

        /// <summary>The device group the caller assigned it to.</summary>
        public required long DeviceGroupID { get; init; }

        /// <summary>Physical files it holds.</summary>
        public required int MediaFileCount { get; init; }

        /// <summary>Of those, files carrying a committed <c>FileDate</c>.</summary>
        public required int DatedFileCount { get; init; }

        /// <summary>Of those, files carrying none.</summary>
        public required int UndatedFileCount { get; init; }

        /// <summary>Of those, files carrying supported sequence evidence.</summary>
        public required int SupportedFileCount { get; init; }
    }
}
