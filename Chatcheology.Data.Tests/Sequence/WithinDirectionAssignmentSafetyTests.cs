using System.Globalization;
using System.Security.Cryptography;
using Chatcheology.Data.Sequence;
using Chatcheology.Data.Tests.Matching;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests what the within-direction assignment census refuses, and the guarantees that matter more
    /// than any figure it produces: it changes nothing, decides nothing, and repeats itself exactly.
    /// </summary>
    /// <remarks>
    /// Several of the service's checks are assertions about the frozen rules rather than reachable
    /// states, and are not tested here because no workspace this fixture can build reaches them: a
    /// wanted key resolving to a zero-byte asset, a direction-contradicting copy inside a compatible
    /// relation, two attachments in one group exposing different compatible sets, a compatible candidate
    /// with no qualifying copy, and a candidate list disagreeing with its own count. Each is
    /// unreachable because the frozen analysis or the schema forbids it, which is why the assertion
    /// exists.
    /// </remarks>
    public class WithinDirectionAssignmentSafetyTests
    {
        // ---------------------------------------------------------------------------------------
        // Nothing is written, decided or persisted.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheCensus_ResolvesNothingAndLeavesTheWorkspaceUnchanged()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");
            fixture.AddTokenAsset(date, isSent: true, 10, 20, 30);

            var before = DescribeWorkspaceState(fixture);

            fixture.AnalyseAssignments();

            Assert.Equal(before, DescribeWorkspaceState(fixture));
            Assert.Equal(2, fixture.Workspace.ScalarLongReadOnly("PRAGMA user_version;"));

            Assert.Equal(
                2,
                fixture.Workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Unresolved';"));

            Assert.Equal(
                0,
                fixture.Workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM Attachment WHERE ResolvedMediaAssetID IS NOT NULL;"));

            Assert.Equal(
                0,
                fixture.Workspace.ScalarLongReadOnly(
                    "SELECT COUNT(*) FROM Attachment WHERE ExpectedMediaType IS NOT NULL;"));
        }

        [Fact]
        public void TheCensus_LeavesTheDatabaseFileByteIdentical()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");
            fixture.AddTokenAsset(date, isSent: true, 10, 20);
            fixture.AddTokenAsset(date, isSent: false, 30);

            fixture.Workspace.CloseBuildingConnection();

            var before = HashFile(fixture.Workspace.DatabasePath);

            fixture.AnalyseAssignments();

            Assert.Equal(before, HashFile(fixture.Workspace.DatabasePath));
        }

        [Fact]
        public void TheCensus_LeavesNoSqliteCompanionFileBehind()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 10);

            fixture.Workspace.CloseBuildingConnection();

            fixture.AnalyseAssignments();

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

            var first = SequenceTestFixture.FirstDate;
            fixture.AddOutgoing(first, "09:00:00");
            fixture.AddOutgoing(first, "09:01:00");
            fixture.AddIncoming(first, "09:02:00");
            fixture.AddTokenAsset(first, isSent: true, 10, 20);
            fixture.AddTokenAsset(first, isSent: true, 30);
            fixture.AddTokenAsset(first, isSent: false, 40);

            var second = SequenceTestFixture.Day(7);
            fixture.AddOutgoing(second, "10:00:00");
            fixture.AddTokenAsset(second, isSent: true, 50);
            fixture.AddNamedAsset(second, isSent: true, "0060-1");

            Assert.Equal(
                Describe(fixture.AnalyseAssignments()), Describe(fixture.AnalyseAssignments()));
        }

        /// <remarks>
        /// The census reads a name for one purpose. Changing every other part of the name, the path and
        /// the source root, while leaving the marker and the four digits intact, must leave every figure
        /// where it was.
        /// </remarks>
        [Fact]
        public void PrefixesPathsAndRootsTakeNoPartBeyondTheToken()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");
            fixture.AddTokenAsset(date, isSent: true, 11, 22);
            fixture.AddTokenAsset(date, isSent: true, 33);

            var before = Describe(fixture.AnalyseAssignments());

            fixture.Workspace.Execute(
                """
                UPDATE MediaFile
                SET FileName = 'VID' || substr(FileName, 4),
                    RelativePath = 'elsewhere/' || MediaFileID || '.jpg';

                UPDATE MediaSource SET RootPath = 'SomewhereElse';
                """);

            Assert.Equal(before, Describe(fixture.AnalyseAssignments()));
        }

        // ---------------------------------------------------------------------------------------
        // Refusals.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void DatedCopyWithNoLocatableMarker_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            fixture.Workspace.AddAssetWithCopy(
                fixture.SourceID, MatchingTestData.Hash(90), date, isSent: true);

            var failure = Assert.Throws<InvalidOperationException>(
                () => fixture.AnalyseAssignments());

            Assert.Contains("no locatable", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void MarkerDateDisagreeingWithThePersistedFileDate_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                date,
                isSent: true,
                fileName: SequenceTestFixture.Name(date.AddDays(-3), "0001") + ".jpg",
                extension: ".jpg");

            var failure = Assert.Throws<InvalidOperationException>(
                () => fixture.AnalyseAssignments());

            Assert.Contains("not the date its own name", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void IncompleteMediaState_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                date,
                isSent: true,
                storedSHA256: string.Empty,
                fileName: SequenceTestFixture.Name(date, "0001") + ".jpg",
                extension: ".jpg");

            Assert.Throws<InvalidOperationException>(() => fixture.AnalyseAssignments());
        }

        [Fact]
        public void FileDateNotInTheFormatTheWorkspaceWrites_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            fixture.Workspace.Execute("UPDATE MediaFile SET FileDate = '02/03/2026';");

            Assert.Throws<InvalidOperationException>(() => fixture.AnalyseAssignments());
        }

        [Fact]
        public void UnknownConversationOrOutsideParticipant_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddOutgoing(SequenceTestFixture.FirstDate);
            fixture.AddTokenAsset(SequenceTestFixture.FirstDate, isSent: true, 10);

            var service = new WithinDirectionAssignmentCensusService();

            Assert.Throws<InvalidOperationException>(
                () => service.Analyse(
                    new WithinDirectionAssignmentCensusRequest
                    {
                        DatabasePath = fixture.Workspace.DatabasePath,
                        ConversationID = 99,
                        LocalParticipantID = MatchingTestWorkspace.LocalParticipantID,
                    }));

            Assert.Throws<InvalidOperationException>(
                () => service.Analyse(
                    new WithinDirectionAssignmentCensusRequest
                    {
                        DatabasePath = fixture.Workspace.DatabasePath,
                        ConversationID = MatchingTestWorkspace.ConversationID,
                        LocalParticipantID = MatchingTestWorkspace.OutsiderParticipantID,
                    }));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankDatabasePath_IsRefused(string path) =>
            Assert.Throws<ArgumentException>(
                () => new WithinDirectionAssignmentCensusService().Analyse(
                    new WithinDirectionAssignmentCensusRequest
                    {
                        DatabasePath = path,
                        ConversationID = 1,
                        LocalParticipantID = 2,
                    }));

        [Fact]
        public void NullRequest_IsRefused() =>
            Assert.Throws<ArgumentNullException>(
                () => new WithinDirectionAssignmentCensusService().Analyse(null!));

        // ---------------------------------------------------------------------------------------
        // Cancellation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void CancellationBeforeAnyWork_ThrowsAndReturnsNothing()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 10);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => fixture.AnalyseAssignments(cancellation.Token));
        }

        [Fact]
        public void AfterCancellation_TheWorkspaceIsStillUntouched()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 10);

            fixture.Workspace.CloseBuildingConnection();

            var before = HashFile(fixture.Workspace.DatabasePath);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => fixture.AnalyseAssignments(cancellation.Token));

            Assert.Equal(before, HashFile(fixture.Workspace.DatabasePath));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>Renders every figure a census produced, so two runs compare as text.</summary>
        private static string Describe(WithinDirectionAssignmentCensus census) =>
            string.Join(
                "|",
                [
                    $"{census.ConversationID}:{census.LocalParticipantID}:" +
                    $"{census.ExcludedAdjacentDateOnlyAttachmentCount}",
                    Describe(census.OutgoingPopulation),
                    Describe(census.IncomingPopulation),
                    Describe(census.PooledPopulation),
                    Describe(census.OutgoingSlack),
                    Describe(census.IncomingSlack),
                    Describe(census.PooledSlack),
                    Describe(census.Feasibility),
                    $"{census.ForcedPositions.Groups}:{census.ForcedPositions.Messages}:" +
                    $"{census.ForcedPositions.MessagesWhereTokenHoldsOneAsset}:" +
                    $"{census.ForcedPositions.MessagesWhereTokenHoldsSeveralAssets}",
                    Describe(census.PositionAmbiguity.MessagesByPossibleTokenPositionCount),
                    Describe(census.OutgoingNarrowing),
                    Describe(census.IncomingNarrowing),
                    Describe(census.PooledNarrowing),
                    $"{census.AssignmentCounts.FeasibleGroupCount}:" +
                    $"{census.AssignmentCounts.GroupsWhereWeightedEqualsUnweighted}:" +
                    $"{census.AssignmentCounts.GroupsWhereWeightedExceedsUnweighted}:" +
                    $"{census.AssignmentCounts.TokenPositionsWithSeveralAssets}:" +
                    $"{census.AssignmentCounts.MaximumDecimalDigitCount}:" +
                    $"{census.AssignmentCounts.MedianDecimalDigitCount}",
                    $"{census.AssetMultiplicity.AssetGroupRelationsWithOneToken}:" +
                    $"{census.AssetMultiplicity.AssetGroupRelationsWithSeveralTokens}:" +
                    $"{census.AssetMultiplicity.MaximumTokenPositionsForOneAsset}",
                    $"{census.ImpossibleGroups.MessagesInImpossibleGroups}:" +
                    $"{census.ImpossibleGroups.BaselineRelationsInImpossibleGroups}:" +
                    $"{census.ImpossibleGroups.Shortfall.Total}",
                    $"{census.Sensitivity.FrozenCandidateRelations}:" +
                    $"{census.Sensitivity.AgreeingTokenEligibleCandidateRelations}:" +
                    $"{census.Sensitivity.TokenPositionsRemovedByFiltering}:" +
                    $"{census.Sensitivity.AssetsLosingEveryTokenPosition}:" +
                    $"{census.Sensitivity.CombinedFrozenBaselineRelations}:" +
                    $"{census.Sensitivity.CombinedFinalCandidateRelations}",
                    Describe(census.Sensitivity.SequenceOrderEffect),
                    $"{census.Collisions.TokenPositionsWithSeveralCompatibleAssets}:" +
                    $"{census.Collisions.GroupsContainingSuchAPosition}:" +
                    $"{census.Collisions.MessagesWhoseRangeIncludesSuchAPosition}",
                ]);

        private static string Describe(AssignmentGroupPopulation population) =>
            $"{population.GroupCount}:{population.MessageCount}:" +
            $"{population.BaselineCompatibleRelationCount}:" +
            $"{population.BaselineRelationsInFeasibleGroups}:" +
            $"{population.BaselineRelationsInImpossibleGroups}:" +
            $"{Describe(population.CompatibleAssetsPerGroup)}:" +
            $"{Describe(population.TokenPositionsPerGroup)}:" +
            $"{Describe(population.OccurrencesPerGroup)}";

        private static string Describe(SequenceSlackDistribution slack) =>
            $"{Describe(slack.Groups)}/{Describe(slack.Messages)}";

        private static string Describe(SlackBandCounts bands) =>
            $"{bands.Negative},{bands.Zero},{bands.One},{bands.Two},{bands.ThreeToFive}," +
            $"{bands.SixToTen},{bands.ElevenToTwentyFive},{bands.MoreThanTwentyFive}";

        private static string Describe(SequenceBandCounts bands) =>
            $"{bands.Zero},{bands.One},{bands.Two},{bands.ThreeToFive},{bands.SixToTen}," +
            $"{bands.ElevenToTwentyFive},{bands.TwentySixToFifty},{bands.MoreThanFifty}";

        private static string Describe(FeasibilityCounts feasibility) =>
            $"{feasibility.NoCompatibleCandidateAssetGroups}:" +
            $"{feasibility.NoSupportedTokenPositionGroups}:" +
            $"{feasibility.TooFewTokenPositionGroups}:{feasibility.EnoughTokenPositionGroups}:" +
            $"{feasibility.MessageTotal}";

        private static string Describe(CandidateNarrowingCensus narrowing) =>
            $"{narrowing.NoReduction}:" +
            $"{narrowing.UniqueCandidateUnderSequenceOrderHypothesis}:" +
            $"{narrowing.ReducedUnderSequenceOrderHypothesis}:" +
            $"{narrowing.MessagesAlreadyUniqueWithoutHypothesis}:" +
            $"{narrowing.BaselineCandidateRelations}:" +
            $"{narrowing.SequenceCompatibleCandidateRelations}";

        private static string DescribeWorkspaceState(SequenceTestFixture fixture) =>
            string.Join(
                "|",
                [
                    Count(fixture, "Message"),
                    Count(fixture, "Attachment"),
                    Count(fixture, "MediaFile"),
                    Count(fixture, "MediaAsset"),
                    Count(fixture, "MediaAssetFile"),
                    fixture.Workspace.ScalarLongReadOnly("SELECT COUNT(*) FROM sqlite_master;")
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
