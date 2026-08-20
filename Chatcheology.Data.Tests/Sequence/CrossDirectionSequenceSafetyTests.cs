using System.Globalization;
using System.Security.Cryptography;
using Chatcheology.Data.Sequence;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests the guarantees that matter more than any figure the census produces: that it changes
    /// nothing, decides nothing, repeats itself exactly, and stops when told to.
    /// </summary>
    public class CrossDirectionSequenceSafetyTests
    {
        // ---------------------------------------------------------------------------------------
        // Nothing is written.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheCensus_LeavesRowCountsAttachmentStatesAndSchemaUnchanged()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, outgoingToken: 3, incomingToken: 8);

            var before = DescribeWorkspaceState(fixture);

            fixture.Analyse();

            Assert.Equal(before, DescribeWorkspaceState(fixture));
            Assert.Equal(2, fixture.Workspace.ScalarLongReadOnly("PRAGMA user_version;"));

            Assert.Equal(
                0,
                fixture.Workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM Attachment WHERE ResolvedMediaAssetID IS NOT NULL;"));
        }

        /// <remarks>
        /// The pool is cleared before each hash because a disposed read-only connection can still hold
        /// the file open, and a hash taken through a live handle would prove less than it appears to.
        /// </remarks>
        [Fact]
        public void TheCensus_LeavesTheDatabaseFileByteIdentical()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, outgoingToken: 3, incomingToken: 8);
            fixture.AddCleanDate(SequenceTestFixture.Day(7), outgoingToken: 40, incomingToken: 12);

            fixture.Workspace.CloseBuildingConnection();

            var before = HashFile(fixture.Workspace.DatabasePath);

            fixture.Analyse();

            Assert.Equal(before, HashFile(fixture.Workspace.DatabasePath));
        }

        [Fact]
        public void TheCensus_LeavesNoSqliteCompanionFileBehind()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, outgoingToken: 3, incomingToken: 8);

            fixture.Workspace.CloseBuildingConnection();

            fixture.Analyse();

            SqliteConnection.ClearAllPools();

            var companions = Directory.GetFiles(fixture.Workspace.DirectoryPath)
                .Where(
                    path => path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.Empty(companions);
        }

        // ---------------------------------------------------------------------------------------
        // Determinism.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TwoRunsOverOneUnchangedWorkspace_ProduceIdenticalFigures()
        {
            using var fixture = new SequenceTestFixture();

            fixture.AddCleanDate(SequenceTestFixture.Day(0), outgoingToken: 3, incomingToken: 8);
            fixture.AddCleanDate(SequenceTestFixture.Day(7), outgoingToken: 90, incomingToken: 20);
            fixture.AddCleanDate(SequenceTestFixture.Day(14), outgoingToken: 15, incomingToken: 15);

            fixture.AddOutgoing(SequenceTestFixture.Day(21), "08:00:00");
            fixture.AddOutgoing(SequenceTestFixture.Day(21), "08:05:00");
            fixture.AddTokenAsset(SequenceTestFixture.Day(21), isSent: true, 61, 62);

            Assert.Equal(Describe(fixture.Analyse()), Describe(fixture.Analyse()));
        }

        // ---------------------------------------------------------------------------------------
        // Names and paths beyond the four digits.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The census reads a name for one purpose. Changing everything else about the name, the path
        /// and the source root, while leaving the marker and the four digits intact, must leave every
        /// figure where it was.
        /// </remarks>
        [Fact]
        public void PrefixesPathsAndRootsTakeNoPartBeyondTheToken()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, outgoingToken: 11, incomingToken: 22);
            fixture.AddCleanDate(SequenceTestFixture.Day(7), outgoingToken: 88, incomingToken: 30);

            var before = Describe(fixture.Analyse());

            fixture.Workspace.Execute(
                """
                UPDATE MediaFile
                SET FileName = 'VID' || substr(FileName, 4),
                    RelativePath = 'elsewhere/' || MediaFileID || '.jpg';

                UPDATE MediaSource SET RootPath = 'SomewhereElse';
                """);

            Assert.Equal(before, Describe(fixture.Analyse()));
        }

        // ---------------------------------------------------------------------------------------
        // Cancellation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void CancellationBeforeAnyWork_ThrowsAndReturnsNothing()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, outgoingToken: 3, incomingToken: 8);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => fixture.Analyse(cancellation.Token));
        }

        [Fact]
        public void CancellationOnAWorkspaceWithNoMediaAndNoMessages_StillThrows()
        {
            using var fixture = new SequenceTestFixture();

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => fixture.Analyse(cancellation.Token));
        }

        [Fact]
        public void AfterCancellation_TheWorkspaceIsStillUntouched()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, outgoingToken: 3, incomingToken: 8);

            fixture.Workspace.CloseBuildingConnection();

            var before = HashFile(fixture.Workspace.DatabasePath);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => fixture.Analyse(cancellation.Token));

            Assert.Equal(before, HashFile(fixture.Workspace.DatabasePath));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>Renders every figure a census produced, so two runs compare as text.</summary>
        private static string Describe(CrossDirectionSequenceCensus census)
        {
            var structure = census.CohortStructure;
            var cardinality = census.StrictDateTokenCardinality;
            var diagnostic = census.DirectionNamespace;

            return string.Join(
                "|",
                [
                    $"{census.ConversationID}:{census.LocalParticipantID}",
                    $"{structure.CohortRelationCount}:{structure.QualifyingGroupCount}:" +
                    $"{structure.DistinctCandidateAssetCount}:{structure.DistinctCohortDateCount}",
                    $"{structure.OutgoingOnlyDateCount}:{structure.IncomingOnlyDateCount}:" +
                    $"{structure.BothDirectionDateCount}",
                    $"{structure.RelationsPerGroup.Population}:{structure.RelationsPerGroup.Minimum}:" +
                    $"{structure.RelationsPerGroup.Median}:{structure.RelationsPerGroup.Maximum}",
                    $"{structure.CrossDirectionDates.BothDirectionGroupsSingleton}:" +
                    $"{structure.CrossDirectionDates.OneSingletonOneMultiRelation}:" +
                    $"{structure.CrossDirectionDates.BothMultiRelation}",
                    Describe(census.RelationWeightedTokenProvenance),
                    Describe(census.GroupWeightedTokenProvenance),
                    $"{cardinality.NoSupportedToken}:{cardinality.ExactlyOneDistinctToken}:" +
                    $"{cardinality.SeveralDistinctTokens}:{cardinality.StrictOrderableDateCount}:" +
                    $"{cardinality.DatesExcludedEqualToken}",
                    Describe(census.PrimaryOrder),
                    Describe(census.DirectionAgreeingSensitivity),
                    Describe(census.Displacement.Concordant),
                    Describe(census.Displacement.Discordant),
                    $"{diagnostic.KnownDirectionSupportedOccurrenceCount}:" +
                    $"{diagnostic.BothDirectionClassDateCount}:{diagnostic.BothClassTokenCount}:" +
                    $"{diagnostic.SingletonInvolvedDateCount}:" +
                    $"{diagnostic.OverlapOrInterleaveDateCount}",
                    Describe(diagnostic.TransitionCounts),
                ]);
        }

        private static string Describe(TokenDirectionProvenanceCounts provenance) =>
            $"{provenance.AgreeingOnly}:{provenance.UnknownOnly}:" +
            $"{provenance.AgreeingAndUnknown}:{provenance.NoSupportedToken}";

        private static string Describe(StrictOrderCensus order) =>
            $"{order.ObservationCount}:{order.ConcordantCount}:{order.DiscordantCount}:" +
            $"{order.ExactOneSidedProbabilityNumerator ?? "-"}:" +
            $"{order.ExactOneSidedProbability ?? "-"}";

        private static string Describe(SequenceBandCounts bands) =>
            $"{bands.Zero}:{bands.One}:{bands.Two}:{bands.ThreeToFive}:{bands.SixToTen}:" +
            $"{bands.ElevenToTwentyFive}:{bands.TwentySixToFifty}:{bands.MoreThanFifty}";

        private static string DescribeWorkspaceState(SequenceTestFixture fixture) =>
            string.Join(
                "|",
                [
                    Count(fixture, "Message"),
                    Count(fixture, "Attachment"),
                    Count(fixture, "MediaFile"),
                    Count(fixture, "MediaAsset"),
                    Count(fixture, "MediaAssetFile"),
                    fixture.Workspace.ScalarLongReadOnly(
                        "SELECT COUNT(*) FROM sqlite_master;")
                        .ToString(CultureInfo.InvariantCulture),
                ]);

        private static string Count(SequenceTestFixture fixture, string table) =>
            fixture.Workspace.ScalarLongReadOnly($"SELECT COUNT(*) FROM {table};")
                .ToString(CultureInfo.InvariantCulture);

        private static string HashFile(string path)
        {
            SqliteConnection.ClearAllPools();

            using var stream = File.OpenRead(path);

            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
