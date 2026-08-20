namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C8 — whether a token position holding several compatible assets enters the analysed population.
    /// </summary>
    /// <remarks>
    /// Stage B1 found exactly one archive-wide <c>(date, token)</c> position mapping to several assets.
    /// It enters a group here only if both of its assets are compatible for that group's date and
    /// direction; otherwise the analysed weight is one. So a zero in this section is not a contradiction
    /// of Stage B1.
    /// <para>
    /// The collision is represented, never broken by a tie rule on source, extension, file name, device
    /// or path.
    /// </para>
    /// </remarks>
    public sealed class CollisionParticipationCensus
    {
        public required int TokenPositionsWithSeveralCompatibleAssets { get; init; }

        public required int GroupsContainingSuchAPosition { get; init; }

        /// <summary>Messages whose valid token range includes at least one such position.</summary>
        public required int MessagesWhoseRangeIncludesSuchAPosition { get; init; }
    }
}
