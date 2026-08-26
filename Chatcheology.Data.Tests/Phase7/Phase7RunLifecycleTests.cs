using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Tests.Workspace;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The run lifecycle: <c>Running</c> committed before any computation, one atomic publication,
    /// <c>Failed</c> on any post-T1 failure with no surviving evidence row, and nothing recorded at all
    /// when the run never began.
    /// </summary>
    /// <remarks>
    /// The ordering is what these tests are for. A <c>Failed</c> run row can only exist if the run row
    /// was committed <em>before</em> the computation that failed, so the failure tests below are the
    /// decisive evidence for that ordering, not merely tests of the failure path.
    /// </remarks>
    public class Phase7RunLifecycleTests
    {
        // ---------------------------------------------------------------------------------------
        // Before the run begins.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Run_AgainstAMissingFile_CreatesNothing()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            var path = Path.Combine(workspace.DirectoryPath, "absent.db");

            Assert.Throws<FileNotFoundException>(
                () => new Phase7EvidenceSnapshotService().Run(
                    new Phase7RunRequest { DatabasePath = path, ConversationID = 1 }));

            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Run_WithABlankPath_IsRefused() =>
            Assert.Throws<ArgumentException>(
                () => new Phase7EvidenceSnapshotService().Run(
                    new Phase7RunRequest { DatabasePath = "   ", ConversationID = 1 }));

        /// <remarks>
        /// The direct test of the carried-forward hazard: a Phase 7 run must never migrate a workspace.
        /// The schema guard runs on a read-only connection before any read-write connection is opened, so
        /// a version-2 workspace is refused with nothing created — not even a run row — and the file is
        /// left byte-identical.
        /// </remarks>
        [Fact]
        public void Run_AgainstAVersionTwoWorkspace_IsRefusedAndMigratesNothing()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            SqliteConnection.ClearAllPools();

            var before = WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath);

            var error = Assert.Throws<InvalidOperationException>(
                () => new Phase7EvidenceSnapshotService().Run(
                    new Phase7RunRequest
                    {
                        DatabasePath = workspace.DatabasePath,
                        ConversationID = 1,
                        LocalParticipantID = 1,
                    }));

            Assert.Contains("schema version 2", error.Message, StringComparison.Ordinal);
            Assert.Contains("a Phase 7 evidence run", error.Message, StringComparison.Ordinal);

            SqliteConnection.ClearAllPools();

            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath));

            using var connection =
                SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath);

            Assert.Equal(2, WorkspaceDatabase.ReadSchemaVersion(connection));

            Assert.Equal(
                SyntheticVersionTwoWorkspace.Tables.Order(),
                WorkspaceDatabase.ReadApplicationTableNames(connection));
        }

        [Fact]
        public void Run_WhileTheWorkspaceIsLocked_IsRefused()
        {
            using var fixture = new Phase7TestFixture();

            fixture.AddOutgoing(Phase7TestFixture.FirstDate);
            fixture.CloseBuilding();

            using (WorkspaceExclusiveLock.Acquire(fixture.DatabasePath))
            {
                var error = Assert.Throws<InvalidOperationException>(() => fixture.Run());

                Assert.Contains("already held", error.Message, StringComparison.Ordinal);
            }

            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM Phase7Run;"));
            Assert.Equal(0, fixture.TotalEvidenceRows());
        }

        /// <remarks>
        /// The companion-file check comes before the pre-run hash, and that ordering is load-bearing:
        /// hashing a file beside a live journal would prove nothing, and opening a database with a hot
        /// journal is the one case where an open really does write.
        /// </remarks>
        [Fact]
        public void Run_WhenACompanionFileExists_IsRefusedBeforeAnyWrite()
        {
            using var fixture = new Phase7TestFixture();

            fixture.AddOutgoing(Phase7TestFixture.FirstDate);
            fixture.CloseBuilding();

            var journalPath = fixture.DatabasePath + "-journal";

            File.WriteAllBytes(journalPath, []);

            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => fixture.Run());

                Assert.Contains("companion files", error.Message, StringComparison.Ordinal);

                Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM Phase7Run;"));
            }
            finally
            {
                File.Delete(journalPath);
            }
        }

        /// <remarks>
        /// Request scope is refused before T1, so a caller's typo leaves nothing behind — the same way a
        /// missing file, a held cooperative lock, a stray companion file and a version-2 workspace are
        /// all refused. Were this validated only by the frozen analysis in the first matching pass, it
        /// would be a post-T1 failure and would leave a durable <c>Failed</c> run row.
        /// </remarks>
        [Fact]
        public void Run_InvalidConversation_IsRefusedBeforeT1()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);
            fixture.CloseBuilding();

            var before = DescribePreExistingState(fixture);

            var error = Assert.Throws<InvalidOperationException>(
                () => new Phase7EvidenceSnapshotService().Run(
                    new Phase7RunRequest
                    {
                        DatabasePath = fixture.DatabasePath,
                        ConversationID = 999,
                        LocalParticipantID = MatchingTestWorkspace.LocalParticipantID,
                    }));

            Assert.Contains("has no conversation 999", error.Message, StringComparison.Ordinal);
            Assert.Contains("no run has been recorded", error.Message, StringComparison.Ordinal);

            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM Phase7Run;"));
            Assert.Equal(0, fixture.TotalEvidenceRows());
            Assert.Equal(before, DescribePreExistingState(fixture));
        }

        /// <remarks>
        /// Both shapes of the same mistake: a participant that does not exist at all, and one that
        /// exists but belongs only to another conversation. The second is the dangerous one — it would
        /// produce direction for every message, all of it wrong and all of it looking exactly like the
        /// right answer.
        /// </remarks>
        [Theory]
        [InlineData(999)]
        [InlineData(MatchingTestWorkspace.OutsiderParticipantID)]
        public void Run_InvalidLocalParticipant_IsRefusedBeforeT1(long localParticipantID)
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);
            fixture.CloseBuilding();

            var before = DescribePreExistingState(fixture);

            var error = Assert.Throws<InvalidOperationException>(
                () => new Phase7EvidenceSnapshotService().Run(
                    new Phase7RunRequest
                    {
                        DatabasePath = fixture.DatabasePath,
                        ConversationID = MatchingTestWorkspace.ConversationID,
                        LocalParticipantID = localParticipantID,
                    }));

            Assert.Contains("does not belong to conversation", error.Message, StringComparison.Ordinal);
            Assert.Contains("no run has been recorded", error.Message, StringComparison.Ordinal);

            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM Phase7Run;"));
            Assert.Equal(0, fixture.TotalEvidenceRows());
            Assert.Equal(before, DescribePreExistingState(fixture));
        }

        /// <remarks>
        /// The contrast case, so the pre-flight check cannot drift into refusing a legitimate request: a
        /// null local participant is valid, and means direction is simply unavailable.
        /// </remarks>
        [Fact]
        public void Run_WithNoLocalParticipant_PassesThePreFlightScopeCheck()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            var result = fixture.Run(localParticipantID: null);

            Assert.Equal(Phase7RunStatus.Completed, result.Status);
            Assert.Null(result.LocalParticipantID);
        }

        /// <remarks>
        /// A deterministic T1 failure, driven through the internal seam with a payload the frozen
        /// <c>CHECK (length(RankingDimensionSetVersion) &gt; 0)</c> rejects. The public service can never
        /// produce such a payload, which is exactly why this test has to go through the seam — and why it
        /// does not depend on file permissions, which are platform-fragile and can defeat the temporary
        /// workspace's own cleanup.
        /// </remarks>
        [Fact]
        public void Run_WhenT1Fails_RecordsNoRun()
        {
            using var fixture = new Phase7TestFixture();

            fixture.AddOutgoing(Phase7TestFixture.FirstDate);
            fixture.CloseBuilding();

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                Assert.Throws<SqliteException>(
                    () => session.Writer.BeginRun(
                        DateTime.UtcNow, session.PreRunWorkspaceFileSha256, string.Empty));
            }

            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM Phase7Run;"));
            Assert.Equal(0, fixture.TotalEvidenceRows());
        }

        [Fact]
        public void Run_WhenT1PayloadCarriesAMalformedHash_RecordsNoRun()
        {
            using var fixture = new Phase7TestFixture();

            fixture.AddOutgoing(Phase7TestFixture.FirstDate);
            fixture.CloseBuilding();

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                // Sixty-three characters, which the frozen CHECK (length(...) = 64) rejects.
                Assert.Throws<SqliteException>(
                    () => session.Writer.BeginRun(
                        DateTime.UtcNow,
                        session.PreRunWorkspaceFileSha256[..63],
                        Phase7EvidenceSnapshotService.RankingDimensionSetVersion));
            }

            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM Phase7Run;"));
        }

        // ---------------------------------------------------------------------------------------
        // After the run begins.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// <strong>The decisive test for the corrected ordering.</strong> This <c>Failed</c> row can only
        /// exist if the run row was committed before the computation that failed.
        /// </remarks>
        [Fact]
        public void Run_ComputationFailureAfterT1_ProducesAFailedRunWithNoEvidenceRows()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddAssetWithDisagreeingMarker(date, date.AddDays(-3));

            fixture.CloseBuilding();

            Assert.Throws<InvalidOperationException>(() => fixture.Run());

            Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM Phase7Run;"));

            var runID = fixture.ScalarLong("SELECT MAX(RunID) FROM Phase7Run;");

            Assert.Equal("Failed", fixture.RunStatus(runID));

            Assert.Equal(
                "<null>",
                fixture.ScalarText($"SELECT CompletedAtUtc FROM Phase7Run WHERE RunID = {runID};"));

            Assert.Equal(0, fixture.TotalEvidenceRows());
        }

        /// <remarks>
        /// Cancellation placed deterministically after T1 through the seam, with an already-cancelled
        /// token — no timing, no <c>CancelAfter</c>, no production hook. The
        /// <c>OperationCanceledException</c> must reach the caller as itself, which is what the
        /// primary-exception rule is for.
        /// </remarks>
        [Fact]
        public void Run_CancellationAfterT1_ProducesAFailedRunWithNoEvidenceRows()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 3);
            fixture.CloseBuilding();

            long runID;

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                runID = session.BeginRun();

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                Assert.ThrowsAny<OperationCanceledException>(
                    () => Phase7EvidenceSnapshotService.RunAfterBegin(
                        session, runID, cancellation.Token));
            }

            Assert.Equal("Failed", fixture.RunStatus(runID));
            Assert.Equal(0, fixture.TotalEvidenceRows());
        }

        /// <remarks>
        /// A bookkeeping failure must never replace the primary diagnosis and must never be swallowed
        /// either. Here the run's connection is disposed after T1, so both the guard and the
        /// mark-failed write fail: the primary exception reaches the caller with its own type and stack,
        /// carrying the mark-failed context and the statement that the run remains <c>Running</c>.
        /// </remarks>
        [Fact]
        public void Run_WhenMarkingFailedAlsoFails_RethrowsThePrimaryFailureAndLeavesTheRunRunning()
        {
            using var fixture = new Phase7TestFixture();

            fixture.AddOutgoing(Phase7TestFixture.FirstDate);
            fixture.CloseBuilding();

            long runID;

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                runID = session.BeginRun();

                session.Writer.Connection.Close();

                var error = Assert.ThrowsAny<Exception>(
                    () => Phase7EvidenceSnapshotService.RunAfterBegin(
                        session, runID, CancellationToken.None));

                Assert.Contains(
                    Phase7EvidenceSnapshotService.MarkFailedFailureKey,
                    error.Data.Keys.Cast<object>());

                Assert.Contains(
                    "remains Running and is therefore not current",
                    (string)error.Data[Phase7EvidenceSnapshotService.MarkFailedRunStateKey]!,
                    StringComparison.Ordinal);
            }

            // Running is the safe direction: it is never current.
            Assert.Equal("Running", fixture.RunStatus(runID));
            Assert.Equal(0, fixture.TotalEvidenceRows());
        }

        [Fact]
        public void Reader_RunningRunAndFailedRun_AreNeverCurrent()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            var completed = fixture.Run();

            // Inserted directly, because a real crash cannot be staged in-process.
            fixture.ExecuteDirect(
                $"""
                INSERT INTO Phase7Run (
                    StartedAtUtc, Status, PreRunWorkspaceFileSha256, RankingDimensionSetVersion)
                VALUES ('2026-08-26T09:00:00.0000000Z', 'Running',
                        '{new string('A', 64)}', 'x');

                INSERT INTO Phase7Run (
                    StartedAtUtc, Status, PreRunWorkspaceFileSha256, RankingDimensionSetVersion)
                VALUES ('2026-08-26T09:01:00.0000000Z', 'Failed',
                        '{new string('B', 64)}', 'x');
                """);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            var reader = new Phase7SupportReader();

            Assert.Equal(completed.RunID, reader.ReadLatestCompletedRunID(connection));

            var running = fixture.ScalarLong(
                "SELECT MAX(RunID) FROM Phase7Run WHERE Status = 'Running';");

            var failed = fixture.ScalarLong(
                "SELECT MAX(RunID) FROM Phase7Run WHERE Status = 'Failed';");

            Assert.True(running > completed.RunID);
            Assert.True(failed > completed.RunID);
        }

        [Fact]
        public void Reader_ReadingANonCompletedRun_IsRefused()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            fixture.Run();

            fixture.ExecuteDirect(
                $"""
                INSERT INTO Phase7Run (
                    StartedAtUtc, Status, PreRunWorkspaceFileSha256, RankingDimensionSetVersion)
                VALUES ('2026-08-26T09:00:00.0000000Z', 'Failed',
                        '{new string('C', 64)}', 'x');
                """);

            var failed = fixture.ScalarLong(
                "SELECT MAX(RunID) FROM Phase7Run WHERE Status = 'Failed';");

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            var error = Assert.Throws<InvalidOperationException>(
                () => new Phase7SupportReader()
                    .ReadAttachmentSupport(connection, failed, attachment));

            Assert.Contains("has status Failed", error.Message, StringComparison.Ordinal);

            Assert.Contains(
                "An empty result is not returned", error.Message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------
        // Timestamps, the pre-run hash, and provenance.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Run_StartedAtUtc_PrecedesTheComputationAndTheCompletion()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);
            fixture.CloseBuilding();

            var before = DateTime.UtcNow;

            var result = fixture.Run();

            var after = DateTime.UtcNow;

            Assert.InRange(result.StartedAtUtc, before, after);
            Assert.NotNull(result.CompletedAtUtc);
            Assert.InRange(result.CompletedAtUtc!.Value, result.StartedAtUtc, after);
        }

        [Fact]
        public void Run_StartedAtUtcOnAFailedRun_IsStillRecordedWithNoCompletion()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddAssetWithDisagreeingMarker(date, date.AddDays(-3));
            fixture.CloseBuilding();

            var before = DateTime.UtcNow;

            Assert.Throws<InvalidOperationException>(() => fixture.Run());

            var after = DateTime.UtcNow;

            var runID = fixture.ScalarLong("SELECT MAX(RunID) FROM Phase7Run;");

            var startedAtUtc = DateTime.Parse(
                fixture.ScalarText($"SELECT StartedAtUtc FROM Phase7Run WHERE RunID = {runID};"),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);

            Assert.InRange(startedAtUtc, before, after);

            Assert.Equal(
                "<null>",
                fixture.ScalarText($"SELECT CompletedAtUtc FROM Phase7Run WHERE RunID = {runID};"));
        }

        /// <remarks>
        /// The pre-run hash binds the workspace's byte state immediately before the run's first write —
        /// provenance and debugging, never an evidence fingerprint. The post-run file legitimately
        /// differs, because the run wrote, and nothing reads that difference as evidence changing.
        /// </remarks>
        [Fact]
        public void Run_PreRunHash_BindsTheByteStateImmediatelyBeforeT1()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);
            fixture.CloseBuilding();

            var independent = WorkspaceCheckpoint.ComputeFileSha256(fixture.DatabasePath);

            var result = fixture.Run();

            Assert.Equal(independent, result.PreRunWorkspaceFileSha256);

            Assert.Equal(
                independent,
                fixture.ScalarText(
                    $"SELECT PreRunWorkspaceFileSha256 FROM Phase7Run WHERE RunID = {result.RunID};"));

            SqliteConnection.ClearAllPools();

            Assert.NotEqual(
                independent, WorkspaceCheckpoint.ComputeFileSha256(fixture.DatabasePath));
        }

        [Fact]
        public void Run_PreRunHashOnAFailedRun_IsStillTheByteStateBeforeT1()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddAssetWithDisagreeingMarker(date, date.AddDays(-3));
            fixture.CloseBuilding();

            var independent = WorkspaceCheckpoint.ComputeFileSha256(fixture.DatabasePath);

            Assert.Throws<InvalidOperationException>(() => fixture.Run());

            var runID = fixture.ScalarLong("SELECT MAX(RunID) FROM Phase7Run;");

            Assert.Equal(
                independent,
                fixture.ScalarText(
                    $"SELECT PreRunWorkspaceFileSha256 FROM Phase7Run WHERE RunID = {runID};"));
        }

        [Fact]
        public void Run_Result_EchoesTheRequestScopeExactly()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            var withParticipant = fixture.Run();

            Assert.Equal(MatchingTestWorkspace.ConversationID, withParticipant.ConversationID);

            Assert.Equal(
                MatchingTestWorkspace.LocalParticipantID, withParticipant.LocalParticipantID);

            Assert.Equal(
                Phase7EvidenceSnapshotService.RankingDimensionSetVersion,
                withParticipant.RankingDimensionSetVersion);

            var withoutParticipant = fixture.Run(localParticipantID: null);

            Assert.Null(withoutParticipant.LocalParticipantID);
            Assert.Equal(MatchingTestWorkspace.ConversationID, withoutParticipant.ConversationID);
        }

        // ---------------------------------------------------------------------------------------
        // Retention and reproducibility.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Run_TwiceOnOneWorkspace_ProducesTwoRetainedRuns()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            var first = fixture.Run();
            var firstEvidence = fixture.EvidenceSignature(first.RunID);

            var second = fixture.Run();

            Assert.True(second.RunID > first.RunID);

            Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM Phase7Run;"));
            Assert.Equal("Completed", fixture.RunStatus(first.RunID));
            Assert.Equal("Completed", fixture.RunStatus(second.RunID));

            // The first run's rows are intact, not replaced.
            Assert.Equal(firstEvidence, fixture.EvidenceSignature(first.RunID));

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            Assert.Equal(
                second.RunID,
                new Phase7SupportReader().ReadLatestCompletedRunContaining(connection, attachment));
        }

        /// <remarks>
        /// Two runs over an unchanged workspace produce identical evidence facts. The comparison
        /// deliberately excludes every value that legitimately differs — the run identifier, all four
        /// surrogate primary keys, both timestamps, and the pre-run hash, which necessarily differs
        /// because the first run wrote.
        /// </remarks>
        [Fact]
        public void Run_IsReproducibleFromPersistedFacts()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, _) = fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            fixture.AddTokenAsset(date.AddDays(-1), isSent: true, 60);
            fixture.AddNamedAsset(date, isSent: true, "abcd");
            fixture.AddTokenAsset(date, isSent: false, 70);

            var first = fixture.Run();
            var second = fixture.Run();

            Assert.NotEqual(first.RunID, second.RunID);
            Assert.NotEqual(first.PreRunWorkspaceFileSha256, second.PreRunWorkspaceFileSha256);

            Assert.Equal(
                fixture.EvidenceSignature(first.RunID), fixture.EvidenceSignature(second.RunID));

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

            foreach (var attachment in attachments)
            {
                Assert.Equal(
                    Describe(fixture.Rank(first.RunID, attachment)),
                    Describe(fixture.Rank(second.RunID, attachment)));
            }
        }

        // ---------------------------------------------------------------------------------------
        // The two-pass diagnostic.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Targeted at the diagnostic-construction function rather than at a staged race. Producing a
        /// real mid-run mutation would need a production hook nobody should add, and the property under
        /// test is what the message says — that it names both possibilities and asserts neither.
        /// </remarks>
        [Fact]
        public void TwoPassDisagreement_IsReportedWithoutBlamingTheLock()
        {
            var message = Phase7RunReconciliation.DescribeTwoPassDisagreement(
                "how many exact-date candidates an attachment has", attachmentID: 42);

            Assert.Contains("attachment 42", message, StringComparison.Ordinal);

            // Both possibilities named...
            Assert.Contains(
                "outside this project's cooperating lock protocol", message, StringComparison.Ordinal);

            Assert.Contains(
                "two computations that are supposed to be deterministic did not agree",
                message,
                StringComparison.Ordinal);

            Assert.Contains(
                "This run cannot distinguish those", message, StringComparison.Ordinal);

            // ...and neither asserted.
            Assert.DoesNotContain(
                "the workspace was changed under the lock", message, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("publishes nothing", message, StringComparison.Ordinal);
            Assert.Contains("No evidence row was written", message, StringComparison.Ordinal);
        }

        [Fact]
        public void PreExistingDataChange_IsReportedWithoutBlamingTheLock()
        {
            var message = Phase7RunReconciliation.DescribePreExistingDataChange("before", "after");

            Assert.Contains(
                "changed from outside", message, StringComparison.Ordinal);

            Assert.Contains("or this run has a defect", message, StringComparison.Ordinal);

            Assert.Contains(
                "This run cannot distinguish those", message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The pre-existing state a refused run must leave exactly as it found it.
        /// </summary>
        private static string DescribePreExistingState(Phase7TestFixture fixture) =>
            string.Join(
                "|",
                WorkspaceDatabase.VersionTwoTableNames.Select(
                    table =>
                        $"{table}={fixture.ScalarLong($"SELECT COUNT(*) FROM {table};")}"));
    }
}
