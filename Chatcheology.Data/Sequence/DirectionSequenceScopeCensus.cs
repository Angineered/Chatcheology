using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Everything the gate measured at one numbering scope.
    /// </summary>
    /// <remarks>
    /// One of these per scope, never merged. The same date observed at source scope and at
    /// device-group scope is largely the same physical evidence, and shared <c>(date, token)</c>
    /// positions across sources are the same files, so summing across scopes would manufacture weight
    /// from duplicated evidence.
    /// <para>
    /// Which scope a later primary aggregate uses is a decision frozen at the gate review, and the gate
    /// deliberately does not make it: it reports both so the choice can be made from evidence. Every
    /// eventual primary aggregate must use exactly one scope per date.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceScopeCensus
    {
        /// <summary>The scope described.</summary>
        public required ScopeLevel Scope { get; init; }

        /// <summary>C0 — the pair universe and where each pair stopped.</summary>
        public required DirectionSequencePairPopulation PairPopulation { get; init; }

        /// <summary>C0 — how foreign-dominated the emitted token streams are.</summary>
        public required DirectionSequenceDilutionContext Dilution { get; init; }

        /// <summary>C0b — supply adequacy, before and after collapse.</summary>
        public required DirectionSequenceSupplyCensus Supply { get; init; }

        /// <summary>C0b — burstiness diagnostics over the classified pairs.</summary>
        public required DirectionSequenceBurstinessCensus Burstiness { get; init; }

        /// <summary>C0b — the exact reference expectations the classified population carries.</summary>
        public required DirectionSequenceReferenceCensus Reference { get; init; }

        /// <summary>C0b — the two determinacy classifications and their cross-tabulation.</summary>
        public required DirectionSequenceDeterminacyCensus Determinacy { get; init; }
    }
}
