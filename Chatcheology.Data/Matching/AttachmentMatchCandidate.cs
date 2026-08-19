using Chatcheology.Data.Media;

namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// One media asset offered as a candidate for one attachment, with the evidence that put it
    /// there.
    /// </summary>
    /// <remarks>
    /// A candidate is not a match. Every field here is a fact about surviving files, and no field
    /// is a score, a rank or a judgement. Two candidates for the same attachment are not ordered by
    /// likelihood — they are ordered by <see cref="MediaAssetID"/> precisely so that nothing in the
    /// presentation can be read as a preference.
    /// <para>
    /// Identity is the asset, never the file. Three physical copies of one payload are one
    /// candidate; the copies show up as counts and evidence flags, not as three separate candidates
    /// and not as three votes.
    /// </para>
    /// <para>
    /// The properties divide into two kinds, and the difference matters. The asset-intrinsic ones
    /// describe the payload wherever it survives. The supporting ones describe only those copies
    /// whose <c>FileDate</c> placed this asset in this attachment's candidate set: a copy of the
    /// same payload sitting on an unrelated date says nothing about this relationship and is
    /// excluded from every <c>Supporting</c> field and from
    /// <see cref="DirectionCompatibility"/>.
    /// </para>
    /// </remarks>
    public sealed class AttachmentMatchCandidate
    {
        /// <summary>The candidate asset — one unique SHA-256 payload.</summary>
        public required long MediaAssetID { get; init; }

        /// <summary>
        /// The asset's recorded content kind. Descriptive only: the export does not say what type
        /// an omitted item was, so <c>Attachment.ExpectedMediaType</c> is null and nothing here
        /// filters by type.
        /// </summary>
        public required MediaType MediaType { get; init; }

        /// <summary>The asset's payload size. Zero-byte assets are never candidates.</summary>
        public required long SizeBytes { get; init; }

        /// <summary>How many physical files in the workspace carry this payload, in total.</summary>
        public required int PhysicalCopyCount { get; init; }

        /// <summary>How many distinct media sources hold a copy of this payload, in total.</summary>
        public required int DistinctMediaSourceCount { get; init; }

        /// <summary>
        /// Whether a supporting copy is dated to the message's own calendar date.
        /// </summary>
        public required bool HasExactMessageDateCopy { get; init; }

        /// <summary>Whether a supporting copy is dated to the day before the message.</summary>
        public required bool HasPreviousDayCopy { get; init; }

        /// <summary>Whether a supporting copy is dated to the day after the message.</summary>
        public required bool HasNextDayCopy { get; init; }

        /// <summary>
        /// How many physical copies actually supply this relationship's date evidence.
        /// </summary>
        /// <remarks>
        /// A count of facts, not of votes. A payload surviving in five places is not more likely to
        /// be this attachment's media than one surviving in one place, and nothing in this phase
        /// ranks by this number.
        /// </remarks>
        public required int SupportingPhysicalCopyCount { get; init; }

        /// <summary>
        /// How many distinct media sources supply this relationship's date evidence.
        /// </summary>
        public required int SupportingMediaSourceCount { get; init; }

        /// <summary>Whether a supporting copy was found beneath a <c>Sent</c> directory.</summary>
        public required bool HasSupportingSentFolderCopy { get; init; }

        /// <summary>
        /// Whether a supporting copy came from a source that has <c>Sent</c> structure and was not
        /// beneath it. Weaker than "received", and never treated as that.
        /// </summary>
        public required bool HasSupportingNotUnderSentFolderCopy { get; init; }

        /// <summary>
        /// Whether a supporting copy records no folder direction at all — the whole of the A54
        /// Legacy source, by design.
        /// </summary>
        public required bool HasSupportingDirectionUnknownCopy { get; init; }

        /// <summary>
        /// How the supporting copies' folder evidence sits against the message's direction.
        /// </summary>
        public required DirectionCompatibility DirectionCompatibility { get; init; }
    }
}
