using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The exact-date and adjacent-date bands stay separate, and the weaker one is never promoted into
    /// the stronger one however empty the stronger one is.
    /// </summary>
    /// <remarks>
    /// <c>AttachmentCandidate</c> holds exact-date relations only. There is no candidate-band column to
    /// set and no column anywhere on that table that could record a weaker band, so an
    /// adjacent-date-only attachment has zero candidate rows and stays unresolved with no path by which
    /// its weaker context could be promoted.
    /// </remarks>
    public class Phase7CandidateBandTests
    {
        [Fact]
        public void Run_AdjacentDateOnlyAttachment_ProducesNoAttachmentCandidateRow()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            var asset = fixture.AddTokenAsset(date.AddDays(-1), isSent: true, 1);

            var result = fixture.Run();

            Assert.Equal(0, result.CandidateRowCount);
            Assert.Equal(0, result.OccurrenceRowCount);
            Assert.Equal(1, result.AdjacentDateContextRowCount);

            Assert.Equal("0,1,0,0,0,0,<null>", fixture.Snapshot(result.RunID, attachment));

            Assert.Equal(
                [$"{asset},-1"],
                fixture.Rows(
                    $"SELECT MediaAssetID, AdjacentOffsetDays FROM AttachmentAdjacentDateContext " +
                    $"WHERE RunID = {result.RunID} AND AttachmentID = {attachment};"));
        }

        /// <remarks>
        /// An asset supported on both adjacent days yields two rows, because each day is an independent
        /// qualifying reason the asset is there at all — which is exactly what the frozen
        /// <c>UNIQUE (RunID, AttachmentID, MediaAssetID, AdjacentOffsetDays)</c> is shaped for.
        /// </remarks>
        [Fact]
        public void Run_AdjacentDateContext_IsPersistedWithItsOffsetOnly()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            var previousOnly = fixture.AddTokenAsset(date.AddDays(-1), isSent: true, 1);
            var nextOnly = fixture.AddTokenAsset(date.AddDays(1), isSent: true, 2);

            var both = fixture.AddTokenAssetOnDates(
                isSent: true, (date.AddDays(-1), 3), (date.AddDays(1), 4));

            var result = fixture.Run();

            Assert.Equal(0, result.CandidateRowCount);
            Assert.Equal(4, result.AdjacentDateContextRowCount);

            Assert.Equal(
                [
                    $"{previousOnly},-1",
                    $"{nextOnly},1",
                    $"{both},-1",
                    $"{both},1",
                ],
                fixture.Rows(
                    $"SELECT MediaAssetID, AdjacentOffsetDays FROM AttachmentAdjacentDateContext " +
                    $"WHERE RunID = {result.RunID} AND AttachmentID = {attachment} " +
                    $"ORDER BY MediaAssetID, AdjacentOffsetDays;"));

            Assert.Equal("0,4,0,0,0,0,<null>", fixture.Snapshot(result.RunID, attachment));
        }

        /// <remarks>
        /// Not a one-candidate set. An attachment whose only evidence is adjacent-date context has
        /// nothing to rank, which is the honest answer and the one the closure record puts in the
        /// must-remain-unresolved list.
        /// </remarks>
        [Fact]
        public void Run_AdjacentDateOnlyAttachment_RanksAsAnEmptyCandidateSet()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date.AddDays(1), isSent: true, 1);

            var result = fixture.Run();

            Assert.Empty(fixture.Support(result.RunID, attachment));

            var ranked = fixture.Rank(result.RunID, attachment);

            Assert.Empty(ranked.Tiers);
            Assert.Equal(0, ranked.CandidateCount);

            Assert.Equal(
                1,
                fixture.ScalarLong(
                    $"SELECT COUNT(*) FROM Attachment " +
                    $"WHERE AttachmentID = {attachment} AND ResolutionStatus = 'Unresolved' " +
                    $"AND ResolvedMediaAssetID IS NULL;"));
        }

        [Fact]
        public void Run_ExactAndAdjacentBands_AreDisjointPerAttachment()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            // One asset with copies on the message date and on both neighbouring days: exact-date
            // candidacy wins outright, and it never appears as adjacent context as well.
            fixture.AddTokenAssetOnDates(
                isSent: true, (date.AddDays(-1), 1), (date, 2), (date.AddDays(1), 3));

            var result = fixture.Run();

            Assert.Equal(1, result.CandidateRowCount);
            Assert.Equal(0, result.AdjacentDateContextRowCount);

            Assert.Equal(
                0,
                fixture.ScalarLong(
                    $"""
                    SELECT COUNT(*)
                    FROM AttachmentCandidate AS c
                    INNER JOIN AttachmentAdjacentDateContext AS x
                        ON x.RunID = c.RunID
                       AND x.AttachmentID = c.AttachmentID
                       AND x.MediaAssetID = c.MediaAssetID
                    WHERE c.RunID = {result.RunID};
                    """));

            Assert.Equal("1,0,1,0,0,1,1", fixture.Snapshot(result.RunID, attachment));
        }

        /// <remarks>
        /// Adjacent context has no occurrence children at all, and that is a schema-level guarantee
        /// rather than a convention: occurrence rows reference a candidate, and adjacent context is not
        /// a candidate.
        /// </remarks>
        [Fact]
        public void Run_AdjacentDateContext_HasNoOccurrenceChildren()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date.AddDays(-1), isSent: true, 1);

            var result = fixture.Run();

            Assert.Equal(1, result.AdjacentDateContextRowCount);
            Assert.Equal(0, result.OccurrenceRowCount);

            Assert.Equal(
                0,
                fixture.ScalarLong("SELECT COUNT(*) FROM AttachmentCandidateOccurrenceEvidence;"));
        }

        /// <remarks>
        /// Adding adjacent context to an attachment that already has exact-date candidates changes no
        /// tier and no support value, which is what makes "the ranking ignores it entirely" a
        /// behavioural fact as well as a structural one.
        /// </remarks>
        [Fact]
        public void Ranking_IgnoresAdjacentDateContextEntirely()
        {
            static string Describe(Phase7RankedCandidateSet ranked) =>
                string.Join(
                    "|",
                    ranked.Tiers.Select(
                        tier => string.Join(
                            ",",
                            tier.Select(
                                candidate =>
                                    $"{candidate.MediaAssetID}:{candidate.Direction}:" +
                                    $"{candidate.OrderSupport}"))));

            using var withoutContext = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (plainAttachments, _) =
                withoutContext.AddOutgoingGroup(date, memberCount: 2, positionCount: 3);

            var plainResult = withoutContext.Run();
            var plain = Describe(withoutContext.Rank(plainResult.RunID, plainAttachments[0]));

            using var withContext = new Phase7TestFixture();

            var (contextAttachments, _) =
                withContext.AddOutgoingGroup(date, memberCount: 2, positionCount: 3);

            // The same evidence, plus adjacent-date context on both neighbouring days.
            withContext.AddTokenAsset(date.AddDays(-1), isSent: true, 50);
            withContext.AddTokenAsset(date.AddDays(1), isSent: true, 51);

            var contextResult = withContext.Run();

            Assert.True(contextResult.AdjacentDateContextRowCount > 0);

            Assert.Equal(
                plain, Describe(withContext.Rank(contextResult.RunID, contextAttachments[0])));
        }
    }
}
