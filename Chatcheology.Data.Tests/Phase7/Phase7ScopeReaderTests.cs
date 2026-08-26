using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Workspace;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// Run scope, and the one path by which Stage 7.1 could otherwise hand a caller a confident wrong
    /// answer.
    /// </summary>
    /// <remarks>
    /// A run's scope is whatever it evaluated, and the frozen design says a missing snapshot row for a
    /// run is "a fact about run scope, not about the attachment". So there are three distinct answers a
    /// reader must keep apart, and these tests are what keep them apart:
    /// <list type="bullet">
    /// <item>the attachment was not evaluated in this run — refused, never reported as empty;</item>
    /// <item>it was evaluated and had no candidate — a legitimate empty set;</item>
    /// <item>no completed run has ever evaluated it — a null run identifier.</item>
    /// </list>
    /// <para>
    /// The workspace here holds two conversations and two runs, so the defect these tests guard against
    /// can actually fire: a workspace-global "latest completed run" would name a run that evaluated only
    /// the other conversation.
    /// </para>
    /// </remarks>
    public class Phase7ScopeReaderTests
    {
        [Fact]
        public void Reader_AttachmentNotEvaluatedInThatRun_IsRefusedRatherThanReportedEmpty()
        {
            using var fixture = new Phase7TestFixture();

            var (first, second, firstRun, secondRun) = BuildTwoConversationRuns(fixture);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            var reader = new Phase7SupportReader();

            var error = Assert.Throws<InvalidOperationException>(
                () => reader.ReadAttachmentSupport(connection, secondRun.RunID, first));

            Assert.Contains("was not evaluated", error.Message, StringComparison.Ordinal);
            Assert.Contains("run's scope", error.Message, StringComparison.Ordinal);

            Assert.Contains(
                "empty candidate set must not be inferred", error.Message, StringComparison.Ordinal);

            // The mirror case: the second conversation's attachment against the first run.
            Assert.Throws<InvalidOperationException>(
                () => reader.ReadAttachmentSupport(connection, firstRun.RunID, second));
        }

        [Fact]
        public void Reader_LatestCompletedRunContaining_IgnoresRunsThatDidNotEvaluateTheAttachment()
        {
            using var fixture = new Phase7TestFixture();

            var (first, second, firstRun, secondRun) = BuildTwoConversationRuns(fixture);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            var reader = new Phase7SupportReader();

            // The workspace-global helper is scope-blind, and says so: it names the later run whatever
            // that run covered.
            Assert.Equal(secondRun.RunID, reader.ReadLatestCompletedRunID(connection));

            // The per-attachment reading is the one an evidence read needs.
            Assert.Equal(
                firstRun.RunID, reader.ReadLatestCompletedRunContaining(connection, first));

            Assert.Equal(
                secondRun.RunID, reader.ReadLatestCompletedRunContaining(connection, second));

            Assert.True(secondRun.RunID > firstRun.RunID);
        }

        [Fact]
        public void Reader_ZeroCandidateAttachment_ReturnsAnEmptySetRatherThanThrowing()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Phase7TestFixture.FirstDate);

            var result = fixture.Run();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            var reader = new Phase7SupportReader();

            // Evaluated, and the answer was zero. This is a finding, not an absence of one.
            Assert.Empty(reader.ReadAttachmentSupport(connection, result.RunID, attachment));

            // The snapshot row exists and records zero, which is what makes the empty set legitimate
            // rather than a symptom of the attachment being out of the run's scope.
            Assert.Equal(
                0,
                fixture.ScalarLong(
                    $"SELECT ExactDateCandidateCount FROM AttachmentEvidenceSnapshot " +
                    $"WHERE RunID = {result.RunID} AND AttachmentID = {attachment};"));
        }

        [Fact]
        public void Reader_AttachmentNoCompletedRunEverEvaluated_HasNoContainingRun()
        {
            using var fixture = new Phase7TestFixture();

            var inScope = fixture.AddOutgoing(Phase7TestFixture.FirstDate);
            var neverEvaluated = fixture.AddOutgoingInOtherConversation(Phase7TestFixture.FirstDate);

            var result = fixture.Run();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            var reader = new Phase7SupportReader();

            Assert.Equal(result.RunID, reader.ReadLatestCompletedRunContaining(connection, inScope));

            // A distinct and honest answer, and not the same as "evaluated with no candidates".
            Assert.Null(reader.ReadLatestCompletedRunContaining(connection, neverEvaluated));
        }

        [Fact]
        public void Reader_WithNoCompletedRunAtAll_ReportsNull()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Phase7TestFixture.FirstDate);

            fixture.CloseBuilding();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            var reader = new Phase7SupportReader();

            Assert.Null(reader.ReadLatestCompletedRunID(connection));
            Assert.Null(reader.ReadLatestCompletedRunContaining(connection, attachment));
        }

        [Fact]
        public void Reader_ARunThatDoesNotExist_IsRefused()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Phase7TestFixture.FirstDate);

            var result = fixture.Run();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            var error = Assert.Throws<InvalidOperationException>(
                () => new Phase7SupportReader()
                    .ReadAttachmentSupport(connection, result.RunID + 1000, attachment));

            Assert.Contains("There is no Phase 7 run", error.Message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// One workspace, two conversations, and one completed run per conversation — so that a
        /// workspace-global "latest completed run" names a run that evaluated only one of them.
        /// </summary>
        private static (long First, long Second, Phase7RunResult FirstRun, Phase7RunResult SecondRun)
            BuildTwoConversationRuns(Phase7TestFixture fixture)
        {
            var date = Phase7TestFixture.FirstDate;

            var first = fixture.AddOutgoing(date);
            var second = fixture.AddOutgoingInOtherConversation(date);

            fixture.AddTokenAsset(date, isSent: true, 1);

            var firstRun = fixture.Run(
                MatchingTestWorkspace.LocalParticipantID, MatchingTestWorkspace.ConversationID);

            var secondRun = new Phase7EvidenceSnapshotService().Run(
                new Phase7RunRequest
                {
                    DatabasePath = fixture.DatabasePath,
                    ConversationID = MatchingTestWorkspace.OtherConversationID,
                    LocalParticipantID = MatchingTestWorkspace.OutsiderParticipantID,
                });

            Assert.Equal(1, firstRun.SnapshotRowCount);
            Assert.Equal(1, secondRun.SnapshotRowCount);

            return (first, second, firstRun, secondRun);
        }
    }
}
