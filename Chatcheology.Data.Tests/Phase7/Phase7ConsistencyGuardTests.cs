using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The SQLite consistency guard: while the publication transaction holds the writer reservation, no
    /// conforming SQLite client can commit a change, streaming readers keep working, and nothing is
    /// visible until the commit.
    /// </summary>
    /// <remarks>
    /// The mechanism is one non-deferred transaction, begun immediately after the run row commits, that
    /// executes no DML at all until every read pass has finished and then publishes and commits.
    /// <c>BeginTransaction(deferred: false)</c> takes <c>RESERVED</c> at <c>BEGIN</c>, which coexists
    /// with other connections' <c>SHARED</c> locks — so readers stream — while refusing every shape a
    /// competing writer can take.
    /// <para>
    /// <strong>Two mandatory test-engineering rules for this class.</strong> First, every competing
    /// connection builds its own connection string with a short, explicit, <em>non-zero</em> busy
    /// timeout. The provider's default is thirty seconds, so each blocking assertion would otherwise
    /// stall the suite for that long; and <c>DefaultTimeout = 0</c> means <em>wait for ever</em> in this
    /// provider, which is an ADO.NET convention rather than a fail-fast one and will hang a run until it
    /// is killed. Second, the assertions are on <c>SqliteException.SqliteErrorCode == 5</c>
    /// (<c>SQLITE_BUSY</c>) rather than on a message substring.
    /// </para>
    /// <para>
    /// Nothing here sleeps. Every assertion depends on transaction state and lock acquisition, which are
    /// deterministic, and the moments the assertions need are reached through the service's internal
    /// lifecycle seam rather than by timing.
    /// </para>
    /// </remarks>
    public class Phase7ConsistencyGuardTests
    {
        private const int SqliteBusy = 5;

        /// <remarks>
        /// The three shapes a competing writer can take, all refused: an <c>IMMEDIATE</c> begin, a
        /// deferred transaction's first write, and an autocommit statement. Each is a value-only update
        /// to <c>MediaFile.FileName</c> — the column every persisted sequence token is derived from and
        /// the one no cross-check inspects, which is exactly why a lock rather than a comparison is the
        /// right mechanism.
        /// </remarks>
        [Theory]
        [InlineData("immediate")]
        [InlineData("deferred")]
        [InlineData("autocommit")]
        public void Guard_CompetingWriterCannotCommitWhileHeld(string shape)
        {
            using var fixture = BuildFixture(out var attachment);

            var originalName = fixture.ScalarText(
                "SELECT FileName FROM MediaFile ORDER BY MediaFileID LIMIT 1;");

            using var session =
                Phase7EvidenceSnapshotService.OpenSession(fixture.Request());

            var runID = session.BeginRun();

            // Counted, and asserted after the run. Every lock assertion below lives inside the hook,
            // so without this the whole test would pass if the hook were never invoked — and the
            // outer assertions would not notice, because a competitor that never ran cannot have
            // changed the value either.
            var attempts = 0;

            var result = Phase7EvidenceSnapshotService.RunAfterBegin(
                session,
                runID,
                CancellationToken.None,
                whileGuardHeld: () =>
                {
                    attempts++;

                    using var competitor = fixture.OpenCompetingWriter();

                    var error = Assert.Throws<SqliteException>(
                        () => AttemptWrite(competitor, shape));

                    Assert.Equal(SqliteBusy, error.SqliteErrorCode);
                });

            Assert.Equal(1, attempts);

            // The run published, and the value under attack is unchanged.
            Assert.Equal(Phase7RunStatus.Completed, result.Status);

            Assert.Equal(
                originalName,
                fixture.ScalarText(
                    "SELECT FileName FROM MediaFile ORDER BY MediaFileID LIMIT 1;"));

            Assert.True(result.OccurrenceRowCount > 0);
            Assert.NotEmpty(fixture.Support(result.RunID, attachment));
        }

        /// <remarks>
        /// As important as the previous test, because the failure it guards against would be total: if a
        /// held reservation blocked readers, <c>WorkspaceMatchingService</c> could not run at all under
        /// it and the whole mechanism would be unusable. A streaming reader held open across rows is the
        /// exact shape the frozen analysis needs.
        /// </remarks>
        [Fact]
        public void Guard_StageSevenOneReadOnlyConnectionsStillWork()
        {
            using var fixture = BuildFixture(out _);

            using var session =
                Phase7EvidenceSnapshotService.OpenSession(fixture.Request());

            var runID = session.BeginRun();

            var streamedRows = 0;

            var result = Phase7EvidenceSnapshotService.RunAfterBegin(
                session,
                runID,
                CancellationToken.None,
                whileGuardHeld: () =>
                {
                    using var reader = WorkspaceDatabase.OpenReadOnlyConnection(
                        fixture.DatabasePath);

                    using var command = reader.CreateCommand();
                    command.CommandText =
                        "SELECT MediaFileID, FileName FROM MediaFile ORDER BY MediaFileID;";

                    using var rows = command.ExecuteReader();

                    // Held open across rows, which is what "streaming" means here.
                    while (rows.Read())
                    {
                        streamedRows++;

                        Assert.False(rows.IsDBNull(1));
                    }

                    // A second concurrent read-only connection is fine too.
                    using var second = WorkspaceDatabase.OpenReadOnlyConnection(
                        fixture.DatabasePath);

                    using var count = second.CreateCommand();
                    count.CommandText = "SELECT COUNT(*) FROM MediaFile;";

                    Assert.Equal((long)streamedRows, Convert.ToInt64(count.ExecuteScalar()));
                });

            Assert.True(streamedRows > 0);
            Assert.Equal(Phase7RunStatus.Completed, result.Status);
        }

        [Fact]
        public void Guard_AfterReadConnectionsAreDisposed_PublicationCommits()
        {
            using var fixture = BuildFixture(out var attachment);

            var result = fixture.Run();

            Assert.Equal(Phase7RunStatus.Completed, result.Status);
            Assert.NotNull(result.CompletedAtUtc);
            Assert.Equal("Completed", fixture.RunStatus(result.RunID));

            Assert.True(result.SnapshotRowCount > 0);
            Assert.True(result.CandidateRowCount > 0);
            Assert.True(result.OccurrenceRowCount > 0);
            Assert.True(result.AdjacentDateContextRowCount > 0);

            Assert.Equal(
                result.SnapshotRowCount + result.CandidateRowCount + result.OccurrenceRowCount
                    + result.AdjacentDateContextRowCount,
                fixture.EvidenceRowsFor(result.RunID));

            Assert.NotEmpty(fixture.Support(result.RunID, attachment));
        }

        /// <remarks>
        /// The point specific to this mechanism is the last assertion: <strong>T3 itself commits</strong>,
        /// which it could only do if the rolled-back publication transaction had released the
        /// reservation.
        /// </remarks>
        [Fact]
        public void Guard_ComputationFailure_RollsBackTwoAndMarksFailed()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);

            // A dated copy whose marker encodes another date makes the read phase throw.
            fixture.AddAssetWithDisagreeingMarker(date, date.AddDays(-3));

            fixture.CloseBuilding();

            Assert.Throws<InvalidOperationException>(() => fixture.Run());

            var runID = fixture.ScalarLong("SELECT MAX(RunID) FROM Phase7Run;");

            Assert.Equal("Failed", fixture.RunStatus(runID));

            Assert.Equal(
                "<null>",
                fixture.ScalarText($"SELECT CompletedAtUtc FROM Phase7Run WHERE RunID = {runID};"));

            Assert.Equal(0, fixture.TotalEvidenceRows());
        }

        [Fact]
        public void Guard_NoEvidenceRowIsVisibleBeforeTwoCommits()
        {
            using var fixture = BuildFixture(out _);

            using var session =
                Phase7EvidenceSnapshotService.OpenSession(fixture.Request());

            var runID = session.BeginRun();

            var observedRows = -1L;
            var observedStatus = string.Empty;

            var result = Phase7EvidenceSnapshotService.RunAfterBegin(
                session,
                runID,
                CancellationToken.None,
                afterInsertsBeforeCommit: () =>
                {
                    // Every row is written and every check has passed, but the transaction has not
                    // committed. From another connection there is nothing there.
                    observedRows = fixture.TotalEvidenceRows();
                    observedStatus = fixture.RunStatus(runID);
                });

            Assert.Equal(0, observedRows);
            Assert.Equal("Running", observedStatus);

            // ...and after the commit, everything and the completed status arrive together.
            Assert.Equal(Phase7RunStatus.Completed, result.Status);
            Assert.Equal("Completed", fixture.RunStatus(runID));
            Assert.True(fixture.TotalEvidenceRows() > 0);
        }

        /// <remarks>
        /// A competing writer that already owns the reservation when the run's <em>first write</em> is
        /// attempted makes the run fail before it computes anything — and record nothing, because the
        /// run never began. A row would claim a run started when it did not.
        /// <para>
        /// The competitor takes the reservation after the session is opened rather than before, for a
        /// mechanical reason worth stating: the pre-run hash reads the workspace file directly, and on
        /// Windows a live handle held with write access refuses that read outright, which would fail the
        /// run for a different reason than the one under test.
        /// </para>
        /// <para>
        /// The run's own busy timeout is shortened, on this test-owned connection only, purely so the
        /// assertion does not pay the provider's thirty-second default. It changes when
        /// <c>SQLITE_BUSY</c> is reported, never whether it is, and it touches no production code —
        /// <c>WorkspaceDatabase.BuildConnectionString</c> is deliberately left exactly as it is.
        /// </para>
        /// </remarks>
        [Fact]
        public void Guard_WhenAnotherWriterOwnsTheReservation_FailsBeforeComputation()
        {
            using var fixture = BuildFixture(out _);

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                ShortenBusyTimeout(session);

                using var competitor = fixture.OpenCompetingWriter();
                using var held = competitor.BeginTransaction(deferred: false);

                var error = Assert.Throws<SqliteException>(() => session.BeginRun());

                Assert.Equal(SqliteBusy, error.SqliteErrorCode);

                held.Rollback();
            }

            // Nothing was recorded, and nothing was computed: no run began, so no run is invented.
            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM Phase7Run;"));
            Assert.Equal(0, fixture.TotalEvidenceRows());
        }

        /// <remarks>
        /// The other side of the same coin: a competitor that takes the reservation in the single method
        /// call between the run row's commit and the guard's <c>BEGIN</c>. That is a <em>post</em>-T1
        /// failure, so the run did begin and is recorded — but the same competitor also blocks the
        /// bookkeeping write that would mark it failed, which is the exact situation the primary-exception
        /// rule exists for: the failure to record is attached to the primary exception, the primary
        /// exception reaches the caller as itself, and the run is left <c>Running</c> — never current, and
        /// holding no evidence.
        /// </remarks>
        [Fact]
        public void Guard_WhenTheReservationIsTakenAfterTheRunRow_PublishesNothing()
        {
            using var fixture = BuildFixture(out _);

            long runID;

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                ShortenBusyTimeout(session);

                runID = session.BeginRun();

                using var competitor = fixture.OpenCompetingWriter();
                using var held = competitor.BeginTransaction(deferred: false);

                var error = Assert.Throws<SqliteException>(
                    () => Phase7EvidenceSnapshotService.RunAfterBegin(
                        session, runID, CancellationToken.None));

                Assert.Equal(SqliteBusy, error.SqliteErrorCode);

                Assert.Contains(
                    Phase7EvidenceSnapshotService.MarkFailedFailureKey,
                    error.Data.Keys.Cast<object>());

                Assert.Contains(
                    "remains Running and is therefore not current",
                    (string)error.Data[Phase7EvidenceSnapshotService.MarkFailedRunStateKey]!,
                    StringComparison.Ordinal);

                held.Rollback();
            }

            // Running is never current, and no evidence row survives.
            Assert.Equal("Running", fixture.RunStatus(runID));
            Assert.Equal(0, fixture.TotalEvidenceRows());

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            Assert.Null(new Phase7SupportReader().ReadLatestCompletedRunID(connection));
        }

        /// <remarks>
        /// Asserted rather than trusted: the companion-file check is part of the protocol, and a reviewer
        /// should not have to take on faith that a DML-free non-deferred transaction leaves no
        /// <c>-journal</c> beside the workspace.
        /// </remarks>
        [Fact]
        public void Guard_HoldingTheReservationCreatesNoCompanionFile()
        {
            using var fixture = BuildFixture(out _);

            using var session =
                Phase7EvidenceSnapshotService.OpenSession(fixture.Request());

            var runID = session.BeginRun();

            // Seeded to a sentinel the final assertion would reject, so a hook that never ran shows up
            // as a failure rather than as an empty collection that happens to satisfy Assert.Empty.
            string[] companionsWhileHeld = ["<the guard-held observation never ran>"];

            Phase7EvidenceSnapshotService.RunAfterBegin(
                session,
                runID,
                CancellationToken.None,
                whileGuardHeld: () => companionsWhileHeld = CompanionFiles(fixture));

            Assert.Empty(companionsWhileHeld);
            Assert.Empty(CompanionFiles(fixture));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Shortens the busy timeout of the run's own connection, for this test only.
        /// </summary>
        /// <remarks>
        /// Two settings on a connection the test owns, because two layers wait. SQLite's own busy
        /// handler is what a blocked statement consults, and the provider's transaction begin has its
        /// own retry loop driven by <c>DefaultTimeout</c> — leaving either at the thirty-second default
        /// would stall the suite for that long.
        /// <para>
        /// Both change <em>how long</em> a blocked write waits before reporting <c>SQLITE_BUSY</c>, never
        /// whether it reports it, and neither touches production code:
        /// <c>WorkspaceDatabase.BuildConnectionString</c> is deliberately left exactly as it is, so a
        /// real run keeps the provider's default patience.
        /// </para>
        /// </remarks>
        private static void ShortenBusyTimeout(
            Phase7EvidenceSnapshotService.RunSession session)
        {
            session.Writer.Connection.DefaultTimeout = 1;

            using var command = session.Writer.Connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 50;";
            command.ExecuteNonQuery();
        }

        private static void AttemptWrite(SqliteConnection competitor, string shape)
        {
            switch (shape)
            {
                case "immediate":
                    using (competitor.BeginTransaction(deferred: false))
                    {
                    }

                    break;

                case "deferred":
                    using (var deferred = competitor.BeginTransaction(deferred: true))
                    {
                        using var command = competitor.CreateCommand();
                        command.Transaction = deferred;
                        command.CommandText =
                            "UPDATE MediaFile SET FileName = 'IMG-20260302-WA0009.jpg' " +
                            "WHERE MediaFileID = (SELECT MIN(MediaFileID) FROM MediaFile);";

                        command.ExecuteNonQuery();
                    }

                    break;

                default:
                    using (var command = competitor.CreateCommand())
                    {
                        command.CommandText =
                            "UPDATE MediaFile SET FileName = 'IMG-20260302-WA0009.jpg' " +
                            "WHERE MediaFileID = (SELECT MIN(MediaFileID) FROM MediaFile);";

                        command.ExecuteNonQuery();
                    }

                    break;
            }
        }

        private static string[] CompanionFiles(Phase7TestFixture fixture) =>
            [
                .. new[] { "-wal", "-shm", "-journal" }
                    .Select(suffix => fixture.DatabasePath + suffix)
                    .Where(File.Exists),
            ];

        /// <summary>
        /// One outgoing group with real candidates, occurrence rows and adjacent-date context, so a
        /// published run is not vacuously empty.
        /// </summary>
        private static Phase7TestFixture BuildFixture(out long attachment)
        {
            var fixture = new Phase7TestFixture();

            try
            {
                var date = Phase7TestFixture.FirstDate;

                var (attachments, _) =
                    fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 3);

                attachment = attachments[0];

                fixture.AddTokenAsset(date.AddDays(-1), isSent: true, 60);

                fixture.CloseBuilding();

                return fixture;
            }
            catch
            {
                fixture.Dispose();

                throw;
            }
        }
    }
}
