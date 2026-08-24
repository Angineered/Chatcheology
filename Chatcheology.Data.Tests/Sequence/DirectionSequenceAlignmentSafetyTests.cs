using System.Security.Cryptography;
using Chatcheology.Data.Sequence;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests the guarantees that matter more than any alignment figure: the run changes nothing, decides
    /// nothing, repeats itself exactly, and returns nothing at all when it does not finish.
    /// </summary>
    public class DirectionSequenceAlignmentSafetyTests
    {
        private static readonly DateOnly Own = DirectionSequenceGateFixture.Day(1);

        // ---------------------------------------------------------------------------------------
        // Nothing is written.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheRunLeavesTheDatabaseFileByteIdentical()
        {
            using var fixture = Populated();

            // The fixture's own building connection holds the file open; the run itself opens the
            // workspace read-only through the accepted gate and closes what it opens.
            fixture.Workspace.CloseBuildingConnection();

            var before = HashFile(fixture.Workspace.DatabasePath);

            _ = fixture.AnalyseAlignment();

            Assert.Equal(before, HashFile(fixture.Workspace.DatabasePath));
        }

        [Fact]
        public void TheRunResolvesNothingAndCreatesNoIndex()
        {
            using var fixture = Populated();

            _ = fixture.AnalyseAlignment();

            Assert.Equal(
                0,
                fixture.Workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus <> 'Unresolved';"));

            Assert.Equal(
                0,
                fixture.Workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM Attachment WHERE ResolvedMediaAssetID IS NOT NULL;"));

            Assert.Equal(
                0,
                fixture.Workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' "
                    + "AND name NOT LIKE 'sqlite_%';"));
        }

        [Fact]
        public void TheRunLeavesNoSqliteCompanionFileBehind()
        {
            using var fixture = Populated();

            _ = fixture.AnalyseAlignment();

            SqliteConnection.ClearAllPools();

            Assert.False(File.Exists(fixture.Workspace.DatabasePath + "-wal"));
            Assert.False(File.Exists(fixture.Workspace.DatabasePath + "-shm"));
            Assert.False(File.Exists(fixture.Workspace.DatabasePath + "-journal"));
        }

        /// <remarks>
        /// There is no persisted resume or checkpoint. A failed run restarts from the beginning, so no run
        /// may leave computational state on disk beside the workspace.
        /// </remarks>
        [Fact]
        public void TheRunPersistsNoResumeState()
        {
            using var fixture = Populated();

            var directory = Path.GetDirectoryName(fixture.Workspace.DatabasePath)!;
            var before = Directory.GetFiles(directory).Order().ToList();

            _ = fixture.AnalyseAlignment();

            SqliteConnection.ClearAllPools();

            Assert.Equal(before, Directory.GetFiles(directory).Order().ToList());
        }

        // ---------------------------------------------------------------------------------------
        // Determinism.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TwoRunsOverOneUnchangedWorkspaceProduceIdenticalFigures()
        {
            using var fixture = Populated();

            Assert.Equal(
                Describe(fixture.AnalyseAlignment()), Describe(fixture.AnalyseAlignment()));
        }

        /// <remarks>
        /// The reference cache exists to make a long run finish, not to decide anything. A hit returns the
        /// identical values the dynamic programme would have produced, so the figures must be the same
        /// with it and without it.
        /// </remarks>
        [Fact]
        public void TheReferenceCacheChangesNoFigure()
        {
            using var fixture = Populated();

            var request = fixture.AlignmentRequest();
            var service = new DirectionSequenceAlignmentService();

            var cached = service.Analyse(
                request, new DirectionSequenceReferenceCache(), progress: null, default);

            var uncached = service.Analyse(
                request, DirectionSequenceReferenceCache.Disabled, progress: null, default);

            Assert.Equal(Describe(cached), Describe(uncached));
        }

        // ---------------------------------------------------------------------------------------
        // Cancellation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ACancelledRunThrowsRatherThanReturningAPartialCensus()
        {
            using var fixture = Populated();
            using var cancellation = new CancellationTokenSource();

            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => fixture.AnalyseAlignment(cancellationToken: cancellation.Token));
        }

        /// <remarks>
        /// Cancelled part-way rather than before starting, which is the case that could otherwise return a
        /// smaller census that looked like a result.
        /// </remarks>
        [Fact]
        public void ARunCancelledDuringTheOutcomeStageReturnsNothing()
        {
            using var fixture = Populated();
            using var cancellation = new CancellationTokenSource();

            var progress = new Progress<DirectionSequenceAlignmentProgress>(
                _ => cancellation.Cancel());

            Assert.Throws<OperationCanceledException>(
                () => new DirectionSequenceAlignmentService().Analyse(
                    fixture.AlignmentRequest(), cache: null, progress, cancellation.Token));
        }

        // ---------------------------------------------------------------------------------------
        // Progress carries no outcome.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// A long run has to be able to say it is alive without saying what it is finding. The progress
        /// type is closed on purpose, and this is what makes that checkable: no member of it is an
        /// admission count, a difference or a share.
        /// </remarks>
        [Fact]
        public void ProgressCarriesNoOutcomeBearingMember()
        {
            var members = typeof(DirectionSequenceAlignmentProgress)
                .GetProperties()
                .Select(property => property.Name)
                .Order()
                .ToList();

            Assert.Equal(
                ["Elapsed", "ProcessedPairCount", "Stage", "TotalPairCount", "WorkingSetBytes"],
                members);
        }

        [Fact]
        public void ProgressIsReportedWithCountsAndTimingOnly()
        {
            using var fixture = Populated();

            var reports = new List<DirectionSequenceAlignmentProgress>();

            _ = new DirectionSequenceAlignmentService().Analyse(
                fixture.AlignmentRequest(),
                cache: null,
                new Progress<DirectionSequenceAlignmentProgress>(reports.Add),
                default);

            // Progress is reported through a synchronisation context, so the count is not deterministic;
            // what matters is that anything reported is well formed.
            Assert.All(
                reports,
                report =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(report.Stage));
                    Assert.True(report.ProcessedPairCount >= 0);
                    Assert.True(report.TotalPairCount >= report.ProcessedPairCount);
                });
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>Every reported figure, flattened, so two runs compare as one value.</summary>
        private static string Describe(DirectionSequenceAlignmentCensus census) =>
            string.Join(
                "|",
                [
                    $"{census.ConversationID}:{census.LocalParticipantID}",
                    $"{census.Reconciliation.ReconciledTargetCount}",
                    $"{census.SupplyLoss.PairCount},{census.SupplyLoss.ZeroSupplyLossPairCount}," +
                    $"{census.SupplyLoss.PositiveSupplyLossPairCount}",
                    $"{census.OrderLoss.PairCount},{census.OrderLoss.ZeroOrderLossPairCount}," +
                    $"{census.OrderLoss.PositiveOrderLossPairCount}",
                    $"{census.Multiplicity.NoExactMonotoneDirectionAlignment}," +
                    $"{census.Multiplicity.OneExactMonotoneDirectionAlignment}," +
                    $"{census.Multiplicity.SeveralExactMonotoneDirectionAlignments}," +
                    $"{census.Multiplicity.PairsWithValidEmbedding}",
                    $"{census.FailureAttribution.FailedEmbeddingCount}," +
                    $"{census.FailureAttribution.FailureNotAttributable}",
                    Describe(census.Binary),
                    Describe(census.Graded),
                    $"{census.GradedDeterminateCheck.PairCount}," +
                    $"{census.GradedDeterminateCheck.SumOfObservedNormalisedEmbeddingShare:R}",
                    string.Join(
                        ";",
                        census.BinaryAdjacentControls.Select(
                            control =>
                                $"{control.Offset}:{Describe(control.CorrectDate)}:" +
                                $"{Describe(control.ShiftedDate)}:" +
                                $"{control.Accounting.AccountedTotal}")),
                    string.Join(
                        ";",
                        census.GradedAdjacentControls.Select(
                            control =>
                                $"{control.Offset}:{Describe(control.CorrectDate)}:" +
                                $"{Describe(control.ShiftedDate)}")),
                    $"{census.Reversal.PalindromicPatternPairCount}," +
                    $"{census.Reversal.ForcedBothAdmitPairCount}," +
                    $"{census.Reversal.ForcedGradedTiePairCount}," +
                    $"{census.Reversal.VariableAdmission.Total}",
                    string.Join(
                        ";",
                        census.DescriptiveSplits.Select(
                            split =>
                                $"{split.Dimension}:{split.PairCount}:{split.SuppressedCellCount}")),
                    string.Join(
                        ";",
                        census.ScopeSensitivity.Select(
                            scope => $"{scope.Scope}:{Describe(scope.Binary)}")),
                ]);

        private static string Describe(DirectionSequenceBinaryFigures figures) =>
            $"{figures.PopulationName},{figures.PairCount},{figures.ObservedAdmissions}," +
            $"{figures.SumOfConditionalAdmissionProbability:R},{figures.ObservedMinusExpected:R}";

        private static string Describe(DirectionSequenceGradedFigures figures) =>
            $"{figures.PopulationName},{figures.PairCount}," +
            $"{figures.SumOfObservedNormalisedEmbeddingShare:R}," +
            $"{figures.SumOfExpectedEmbeddingShare:R},{figures.ObservedMinusExpected:R}";

        private static string HashFile(string path)
        {
            SqliteConnection.ClearAllPools();

            using var stream = File.OpenRead(path);

            return Convert.ToHexString(SHA256.HashData(stream));
        }

        /// <summary>An informative pair with neighbours, coverage loss and a second source.</summary>
        private static DirectionSequenceGateFixture Populated()
        {
            var fixture = new DirectionSequenceGateFixture();
            var second = fixture.AddSource();

            fixture.AddOutgoing(Own, "09:00:00");
            fixture.AddIncoming(Own, "09:01:00");

            fixture.AddToken(Own, 1, isSent: true);
            fixture.AddToken(Own, 2, isSent: true);
            fixture.AddToken(Own, 3, isSent: false);
            fixture.AddToken(Own, 4, isSent: false);

            var previous = DirectionSequenceGateFixture.Day(0);
            fixture.AddToken(previous, 1, isSent: false);
            fixture.AddToken(previous, 2, isSent: false);
            fixture.AddToken(previous, 3, isSent: true);
            fixture.AddToken(previous, 4, isSent: true);

            fixture.AddNamed(Own, "0010-1", isSent: true);
            fixture.AddToken(Own, 9, isSent: null, mediaSourceID: second);

            return fixture;
        }
    }
}
