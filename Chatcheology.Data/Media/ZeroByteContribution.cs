namespace Chatcheology.Data.Media
{
    /// <summary>What the empty payload contributes to every figure it can distort.</summary>
    /// <remarks>
    /// Every file with no payload has the same hash, so they deduplicate to one asset standing behind
    /// however many physical copies the acquisition produced. That makes it the single largest
    /// one-payload cluster in an archive, and leaving it silently inside a collision or duplicate-copy
    /// conclusion would let it carry that conclusion.
    /// <para>
    /// It is <b>included</b> in the token curve and in the raw archive description, and said to be. The
    /// excluded variants exist alongside for every conclusion meant to inform later matching evidence.
    /// </para>
    /// </remarks>
    public sealed class ZeroByteContribution
    {
        /// <summary>Assets holding no payload.</summary>
        public required int ZeroByteAssetCount { get; init; }

        /// <summary>Physical files linked to them.</summary>
        public required int PhysicalFileCount { get; init; }

        /// <summary>Of those, files carrying supported sequence evidence.</summary>
        public required int SupportedFileCount { get; init; }

        /// <summary>Distinct recovered names those files carry, compared ordinally.</summary>
        public required int DistinctFileNameCount { get; init; }

        /// <summary>Distinct tokens they carry.</summary>
        public required int DistinctTokenCount { get; init; }

        /// <summary>Distinct calendar dates they carry.</summary>
        public required int DistinctFileDateCount { get; init; }

        /// <summary>
        /// Whether every supported observation is the same token, which is the reachable half of
        /// Stage A's three-way agreement outcome.
        /// </summary>
        /// <remarks>
        /// Null when there are fewer than two supported observations to agree or disagree about. Under a
        /// grammar fixed at four digits the third Stage A outcome, same value at a different width, is
        /// unreachable, so a boolean carries everything that remains.
        /// </remarks>
        public required bool? AllSupportedObservationsCarryTheSameToken { get; init; }
    }
}
