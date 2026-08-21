using System.Numerics;
using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// One <c>(scope key, local date)</c> pair's own gate figures, keyed by a deterministic anonymised
    /// identifier rather than by its date.
    /// </summary>
    /// <remarks>
    /// Handed to an optional sink, and never part of the census the gate returns. Pair-level data is
    /// what a freeze review may need to reproduce a figure, and it is also what permanent evidence must
    /// not carry: message length beside a composition beside a run count pins a short direction pattern
    /// down exactly, and beside a real date that pattern becomes attributable.
    /// <para>
    /// <see cref="PairID"/> is a positional index over the gate's own canonical ordering — scope, then
    /// scope key, then date ascending. It reproduces across reruns of the same workspace, which keeps a
    /// review auditable, and it is not derivable back to a date. A hash of the date would not satisfy
    /// the second of those.
    /// </para>
    /// <para>
    /// No raw direction string appears here, in either direction. The shape fields are what the
    /// reference depends on; the sequences themselves stay in memory.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequencePairRow
    {
        /// <summary>The anonymised identifier, unique across the census and stable across reruns.</summary>
        public required int PairID { get; init; }

        /// <summary>Which scope this pair belongs to.</summary>
        public required ScopeLevel Scope { get; init; }

        /// <summary>The scope key — a source or a device group — as a stable identifier.</summary>
        public required long ScopeKeyID { get; init; }

        /// <summary>How far the pair got.</summary>
        public required DirectionSequencePairState State { get; init; }

        /// <summary>Message symbols on the date.</summary>
        public required int MessageSymbolCount { get; init; }

        /// <summary>How many of them are outgoing.</summary>
        public required int MessageOutgoingCount { get; init; }

        /// <summary>How many are incoming.</summary>
        public required int MessageIncomingCount { get; init; }

        /// <summary>Direction runs in the message pattern.</summary>
        public required int MessageRunCount { get; init; }

        /// <summary>Direction transitions in the message pattern.</summary>
        public required int MessageTransitionCount { get; init; }

        /// <summary>Emitted token positions after collapse.</summary>
        public required int TokenPositionCount { get; init; }

        /// <summary>How many of them are outgoing.</summary>
        public required int TokenOutgoingCount { get; init; }

        /// <summary>How many are incoming.</summary>
        public required int TokenIncomingCount { get; init; }

        /// <summary>Direction runs in the emitted token sequence.</summary>
        public required int TokenRunCount { get; init; }

        /// <summary>Direction-labelled physical observations before collapse, outgoing.</summary>
        public required int TokenOutgoingCountBeforeCollapse { get; init; }

        /// <summary>Direction-labelled physical observations before collapse, incoming.</summary>
        public required int TokenIncomingCountBeforeCollapse { get; init; }

        /// <summary>Outgoing message symbols the token side cannot supply.</summary>
        public required int OutgoingShortfall { get; init; }

        /// <summary>Incoming message symbols the token side cannot supply.</summary>
        public required int IncomingShortfall { get; init; }

        /// <summary><c>A</c>, or zero where no reference was computed.</summary>
        public required BigInteger ArrangementCount { get; init; }

        /// <summary><c>S</c>, or zero where no reference was computed.</summary>
        public required BigInteger AdmittingArrangementCount { get; init; }

        /// <summary><c>P</c>, or zero where no reference was computed.</summary>
        public required BigInteger EmbeddingPairCount { get; init; }

        /// <summary>
        /// <c>Q</c>, or zero where no reference was computed.
        /// </summary>
        /// <remarks>
        /// Present so the <c>A * Q = P * P</c> classification can be reproduced from a retained row, and
        /// for no other reason. It is not a variance, a dispersion or a confidence quantity, and no
        /// figure derived from it belongs in a result.
        /// </remarks>
        public required BigInteger SquaredEmbeddingCount { get; init; }

        /// <summary><c>q_r</c> as a decimal reading, or zero where none was computed.</summary>
        public required double ConditionalAdmissionProbability { get; init; }

        /// <summary><c>E_r[share]</c> as a decimal reading, or zero where none was computed.</summary>
        public required double ExpectedEmbeddingShare { get; init; }

        /// <summary>
        /// The composition-only <c>q(p, o, i)</c> as a decimal reading, for the burstiness comparison.
        /// </summary>
        public required double ExchangeableAdmissionProbability { get; init; }

        /// <summary>Whether the conditioning data fixes this pair's binary admission.</summary>
        public required DirectionSequenceDeterminacyClass BinaryClass { get; init; }

        /// <summary>Whether it fixes this pair's normalised embedding share.</summary>
        public required DirectionSequenceDeterminacyClass GradedClass { get; init; }
    }
}
