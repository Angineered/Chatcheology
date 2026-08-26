namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The complete set of rows one run will publish, built entirely in memory before any of it is
    /// written.
    /// </summary>
    /// <remarks>
    /// Buffered rather than streamed into the database for one mechanical reason: the publication
    /// transaction holds SQLite's writer reservation across the whole read phase, and a transaction
    /// that has modified pages can need the <c>EXCLUSIVE</c> upgrade at any point its page cache
    /// spills — which a streaming reader's <c>SHARED</c> lock would refuse. So no statement may modify
    /// a page until every read connection is closed, which means the rows have to exist somewhere
    /// first.
    /// <para>
    /// Bounded: order 10^5 candidate rows and a small multiple of that in occurrence rows, all value
    /// types, for a total in the tens of megabytes — smaller than the media index the frozen analysis
    /// builds twice on its own account.
    /// </para>
    /// </remarks>
    internal sealed class Phase7EvidencePayload
    {
        /// <summary>One row per in-scope attachment, in the frozen analysis order.</summary>
        internal required IReadOnlyList<Phase7SnapshotRow> SnapshotRows { get; init; }

        /// <summary>
        /// The exact-date candidate rows, in attachment order and then <c>MediaAssetID</c> order.
        /// </summary>
        internal required IReadOnlyList<Phase7CandidateRow> CandidateRows { get; init; }

        /// <summary>
        /// The occurrence rows, each naming its parent's index in <see cref="CandidateRows"/>.
        /// </summary>
        internal required IReadOnlyList<Phase7OccurrenceRow> OccurrenceRows { get; init; }

        /// <summary>The adjacent-date context rows.</summary>
        internal required IReadOnlyList<Phase7AdjacentContextRow> AdjacentContextRows { get; init; }

        /// <summary>
        /// How many attachments the frozen analysis emitted for the requested conversation.
        /// </summary>
        /// <remarks>
        /// Equal to <see cref="SnapshotRows"/>' count by construction; reconciliation proves it again
        /// from the database and from an independent count of the conversation's unresolved
        /// attachments, so one number has two derivations that must agree.
        /// </remarks>
        internal required int InScopeAttachmentCount { get; init; }

        /// <summary>
        /// Whether the second read pass agreed with the first about attachment order, total and
        /// per-attachment counts.
        /// </summary>
        /// <remarks>
        /// Always true on a payload that was built at all — the builder throws rather than recording
        /// a disagreement — so this exists to make reconciliation's determinism check a named,
        /// visible step rather than an implicit one.
        /// </remarks>
        internal required bool TwoPassAgreementVerified { get; init; }
    }
}
