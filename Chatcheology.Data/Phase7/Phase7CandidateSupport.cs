namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// One candidate reduced to what the frozen comparison rule is allowed to read: its asset, and its
    /// reading on each of the two dimensions.
    /// </summary>
    /// <remarks>
    /// <strong>Exactly three properties, and that is the point.</strong> Token uniqueness, the
    /// remaining-position count, the group-shape flags, adjacency, copy counts, source counts, media
    /// type and size cannot influence ranking because they are not here to be read. A fourth property
    /// would be a new evidence dimension, which is a reviewed design change.
    /// <para>
    /// Nothing here is persisted. There is no rank column, no score column, no weight and no threshold
    /// anywhere in the schema, and this reduction runs at read time over stored facts.
    /// </para>
    /// </remarks>
    public sealed class Phase7CandidateSupport
    {
        /// <summary>The candidate payload.</summary>
        public required long MediaAssetID { get; init; }

        /// <summary>
        /// Its direction reading. Available and positive only for <c>Compatible</c>, and — under the
        /// frozen value set — never able to produce a strict preference.
        /// </summary>
        public required Phase7DirectionSupport Direction { get; init; }

        /// <summary>
        /// Its occurrence-coherent order reading: unanimous across its assessable occurrences, or
        /// unavailable.
        /// </summary>
        public required Phase7OrderSupport OrderSupport { get; init; }
    }
}
