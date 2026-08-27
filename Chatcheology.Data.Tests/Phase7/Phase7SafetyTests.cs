using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The guarantees that matter more than any evidence row the run produces: it changes nothing that
    /// already existed, it decides nothing, and there is no path from the strongest evidence shape it can
    /// compute to a resolved attachment.
    /// </summary>
    /// <remarks>
    /// Stage 7.1 is allowed to say what evidence exists. It is not allowed to resolve an attachment, to
    /// record a human decision, or to leave a mark on anything the earlier phases wrote. A wrong match
    /// accepted once is indistinguishable afterwards from a real one, which is why the absence of a
    /// resolution path is itself tested here.
    /// </remarks>
    public class Phase7SafetyTests
    {
        /// <remarks>
        /// The whole-row invariant rather than a hand-written field list: a checkpoint copy taken before
        /// the run, then <c>WorkspaceCheckpoint.RequireIdenticalTables</c> over all ten pre-existing
        /// tables, which proves identical schema objects, identical row counts and zero row-level
        /// differences with nulls compared equal.
        /// <para>
        /// The building connection is closed before the copy, following the existing convention: a
        /// pooled or live handle defeats a byte-identity proof.
        /// </para>
        /// </remarks>
        [Fact]
        public void Run_ChangesNoPreExistingTable()
        {
            using var fixture = BuildRichFixture();

            var checkpointPath = Path.Combine(fixture.DirectoryPath, "checkpoint.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(fixture.DatabasePath, checkpointPath);

            var result = fixture.Run();

            Assert.True(result.SnapshotRowCount > 0);
            Assert.True(result.OccurrenceRowCount > 0);

            using var live = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            WorkspaceCheckpoint.RequireIdenticalTables(
                live, checkpointPath, WorkspaceDatabase.VersionTwoTableNames);
        }

        /// <remarks>
        /// The same proof, made to fail on purpose, so the proof is known to detect a change rather than
        /// merely to pass. One cell of one pre-existing table is altered after the checkpoint; row counts
        /// still match, and the comparison still catches it.
        /// </remarks>
        [Fact]
        public void RequireIdenticalTables_CatchesAChangeAPhase7RunWouldNeverMake()
        {
            using var fixture = BuildRichFixture();

            var checkpointPath = Path.Combine(fixture.DirectoryPath, "checkpoint.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(fixture.DatabasePath, checkpointPath);

            fixture.Run();

            // Not something the run does — something the run is proved not to do.
            fixture.ExecuteDirect(
                "UPDATE MediaFile SET FileName = 'IMG-20260302-WA0099.jpg' " +
                "WHERE MediaFileID = (SELECT MIN(MediaFileID) FROM MediaFile);");

            using var live = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            var error = Assert.Throws<InvalidOperationException>(
                () => WorkspaceCheckpoint.RequireIdenticalTables(
                    live, checkpointPath, WorkspaceDatabase.VersionTwoTableNames));

            Assert.Contains("MediaFile", error.Message, StringComparison.Ordinal);
            Assert.Contains("FileName", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Run_ChangesNoAttachmentResolutionField()
        {
            using var fixture = BuildRichFixture();

            var before = fixture.Rows(
                """
                SELECT ResolutionStatus, ResolvedMediaAssetID IS NULL, COUNT(*)
                FROM Attachment
                GROUP BY ResolutionStatus, ResolvedMediaAssetID IS NULL
                ORDER BY ResolutionStatus;
                """);

            fixture.Run();

            Assert.Equal(
                before,
                fixture.Rows(
                    """
                    SELECT ResolutionStatus, ResolvedMediaAssetID IS NULL, COUNT(*)
                    FROM Attachment
                    GROUP BY ResolutionStatus, ResolvedMediaAssetID IS NULL
                    ORDER BY ResolutionStatus;
                    """));

            Assert.Equal(
                0,
                fixture.ScalarLong(
                    "SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus <> 'Unresolved';"));

            Assert.Equal(
                0,
                fixture.ScalarLong(
                    "SELECT COUNT(*) FROM Attachment WHERE ResolvedMediaAssetID IS NOT NULL;"));
        }

        /// <remarks>
        /// What this proves is that a Stage 7.1 run <em>creates</em> no decision row, over a fixture that
        /// has none to begin with. It is deliberately not a claim that the tables are globally empty:
        /// once Stage 7.2 records a legitimate decision they are not, and a run must still work. That a
        /// run leaves pre-existing decision rows untouched is proved separately, whole-row, in
        /// <c>Phase7StageSevenOneCompatibilityTests</c>.
        /// </remarks>
        [Fact]
        public void Run_CreatesNoStage72EventRow()
        {
            using var fixture = BuildRichFixture();

            fixture.Run();

            foreach (var tableName in new[]
            {
                "AttachmentReviewEvent",
                "AttachmentCandidateDispositionEvent",
                "AttachmentResolutionEvent",
            })
            {
                Assert.Equal(0, fixture.ScalarLong($"SELECT COUNT(*) FROM {tableName};"));
            }
        }

        [Fact]
        public void Run_AddsNoSchemaObject()
        {
            using var fixture = BuildRichFixture();

            var before = DescribeSchemaObjects(fixture);

            fixture.Run();

            Assert.Equal(before, DescribeSchemaObjects(fixture));

            Assert.Equal(
                WorkspaceDatabase.SchemaVersion, fixture.ScalarLong("PRAGMA user_version;"));
        }

        /// <remarks>
        /// The run leaves the workspace as one file plus the cooperative lock file it is entitled to
        /// create, and no SQLite companion file behind it.
        /// </remarks>
        [Fact]
        public void Run_LeavesNoSqliteCompanionFile()
        {
            using var fixture = BuildRichFixture();

            fixture.Run();

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
        /// The behavioural form of the rule, and the one that matters most: the most persuasive evidence
        /// shape the model can produce — a single candidate, direction-compatible, uniquely constrained —
        /// resolves nothing at all.
        /// </remarks>
        [Fact]
        public void Run_ContainsNoAutomaticResolutionPath()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            var asset = fixture.AddTokenAsset(date, isSent: true, 1);

            var result = fixture.Run();

            // The strongest shape the model can express.
            var support = Assert.Single(fixture.Support(result.RunID, attachment));

            Assert.Equal(asset, support.MediaAssetID);
            Assert.Equal(Phase7DirectionSupport.Supportive, support.Direction);
            Assert.Equal(Phase7OrderSupport.Supportive, support.OrderSupport);

            Assert.Equal(
                OrderConsistencyAnnotation.UniquelyConstrained,
                fixture.Annotation(result.RunID, attachment, asset));

            Assert.Single(Assert.Single(fixture.Rank(result.RunID, attachment).Tiers));

            // ...and the attachment is exactly as unresolved as the emptiest one.
            Assert.Equal(
                1,
                fixture.ScalarLong(
                    $"SELECT COUNT(*) FROM Attachment " +
                    $"WHERE AttachmentID = {attachment} " +
                    $"  AND ResolutionStatus = 'Unresolved' " +
                    $"  AND ResolvedMediaAssetID IS NULL;"));

            Assert.Equal(
                0,
                fixture.ScalarLong(
                    """
                    SELECT (SELECT COUNT(*) FROM AttachmentReviewEvent)
                         + (SELECT COUNT(*) FROM AttachmentCandidateDispositionEvent)
                         + (SELECT COUNT(*) FROM AttachmentResolutionEvent);
                    """));
        }

        /// <remarks>
        /// No production or test file in this stage may carry a path under the recovery drive, or an
        /// archive-specific count from the closure record. Scanned as text, so it cannot be reintroduced
        /// unnoticed.
        /// </remarks>
        [Fact]
        public void Phase7Sources_ContainNoRealWorkspacePathOrArchiveSpecificCount()
        {
            var root = FindRepositoryRoot();

            string[] directories =
            [
                Path.Combine(root, "Chatcheology.Data", "Phase7"),
                Path.Combine(root, "Chatcheology.Data.Tests", "Phase7"),
            ];

            string[] forbidden =
            [
                "WhatsApp Recovery",
                "5,222",
                "45,979",
                "45,980",
                "86,742",
                "87,683",
                "70,802",
                "48,708",
                "31,711",
                "1,070",
                "1,294",
            ];

            var scanned = 0;

            foreach (var directory in directories)
            {
                Assert.True(Directory.Exists(directory), directory);

                foreach (var file in Directory.GetFiles(directory, "*.cs"))
                {
                    // This file holds the list of forbidden strings, so scanning it would find
                    // every one of them. Excluded by name rather than by contorting the literals,
                    // which would make the list harder to read and to extend.
                    if (Path.GetFileName(file) == ThisFileName)
                    {
                        continue;
                    }

                    scanned++;

                    var text = File.ReadAllText(file);

                    foreach (var needle in forbidden)
                    {
                        Assert.DoesNotContain(needle, text, StringComparison.OrdinalIgnoreCase);
                    }

                    Assert.DoesNotContain("E:\\", text, StringComparison.Ordinal);
                }
            }

            Assert.True(scanned > 20, $"Only {scanned} Phase 7 source files were scanned.");
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>The one file the forbidden-string scan skips: this one.</summary>
        private const string ThisFileName = "Phase7SafetyTests.cs";

        /// <summary>
        /// A workspace with every evidence shape the run can publish, so "changes nothing" is proved
        /// against a run that really wrote something.
        /// </summary>
        private static Phase7TestFixture BuildRichFixture()
        {
            var fixture = new Phase7TestFixture();

            try
            {
                var date = Phase7TestFixture.FirstDate;

                fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

                fixture.AddIncoming(date);
                fixture.AddTokenAsset(date, isSent: false, 30);

                fixture.AddSenderless(Phase7TestFixture.Day(3));
                fixture.AddTokenAsset(Phase7TestFixture.Day(3), isSent: true, 40);

                fixture.AddOutgoing(Phase7TestFixture.Day(6));
                fixture.AddTokenAsset(Phase7TestFixture.Day(5), isSent: true, 50);
                fixture.AddNamedAsset(Phase7TestFixture.Day(6), isSent: true, "abcd");

                fixture.CloseBuilding();

                return fixture;
            }
            catch
            {
                fixture.Dispose();

                throw;
            }
        }

        private static List<string> DescribeSchemaObjects(Phase7TestFixture fixture) =>
            fixture.Rows(
                """
                SELECT type, name, tbl_name, COALESCE(sql, '<automatic>')
                FROM sqlite_master
                ORDER BY type, name;
                """);

        /// <summary>
        /// Walks up from the test assembly's location to the directory holding the solution file.
        /// </summary>
        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Chatcheology.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                "The repository root could not be located from the test assembly's directory.");
        }
    }
}
