using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Workspace;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// That the read-validate-write window really is exclusive, that two writers cannot both commit a
    /// transition, and that contention fails safely.
    /// </summary>
    /// <remarks>
    /// No sleeps, no threads, no timing. Every test drives the exact moment it needs through the same
    /// internal session the three services use, or by holding a real SQLite reservation from a
    /// test-owned connection — the pattern <c>Phase7ConsistencyGuardTests</c> established for Stage 7.1.
    /// </remarks>
    public class Phase7DecisionConcurrencyTests
    {
        private const int SqliteBusy = 5;

        /// <remarks>
        /// The reservation is taken at <c>BEGIN</c>, before the first validation read, so a decision
        /// cannot even start while another conforming writer owns it. That is what makes the whole
        /// validate-then-write sequence one atomic view.
        /// </remarks>
        [Fact]
        public void DecisionSession_WhenAnotherWriterOwnsTheReservation_FailsWithSqliteBusy()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            fixture.Evidence.Run();

            using (var session = Phase7DecisionSession.Open(fixture.DatabasePath, "a test"))
            {
                ShortenBusyTimeout(session);

                using var competitor = fixture.Evidence.OpenCompetingWriter();
                using var held = competitor.BeginTransaction(deferred: false);

                var error = Assert.Throws<SqliteException>(session.Begin);

                Assert.Equal(SqliteBusy, error.SqliteErrorCode);

                held.Rollback();
            }

            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// The other side of the same coin, and the direct proof that no second writer can slip between
        /// a decision's validation and its write: while the decision transaction is open, a competing
        /// non-deferred begin is refused.
        /// </remarks>
        [Fact]
        public void DecisionSession_WhileHoldingTheReservation_BlocksACompetingWriter()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            fixture.Evidence.Run();

            using var session = Phase7DecisionSession.Open(fixture.DatabasePath, "a test");

            session.Begin();

            using var competitor = fixture.Evidence.OpenCompetingWriter();

            var error = Assert.Throws<SqliteException>(
                () =>
                {
                    using var blocked = competitor.BeginTransaction(deferred: false);
                });

            Assert.Equal(SqliteBusy, error.SqliteErrorCode);
        }

        /// <remarks>
        /// Two confirmations of one attachment cannot both commit. The first wins; the second, whose
        /// validation now runs against the committed state, is refused as already resolved. Exactly one
        /// event exists, and it is the first one.
        /// </remarks>
        [Fact]
        public void TwoConfirms_CannotBothCommit()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            var winner = fixture.Confirm(attachment, first);

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Confirm(attachment, second));

            Assert.Contains("valid only while attachment", error.Message, StringComparison.Ordinal);

            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));

            Assert.Equal(
                winner.AttachmentResolutionEventID,
                fixture.Replayed(attachment).CurrentEffectiveEventID);
        }

        /// <remarks>
        /// Two replacements built against the same current event cannot both commit, and the loser loses
        /// <em>deterministically</em>: its supersedes reference is stale the moment the first commits, so
        /// it is refused by validation rather than by timing.
        /// </remarks>
        [Fact]
        public void TwoReplacementsAgainstTheSameCurrentEvent_CannotBothCommit()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);
            var third = fixture.Evidence.AddTokenAsset(date, isSent: true, 3);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);

            // Both requests are built while the confirmation is still current.
            var one = fixture.ReplaceRequest(
                attachment, second, confirmed.AttachmentResolutionEventID);

            var two = fixture.ReplaceRequest(
                attachment, third, confirmed.AttachmentResolutionEventID);

            var winner = new Phase7ResolutionService().ConfirmReplacement(one);

            var error = Assert.Throws<InvalidOperationException>(
                () => new Phase7ResolutionService().ConfirmReplacement(two));

            Assert.Contains("current effective", error.Message, StringComparison.Ordinal);

            Assert.Equal($"Resolved/{second}", fixture.StoredProjection(attachment));
            Assert.Equal(2, fixture.EventCount("AttachmentResolutionEvent"));

            Assert.Equal(
                winner.AttachmentResolutionEventID,
                fixture.Replayed(attachment).CurrentEffectiveEventID);
        }

        /// <remarks>
        /// A Stage 7.1 evidence run and a Stage 7.2 decision take the same cooperative claim, so they
        /// exclude each other with a clear diagnostic before any SQLite lock is attempted. That is what
        /// closes the window in which a decision could be validated as current against one snapshot and
        /// committed after a newer one published.
        /// </remarks>
        [Fact]
        public void Decision_WhenTheWorkspaceLockIsHeld_IsRefusedWithADiagnostic()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            using (WorkspaceExclusiveLock.Acquire(fixture.DatabasePath))
            {
                var confirmError = Assert.Throws<InvalidOperationException>(
                    () => fixture.Confirm(attachment, asset));

                Assert.Contains("already held", confirmError.Message, StringComparison.Ordinal);

                var reviewError = Assert.Throws<InvalidOperationException>(
                    () => fixture.Review(attachment, run.RunID));

                Assert.Contains("already held", reviewError.Message, StringComparison.Ordinal);
            }

            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
            Assert.Equal(0, fixture.EventCount("AttachmentReviewEvent"));

            // Released, so the same decision now succeeds — the refusal above was contention, not a rule.
            Assert.Equal(asset, fixture.Confirm(attachment, asset).ResolvedMediaAssetID);
        }

        [Fact]
        public void StageSevenOneRun_WhileADecisionSessionHoldsTheLock_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var first = fixture.Evidence.Run();

            using (var session = Phase7DecisionSession.Open(fixture.DatabasePath, "a test"))
            {
                session.Begin();

                var error = Assert.Throws<InvalidOperationException>(() => fixture.Evidence.Run());

                Assert.Contains("already held", error.Message, StringComparison.Ordinal);
            }

            // No second run row was invented while the claim was held.
            Assert.Equal(
                first.RunID, fixture.Evidence.ScalarLong("SELECT MAX(RunID) FROM Phase7Run;"));
        }

        /// <remarks>
        /// A workspace this build must not write is refused by the schema guard, before a read-write
        /// connection exists. Nothing migrates it as a convenience.
        /// </remarks>
        [Fact]
        public void Decision_AgainstASchemaVersionTwoWorkspace_IsRefusedAndMigratesNothing()
        {
            using var directory = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(directory.DatabasePath);

            var error = Assert.Throws<InvalidOperationException>(
                () => new Phase7ResolutionService().Confirm(
                    new Phase7ConfirmRequest
                    {
                        DatabasePath = directory.DatabasePath,
                        AttachmentID = 1,
                        MediaAssetID = 1,
                        IndependentBasisType =
                            Phase7IndependentBasisType.HumanContentOrContextInspection,
                        IndependentBasisDescription = Phase7DecisionFixture.HumanBasis,
                    }));

            Assert.Contains("schema version 2", error.Message, StringComparison.Ordinal);
            Assert.Contains("does not migrate implicitly", error.Message, StringComparison.Ordinal);

            SqliteConnection.ClearAllPools();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(directory.DatabasePath);
            using var command = connection.CreateCommand();

            command.CommandText = "PRAGMA user_version;";

            Assert.Equal(2L, Convert.ToInt64(command.ExecuteScalar()));

            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_schema " +
                "WHERE type = 'table' AND name = 'AttachmentResolutionEvent';";

            Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
        }

        [Fact]
        public void Decision_LeavesNoSqliteCompanionFile()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            fixture.Review(attachment, run.RunID);
            fixture.Confirm(attachment, asset);

            SqliteConnection.ClearAllPools();

            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                Assert.False(File.Exists(fixture.DatabasePath + suffix));
            }

            Assert.Equal(
                [
                    fixture.DatabasePath,
                    fixture.DatabasePath + WorkspaceExclusiveLock.LockFileSuffix,
                ],
                Directory.GetFiles(fixture.DirectoryPath).Order());
        }

        /// <remarks>
        /// Shortens the wait on this test-owned session only, so an assertion that expects
        /// <c>SQLITE_BUSY</c> immediately does not pay the provider's thirty-second default. It changes
        /// when the condition is reported, never whether it is, and it touches no production code.
        /// </remarks>
        private static void ShortenBusyTimeout(Phase7DecisionSession session)
        {
            session.Connection.DefaultTimeout = 1;

            using var command = session.Connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 50;";
            command.ExecuteNonQuery();
        }
    }
}
