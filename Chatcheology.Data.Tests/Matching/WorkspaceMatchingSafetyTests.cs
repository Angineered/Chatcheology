using System.Security.Cryptography;
using Chatcheology.Data.Matching;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Matching.MatchingTestData;

namespace Chatcheology.Data.Tests.Matching
{
    /// <summary>
    /// Tests the guarantees that matter more than any candidate the engine produces: that it
    /// changes nothing, decides nothing, and stops cleanly when told to.
    /// </summary>
    /// <remarks>
    /// Phase 6 is allowed to say a candidate is possible. It is not allowed to resolve an
    /// attachment, to score one candidate above another, or to leave a mark on the workspace it
    /// read. A wrong match accepted once is indistinguishable afterwards from a real one, which is
    /// why the absence of a confidence figure is itself tested here.
    /// </remarks>
    public class WorkspaceMatchingSafetyTests
    {
        // ---------------------------------------------------------------------------------------
        // Nothing is written.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Analysis_LeavesRowCountsAndAttachmentStatesUnchanged()
        {
            using var workspace = new MatchingTestWorkspace();
            BuildUniqueCandidateFixture(workspace);

            var before = DescribeWorkspaceState(workspace);

            Analyse(workspace, MatchingTestWorkspace.LocalParticipantID);

            Assert.Equal(before, DescribeWorkspaceState(workspace));

            Assert.Equal(
                1,
                workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Unresolved';"));

            Assert.Equal(
                0,
                workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM Attachment WHERE ResolvedMediaAssetID IS NOT NULL;"));
        }

        [Fact]
        public void Analysis_PersistsNoCandidateOrDecisionTable()
        {
            using var workspace = new MatchingTestWorkspace();
            BuildUniqueCandidateFixture(workspace);

            var before = DescribeSchema(workspace);

            Analyse(workspace);

            Assert.Equal(before, DescribeSchema(workspace));

            Assert.Equal(
                2, workspace.ScalarLongReadOnly("PRAGMA user_version;"));
        }

        /// <remarks>
        /// The connection pool is cleared before each hash, because a disposed read-only connection
        /// can still hold the file open — and a hash taken through a live handle would prove less
        /// than it appears to.
        /// </remarks>
        [Fact]
        public void Analysis_LeavesTheDatabaseFileByteIdentical()
        {
            using var workspace = new MatchingTestWorkspace();
            BuildUniqueCandidateFixture(workspace);

            workspace.CloseBuildingConnection();

            var before = HashFile(workspace.DatabasePath);

            Analyse(workspace, MatchingTestWorkspace.LocalParticipantID);

            SqliteConnection.ClearAllPools();

            Assert.Equal(before, HashFile(workspace.DatabasePath));

            Assert.Equal(
                [workspace.DatabasePath],
                Directory.GetFiles(workspace.DirectoryPath));
        }

        // ---------------------------------------------------------------------------------------
        // Nothing is decided.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void UniqueExactDateCandidate_DoesNotResolveTheAttachment()
        {
            using var workspace = new MatchingTestWorkspace();
            BuildUniqueCandidateFixture(workspace);

            var analysis = AnalyseOne(workspace);

            Assert.True(analysis.HasUniqueExactDateCandidate);

            // The tempting case, left exactly as it was found: uniqueness under a heuristic is not
            // proof, and there is deliberately no path from this flag to a resolved attachment.
            Assert.Equal(
                1,
                workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM Attachment " +
                    "WHERE ResolutionStatus = 'Unresolved' AND ResolvedMediaAssetID IS NULL;"));
        }

        [Fact]
        public void UniqueDirectionCompatibleCandidate_DoesNotResolveTheAttachment()
        {
            using var workspace = new MatchingTestWorkspace();
            BuildUniqueCandidateFixture(workspace, isSent: true);

            var analysis = AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID);

            Assert.True(analysis.HasUniqueExactDateCandidate);
            Assert.True(analysis.HasUniqueExactDateDirectionCompatibleCandidate);

            Assert.Equal(
                0,
                workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Resolved';"));
        }

        [Fact]
        public void ContradictoryOnlyCandidates_RemainInspectableRatherThanExcluded()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID =
                workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: false);

            var analysis = AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID);

            var candidate = Assert.Single(analysis.ExactDateCandidates);

            Assert.Equal(mediaAssetID, candidate.MediaAssetID);
            Assert.Equal(
                DirectionCompatibility.ContradictoryOnly, candidate.DirectionCompatibility);
        }

        [Fact]
        public void PhysicalCopyCount_NeverDuplicatesACandidateOrItsRelationship()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);

            for (var copy = 0; copy < 5; copy++)
            {
                workspace.AddMediaFile(sourceID, mediaAssetID, Hash(1), MessageDate);
            }

            var run = Analyse(workspace);
            var candidate = Assert.Single(Assert.Single(run.Analyses).ExactDateCandidates);

            Assert.Equal(6, candidate.PhysicalCopyCount);
            Assert.Equal(1, run.Census.ExactCandidateRelationsUnknown);
            Assert.Equal(1, run.Census.DistinctCandidateMediaAssetsOverall);
        }

        /// <remarks>
        /// The public surface is checked by name rather than by reading the code, so a confidence
        /// figure cannot be added later without this failing. Phase 6 reports named facts and
        /// counts; converting them into a judgement belongs to a phase that can persist a decision
        /// and be argued with.
        /// </remarks>
        [Fact]
        public void MatchingApi_ExposesNoConfidenceScoreOrRanking()
        {
            string[] forbidden =
            [
                "confidence", "score", "rank", "evidencelevel", "probability", "likelihood",
                "weight", "strength", "best", "preferred",
            ];

            var members = typeof(WorkspaceMatchingService).Assembly.GetTypes()
                .Where(type => type.IsPublic && type.Namespace == "Chatcheology.Data.Matching")
                .SelectMany(type => type.GetMembers().Select(member => $"{type.Name}.{member.Name}"))
                .ToList();

            Assert.NotEmpty(members);

            foreach (var member in members)
            {
                foreach (var word in forbidden)
                {
                    Assert.DoesNotContain(word, member, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        [Fact]
        public void DirectionCompatibility_HasNoHeuristicEvidenceLevels()
        {
            Assert.Equal(
                ["Unknown", "Compatible", "Mixed", "ContradictoryOnly"],
                Enum.GetNames<DirectionCompatibility>());
        }

        /// <remarks>
        /// Path, file name and extension are rewritten to junk between two runs. Identical output
        /// proves no filename or path evidence took part, which is the boundary the first Phase 6
        /// pass draws around itself.
        /// </remarks>
        [Fact]
        public void PathsAndFileNames_TakeNoPartInTheEvidence()
        {
            using var workspace = new MatchingTestWorkspace();
            BuildUniqueCandidateFixture(workspace, isSent: true);

            var sourceID = workspace.ScalarLong("SELECT MIN(MediaSourceID) FROM MediaSource;");
            workspace.AddAssetWithCopy(sourceID, Hash(9), MessageDate, isSent: false);

            var before = DescribeAnalysis(workspace);

            workspace.Execute(
                """
                UPDATE MediaFile
                SET RelativePath = 'renamed/' || MediaFileID || '.dat',
                    FileName = 'renamed-' || MediaFileID || '.dat',
                    Extension = NULL;

                UPDATE MediaSource SET RootPath = 'SomewhereElse';
                """);

            Assert.Equal(before, DescribeAnalysis(workspace));
        }

        // ---------------------------------------------------------------------------------------
        // Cancellation and the sink.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void CancellationBeforeAnyWork_ThrowsAndEmitsNothing()
        {
            using var workspace = new MatchingTestWorkspace();
            BuildUniqueCandidateFixture(workspace);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var emitted = new List<AttachmentMatchAnalysis>();

            Assert.Throws<OperationCanceledException>(
                () => new WorkspaceMatchingService().Analyse(
                    workspace.DatabasePath,
                    new MatchAnalysisRequest(MatchingTestWorkspace.ConversationID),
                    emitted.Add,
                    cancellation.Token));

            Assert.Empty(emitted);
        }

        /// <remarks>
        /// Cancelled from inside the sink after the first attachment, so the run stops between two
        /// attachments rather than at a convenient boundary. What the sink holds is a deterministic
        /// prefix of the analysis and nothing more; no census is returned to be mistaken for a
        /// complete one.
        /// </remarks>
        [Fact]
        public void CancellationMidRun_ThrowsAndLeavesOnlyADeterministicPrefix()
        {
            using var workspace = new MatchingTestWorkspace();

            var first = workspace.AddMediaAttachment(MessageDate, time: "09:00:00");
            workspace.AddMediaAttachment(MessageDate, time: "10:00:00");
            workspace.AddMediaAttachment(MessageDate, time: "11:00:00");

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);

            using var cancellation = new CancellationTokenSource();

            var emitted = new List<AttachmentMatchAnalysis>();
            MatchAnalysisCensus? census = null;

            Assert.Throws<OperationCanceledException>(
                () => census = new WorkspaceMatchingService().Analyse(
                    workspace.DatabasePath,
                    new MatchAnalysisRequest(MatchingTestWorkspace.ConversationID),
                    analysis =>
                    {
                        emitted.Add(analysis);
                        cancellation.Cancel();
                    },
                    cancellation.Token));

            Assert.Null(census);
            Assert.Equal(first, Assert.Single(emitted).AttachmentID);
        }

        [Fact]
        public void SinkException_PropagatesAndStopsTheAnalysis()
        {
            using var workspace = new MatchingTestWorkspace();

            workspace.AddMediaAttachment(MessageDate, time: "09:00:00");
            workspace.AddMediaAttachment(MessageDate, time: "10:00:00");

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);

            var emitted = 0;

            var error = Assert.Throws<InvalidOperationException>(
                () => new WorkspaceMatchingService().Analyse(
                    workspace.DatabasePath,
                    new MatchAnalysisRequest(MatchingTestWorkspace.ConversationID),
                    _ =>
                    {
                        emitted++;

                        throw new InvalidOperationException("The caller's sink failed.");
                    }));

            Assert.Equal("The caller's sink failed.", error.Message);
            Assert.Equal(1, emitted);
        }

        /// <remarks>
        /// Proves the analysis let go of the file. A leaked reader or connection would fail the
        /// delete with a sharing violation, which is exactly what this is watching for.
        /// </remarks>
        [Fact]
        public void AfterCancellation_TheWorkspaceCanStillBeReopenedAndDeleted()
        {
            var workspace = new MatchingTestWorkspace();

            try
            {
                BuildUniqueCandidateFixture(workspace);

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                Assert.Throws<OperationCanceledException>(
                    () => new WorkspaceMatchingService().Analyse(
                        workspace.DatabasePath,
                        new MatchAnalysisRequest(MatchingTestWorkspace.ConversationID),
                        attachmentSink: null,
                        cancellation.Token));

                Assert.Equal(1, workspace.ScalarLongReadOnly("SELECT COUNT(*) FROM Attachment;"));

                workspace.CloseBuildingConnection();
            }
            finally
            {
                workspace.Dispose();
            }
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// One attachment and exactly one asset dated to its message, which is the shape most
        /// likely to tempt an engine into resolving something.
        /// </summary>
        private static void BuildUniqueCandidateFixture(
            MatchingTestWorkspace workspace, bool? isSent = null)
        {
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent);
        }

        /// <summary>Renders what one run produced, so two runs can be compared as text.</summary>
        private static string DescribeAnalysis(MatchingTestWorkspace workspace)
        {
            var run = Analyse(workspace, MatchingTestWorkspace.LocalParticipantID);

            var attachments = run.Analyses.Select(
                analysis => string.Join(
                    ",",
                    analysis.ExactDateCandidates.Select(
                        candidate =>
                            $"{candidate.MediaAssetID}:{candidate.MediaType}:" +
                            $"{candidate.SupportingPhysicalCopyCount}:" +
                            $"{candidate.DirectionCompatibility}")));

            return string.Join(
                "|",
                [
                    ..attachments,
                    run.Census.ExactCandidateRelationsCompatible.ToString(),
                    run.Census.ExactCandidateRelationsContradictoryOnly.ToString(),
                    run.Census.MediaFileWithFileDateCount.ToString(),
                ]);
        }

        private static string DescribeWorkspaceState(MatchingTestWorkspace workspace)
        {
            string[] tables =
            [
                "Message", "Attachment", "MediaSource", "MediaFile", "MediaAsset", "MediaAssetFile",
            ];

            return string.Join(
                "|",
                tables.Select(
                    table =>
                        $"{table}={workspace.ScalarLongReadOnly($"SELECT COUNT(*) FROM {table};")}"));
        }

        private static string DescribeSchema(MatchingTestWorkspace workspace)
        {
            using var connection =
                WorkspaceDatabase.OpenReadOnlyConnection(workspace.DatabasePath);

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

            using var reader = command.ExecuteReader();

            var names = new List<string>();

            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return string.Join(",", names);
        }

        private static string HashFile(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
