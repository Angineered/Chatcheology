using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C2 — how many token positions each message could occupy under the hypothesis.
    /// </summary>
    /// <remarks>
    /// For a feasible group the <c>r</c>th message in source order can occupy token indexes
    /// <c>r .. T - M + r</c>, so <c>PossibleTokenPositionCount = T - M + 1</c> for every message in the
    /// group. It is a per-group quantity; the message bands exist because the population being described
    /// is messages.
    /// <para>
    /// One possible position forces a token position, not a candidate asset: a position can hold several
    /// assets.
    /// </para>
    /// </remarks>
    public sealed class TokenPositionAmbiguityCensus
    {
        /// <summary>Possible token positions per feasible group, as a spread and in bands.</summary>
        public required CountSummary PossibleTokenPositionCountPerGroup { get; init; }

        /// <summary>Messages banded by their group's possible token position count.</summary>
        public required SequenceBandCounts MessagesByPossibleTokenPositionCount { get; init; }
    }
}
