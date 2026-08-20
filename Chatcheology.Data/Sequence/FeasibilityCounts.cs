namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C1 — feasibility under the sequence-order hypothesis, which is arithmetic on the slack.
    /// </summary>
    /// <remarks>
    /// A strictly increasing token-position assignment exists if and only if <c>T &gt;= M</c>, because
    /// every message in one group draws on the identical candidate occurrence pool and every token
    /// position holds at least one asset. The classes below are mutually exclusive, in the order
    /// declared.
    /// </remarks>
    public sealed class FeasibilityCounts
    {
        /// <summary>No frozen compatible candidate asset at all.</summary>
        public required int NoCompatibleCandidateAssetGroups { get; init; }

        /// <summary>Compatible assets, but none of their copies carries a supported token.</summary>
        public required int NoSupportedTokenPositionGroups { get; init; }

        /// <summary>Some token positions, but fewer than the group has messages.</summary>
        public required int TooFewTokenPositionGroups { get; init; }

        /// <summary>At least one token position per message.</summary>
        public required int EnoughTokenPositionGroups { get; init; }

        public required int MessagesInNoCompatibleCandidateAssetGroups { get; init; }

        public required int MessagesInNoSupportedTokenPositionGroups { get; init; }

        public required int MessagesInTooFewTokenPositionGroups { get; init; }

        public required int MessagesInEnoughTokenPositionGroups { get; init; }

        public int GroupTotal =>
            NoCompatibleCandidateAssetGroups + NoSupportedTokenPositionGroups
            + TooFewTokenPositionGroups + EnoughTokenPositionGroups;

        public int MessageTotal =>
            MessagesInNoCompatibleCandidateAssetGroups + MessagesInNoSupportedTokenPositionGroups
            + MessagesInTooFewTokenPositionGroups + MessagesInEnoughTokenPositionGroups;
    }
}
