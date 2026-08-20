namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The slack-zero population: groups where the hypothesis forces one token position per message.
    /// </summary>
    /// <remarks>
    /// Reported inside the slack section rather than as a section of its own, because
    /// <c>SequenceSlack == 0</c> and "every message has one forced token position" are the same
    /// statement. A forced position is not a forced candidate: one token position can hold several
    /// assets.
    /// </remarks>
    public sealed class ForcedPositionCounts
    {
        public required int Groups { get; init; }

        public required int Messages { get; init; }

        /// <summary>Forced-position messages whose token position holds exactly one asset.</summary>
        public required int MessagesWhereTokenHoldsOneAsset { get; init; }

        /// <summary>Forced-position messages whose token position holds several.</summary>
        public required int MessagesWhereTokenHoldsSeveralAssets { get; init; }
    }
}
