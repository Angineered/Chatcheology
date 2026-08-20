namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C5 — whether outgoing and incoming occurrence tokens behave as though they were drawn on one
    /// within-date scale.
    /// </summary>
    /// <remarks>
    /// Reading one direction's token against the other's assumes a shared within-date ordinal scale.
    /// Stage B1 showed pooled <c>(date, token)</c> keys are almost perfectly asset-unique, which is
    /// consistent with one namespace but does not establish it, and the strict test rests entirely on
    /// cross-direction pairs. So the assumption is described here rather than asserted.
    /// <para>
    /// Descriptive only, and deliberately weak evidence: interleaving does not prove one counter,
    /// because names may have been preserved across acquisition paths. Nothing here creates or
    /// removes a candidate.
    /// </para>
    /// </remarks>
    public sealed class DirectionNamespaceDiagnostic
    {
        /// <summary>Occurrences in the diagnostic population.</summary>
        /// <remarks>
        /// Supported, known-direction occurrences on non-zero-byte assets. Stage B1's archive-wide
        /// supported total is higher: it includes unknown-direction occurrences and occurrences on the
        /// zero-byte asset. The two figures are not expected to agree.
        /// </remarks>
        public required int KnownDirectionSupportedOccurrenceCount { get; init; }

        /// <summary>Dates carrying such occurrences in both directions.</summary>
        public required int BothDirectionClassDateCount { get; init; }

        /// <summary>Distinct tokens on those dates seen only on sent-folder occurrences.</summary>
        public required int OutgoingOnlyTokenCount { get; init; }

        /// <summary>Distinct tokens seen only on not-under-sent occurrences.</summary>
        public required int IncomingOnlyTokenCount { get; init; }

        /// <summary>Distinct tokens seen in both classes on the same date.</summary>
        public required int BothClassTokenCount { get; init; }

        /// <summary>Dates carrying at least one token observed in both classes.</summary>
        public required int DatesContainingSharedToken { get; init; }

        /// <summary>At least one direction's distinct-token set holds exactly one token.</summary>
        public required int SingletonInvolvedDateCount { get; init; }

        /// <summary>Every outgoing token below every incoming one.</summary>
        public required int OutgoingRangeEntirelyBelowIncomingDateCount { get; init; }

        /// <summary>Every incoming token below every outgoing one.</summary>
        public required int IncomingRangeEntirelyBelowOutgoingDateCount { get; init; }

        /// <summary>Neither range sits wholly below the other.</summary>
        public required int OverlapOrInterleaveDateCount { get; init; }

        /// <summary>
        /// Direction-class changes per date, in bands, counted between adjacent distinct tokens where
        /// neither is shared between the classes. A shared token breaks the chain rather than
        /// contributing a change, because which class "came first" at one token is not observable.
        /// </summary>
        public required SequenceBandCounts TransitionCounts { get; init; }
    }
}
