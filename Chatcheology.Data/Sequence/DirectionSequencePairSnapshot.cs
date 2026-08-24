using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// One <c>(scope key, local date)</c> pair exactly as the accepted B2C-0 gate built it: both
    /// direction streams, the coverage counts behind them, and the gate's own pair row.
    /// </summary>
    /// <remarks>
    /// The B2C-1 alignment stage needs the two streams the gate already constructed, and it must not
    /// construct them a second time: a second reader of the same tables would be a second grammar, a
    /// second collapse rule and a second ordering, all of which could drift from the frozen ones while
    /// looking identical. This carries them out of the gate instead.
    /// <para>
    /// <see cref="Row"/> is the authority on what the pair <em>is</em>. Exclusion, supply, degeneracy
    /// and both determinacy classifications are decided by the gate's accumulator after the streams are
    /// built, so a consumer that judged eligibility from the streams alone would readmit pairs the gate
    /// excluded — a conflicting pair still emits symbols for its non-conflicting positions. It is null
    /// exactly when the date carries no message symbol, which is how a token-only neighbour date reaches
    /// an adjacent-date control.
    /// </para>
    /// <para>
    /// <b>Memory only.</b> The streams here are the private direction patterns, and
    /// <see cref="Date"/> is a real local date; the combination is precisely what permanent evidence
    /// must never carry. Nothing retains a snapshot beyond the run that produced it, no census or report
    /// type holds one, and pair-level output keys on the row's anonymised
    /// <see cref="DirectionSequencePairRow.PairID"/> rather than on this.
    /// </para>
    /// <para>
    /// The two stream arrays are copies. The gate caches its message patterns and shares its logical
    /// positions between scopes, so handing out the originals would let a consumer's mistake reach the
    /// gate's own figures.
    /// </para>
    /// </remarks>
    internal sealed class DirectionSequencePairSnapshot
    {
        /// <summary>The numbering scope this pair belongs to.</summary>
        internal required ScopeLevel Scope { get; init; }

        /// <summary>The scope key — a source or a device group.</summary>
        internal required long ScopeKey { get; init; }

        /// <summary>The local date. Memory only; never persisted beside shape data.</summary>
        internal required DateOnly Date { get; init; }

        /// <summary>
        /// The message-side stream, one symbol per unresolved attachment, ordered by
        /// <c>Message.SequenceNumber</c> then <c>Attachment.Ordinal</c>. Empty where the date carries
        /// no attachment.
        /// </summary>
        internal required bool[] Pattern { get; init; }

        /// <summary>
        /// The token-side stream after collapse, ordered by the supported four-digit token value.
        /// </summary>
        internal required bool[] EmittedTokens { get; init; }

        /// <summary>Logical positions whose copies disagreed about direction, so the pair is excluded.</summary>
        internal required int ConflictingPositionCount { get; init; }

        /// <summary>
        /// Collapsed positions no copy of which records direction, which is this pair's
        /// direction-coverage loss.
        /// </summary>
        internal required int UnlabelledPositionCount { get; init; }

        /// <summary>
        /// Dated, marker-agreeing observations at this <c>(scope key, date)</c> that carry no supported
        /// four-digit token, which is this pair's grammar-coverage loss.
        /// </summary>
        /// <remarks>
        /// Observations, not positions: a name that fails the grammar never becomes a logical position
        /// at all. Undated observations are absent by construction — they carry no date to attribute a
        /// failure to — and a marker disagreeing with the persisted date aborts the gate rather than
        /// being counted here.
        /// </remarks>
        internal required int UnsupportedTokenObservationCount { get; init; }

        /// <summary>Direction-labelled physical observations before collapse, outgoing.</summary>
        internal required int OutgoingCopiesBeforeCollapse { get; init; }

        /// <summary>Direction-labelled physical observations before collapse, incoming.</summary>
        internal required int IncomingCopiesBeforeCollapse { get; init; }

        /// <summary>
        /// The gate's own row for this pair, or <see langword="null"/> where the date carries no
        /// message symbol.
        /// </summary>
        internal required DirectionSequencePairRow? Row { get; init; }
    }
}
