using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// Every in-scope attachment gets exactly one snapshot row, whatever its evidence looks like.
    /// </summary>
    /// <remarks>
    /// The distinction the whole table exists for: <strong>absence of a row means the attachment was
    /// not evaluated; a zero count means it was evaluated and the answer was zero.</strong> Those are
    /// different facts and must not look alike, so every zero-result shape the archive really contains
    /// is exercised here.
    /// </remarks>
    public class Phase7SnapshotCoverageTests
    {
        [Fact]
        public void Run_AttachmentWithNoExactDateCandidate_StillProducesASnapshotRow()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Phase7TestFixture.FirstDate);

            var result = fixture.Run();

            Assert.Equal(1, result.SnapshotRowCount);
            Assert.Equal(0, result.CandidateRowCount);
            Assert.Equal(0, result.OccurrenceRowCount);
            Assert.Equal(0, result.AdjacentDateContextRowCount);

            Assert.Equal("0,0,0,0,0,0,<null>", fixture.Snapshot(result.RunID, attachment));
        }

        /// <remarks>
        /// Two shapes, both real: a senderless message keeps its direction unknown however the local
        /// participant is supplied, and a run given no local participant at all has every direction
        /// unknown. Neither is out of scope for the snapshot — only for the order model.
        /// </remarks>
        [Fact]
        public void Run_AttachmentWithNoUsableDirectionEvidence_StillProducesASnapshotRow()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var senderless = fixture.AddSenderless(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            var result = fixture.Run();

            // Evaluated, with its candidate persisted under the Unknown annotation, and out of the
            // group model rather than out of the run.
            Assert.Equal("1,0,0,0,0,0,<null>", fixture.Snapshot(result.RunID, senderless));

            Assert.Equal(
                "Unknown",
                fixture.ScalarText(
                    $"SELECT DirectionCompatibilityAnnotation FROM AttachmentCandidate " +
                    $"WHERE RunID = {result.RunID} AND AttachmentID = {senderless};"));
        }

        [Fact]
        public void Run_WithNoLocalParticipant_StillProducesASnapshotRowPerAttachment()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            var result = fixture.Run(localParticipantID: null);

            Assert.Null(result.LocalParticipantID);
            Assert.Equal("1,0,0,0,0,0,<null>", fixture.Snapshot(result.RunID, attachment));

            var support = Assert.Single(fixture.Support(result.RunID, attachment));

            Assert.Equal(Phase7DirectionSupport.Unavailable, support.Direction);
            Assert.Equal(Phase7OrderSupport.Unavailable, support.OrderSupport);
        }

        [Fact]
        public void Run_AttachmentWithNoUsableTokenEvidence_StillProducesASnapshotRow()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            var asset = fixture.AddNamedAsset(date, isSent: true, "000");

            var result = fixture.Run();

            // One candidate, one occurrence row, no token — and the group is impossible because its
            // compatible asset carries no supported token position.
            Assert.Equal("1,0,1,0,1,0,<null>", fixture.Snapshot(result.RunID, attachment));

            Assert.Equal(
                OrderConsistencyAnnotation.NotAssessable,
                fixture.Annotation(result.RunID, attachment, asset));
        }

        [Fact]
        public void Run_EveryInScopeAttachment_HasExactlyOneSnapshotRow()
        {
            using var fixture = new Phase7TestFixture();

            var noEvidence = fixture.AddOutgoing(Phase7TestFixture.Day(0));

            var adjacentOnly = fixture.AddOutgoing(Phase7TestFixture.Day(1));
            fixture.AddTokenAsset(Phase7TestFixture.Day(2), isSent: true, 5);

            var noDirection = fixture.AddSenderless(Phase7TestFixture.Day(4));
            fixture.AddTokenAsset(Phase7TestFixture.Day(4), isSent: true, 6);

            var noToken = fixture.AddOutgoing(Phase7TestFixture.Day(6));
            fixture.AddNamedAsset(Phase7TestFixture.Day(6), isSent: true, "abcd");

            var noneCompatible = fixture.AddOutgoing(Phase7TestFixture.Day(8));
            fixture.AddTokenAsset(Phase7TestFixture.Day(8), isSent: false, 7);

            var ordinary = fixture.AddOutgoing(Phase7TestFixture.Day(10));
            fixture.AddTokenAsset(Phase7TestFixture.Day(10), isSent: true, 8);

            var result = fixture.Run();

            Assert.Equal(6, result.SnapshotRowCount);

            long[] attachments =
                [noEvidence, adjacentOnly, noDirection, noToken, noneCompatible, ordinary];

            foreach (var attachment in attachments)
            {
                Assert.Equal(
                    1,
                    fixture.ScalarLong(
                        $"SELECT COUNT(*) FROM AttachmentEvidenceSnapshot " +
                        $"WHERE RunID = {result.RunID} AND AttachmentID = {attachment};"));
            }

            Assert.Equal(
                6,
                fixture.ScalarLong(
                    $"SELECT COUNT(DISTINCT AttachmentID) FROM AttachmentEvidenceSnapshot " +
                    $"WHERE RunID = {result.RunID};"));
        }

        [Fact]
        public void Run_SnapshotAndCandidateCounts_Reconcile()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 3);

            var adjacentOnly = fixture.AddOutgoing(Phase7TestFixture.Day(20));
            fixture.AddTokenAsset(Phase7TestFixture.Day(21), isSent: true, 4);

            var result = fixture.Run();

            Assert.Equal(
                result.SnapshotRowCount,
                fixture.ScalarLong(
                    $"SELECT COUNT(*) FROM AttachmentEvidenceSnapshot WHERE RunID = {result.RunID};"));

            Assert.Equal(
                result.CandidateRowCount,
                fixture.ScalarLong(
                    $"SELECT COALESCE(SUM(ExactDateCandidateCount), 0) " +
                    $"FROM AttachmentEvidenceSnapshot WHERE RunID = {result.RunID};"));

            Assert.Equal(
                result.AdjacentDateContextRowCount,
                fixture.ScalarLong(
                    $"SELECT COALESCE(SUM(AdjacentDateContextCount), 0) " +
                    $"FROM AttachmentEvidenceSnapshot WHERE RunID = {result.RunID};"));

            Assert.Equal("0,1,0,0,0,0,<null>", fixture.Snapshot(result.RunID, adjacentOnly));
        }

        /// <remarks>
        /// The 115-attachment shape from the closure record, in miniature: exact-date candidates exist
        /// but none is direction-compatible, so the group has no compatible asset at all and is
        /// recorded as impossible — unranked, and with the candidate still fully persisted.
        /// </remarks>
        [Fact]
        public void Run_AttachmentWithExactDateCandidatesButNoneCompatible_MarksTheGroupImpossible()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            var asset = fixture.AddTokenAsset(date, isSent: false, 3);

            var result = fixture.Run();

            Assert.Equal("1,0,0,1,1,0,<null>", fixture.Snapshot(result.RunID, attachment));

            Assert.Equal(
                "ContradictoryOnly",
                fixture.ScalarText(
                    $"SELECT DirectionCompatibilityAnnotation FROM AttachmentCandidate " +
                    $"WHERE RunID = {result.RunID} AND AttachmentID = {attachment};"));

            Assert.Equal(
                OrderConsistencyAnnotation.NotAssessable,
                fixture.Annotation(result.RunID, attachment, asset));
        }

        /// <remarks>
        /// Scope is the requested conversation. An attachment of another conversation is not evaluated,
        /// which is a fact about the run rather than about the attachment — so it has no snapshot row,
        /// not a zero-count one.
        /// </remarks>
        [Fact]
        public void Run_AttachmentOfAnotherConversation_IsNotEvaluated()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var inScope = fixture.AddOutgoing(date);
            var outOfScope = fixture.AddOutgoingInOtherConversation(date);

            fixture.AddTokenAsset(date, isSent: true, 1);

            var result = fixture.Run();

            Assert.Equal(1, result.SnapshotRowCount);

            Assert.Equal(
                1,
                fixture.ScalarLong(
                    $"SELECT COUNT(*) FROM AttachmentEvidenceSnapshot " +
                    $"WHERE RunID = {result.RunID} AND AttachmentID = {inScope};"));

            Assert.Equal(
                0,
                fixture.ScalarLong(
                    $"SELECT COUNT(*) FROM AttachmentEvidenceSnapshot " +
                    $"WHERE RunID = {result.RunID} AND AttachmentID = {outOfScope};"));
        }

        /// <remarks>
        /// The conversation a run covered stays recoverable from the run's own snapshot rows, so it
        /// survives a run whose result object is long gone.
        /// </remarks>
        [Fact]
        public void Run_ConversationScope_IsDerivableFromTheRunsSnapshotRows()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddOutgoingInOtherConversation(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            var result = fixture.Run();

            var conversations = fixture.Rows(
                $"""
                SELECT DISTINCT m.ConversationID
                FROM AttachmentEvidenceSnapshot AS s
                INNER JOIN Attachment AS a ON a.AttachmentID = s.AttachmentID
                INNER JOIN Message AS m ON m.MessageID = a.MessageID
                WHERE s.RunID = {result.RunID}
                ORDER BY m.ConversationID;
                """);

            Assert.Equal(
                [MatchingTestWorkspace.ConversationID.ToString()],
                conversations);
        }
    }
}
