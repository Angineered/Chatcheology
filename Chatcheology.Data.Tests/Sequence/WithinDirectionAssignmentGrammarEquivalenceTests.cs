using Chatcheology.Data.Media;
using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Pins the within-direction assignment census's own reading of the approved suffix grammar against
    /// the committed Stage B1 census.
    /// </summary>
    /// <remarks>
    /// The grammar is stated independently in each census rather than shared, because extracting a helper
    /// would mean editing paths whose real-run evidence is preserved. Stage B2A has its own equivalence
    /// test; that one exercises B2A's copy and says nothing about this one, and two copies being textually
    /// alike today is not a property any test enforces tomorrow.
    /// <para>
    /// Load-bearing here in a way it is not elsewhere: this grammar decides <c>T</c>, and therefore
    /// <c>SequenceSlack</c>, feasibility, every candidate-occurrence, the narrowing result and the
    /// repeated-asset figures. A single suffix graded differently moves all of them.
    /// </para>
    /// <para>
    /// Both sides are exercised through their public surfaces over one synthetic workspace. Stage B1
    /// reports supported dated files directly; this census reports token positions, which for a
    /// single-message group is its possible-token-position count. With one compatible asset per token the
    /// two figures must agree.
    /// </para>
    /// </remarks>
    public class WithinDirectionAssignmentGrammarEquivalenceTests
    {
        [Fact]
        public void SupportedTokensAtBothEndsOfTheDomain_AreRecognisedByBothSides()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddNamedAsset(date, isSent: true, "0000", "9999");

            Assert.Equal(2, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(2, TokenPositionCountFromThisCensus(fixture));
        }

        /// <remarks>
        /// Wrong width in both directions, two shapes of decoration, non-numeric, and empty. None is the
        /// approved grammar, so neither census may read a token from any of them.
        /// </remarks>
        [Fact]
        public void UnsupportedSuffixShapes_AreRejectedByBothSides()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            fixture.AddNamedAsset(
                date,
                isSent: true,
                "000",
                "00100",
                "0001-1",
                "0001 (2)",
                "abcd",
                string.Empty);

            Assert.Equal(0, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(0, TokenPositionCountFromThisCensus(fixture));

            // The asset is still a compatible candidate; it simply carries no sequence evidence.
            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.Feasibility.NoSupportedTokenPositionGroups);
            Assert.Equal(0, census.Feasibility.EnoughTokenPositionGroups);
            Assert.Equal(1, census.PooledPopulation.CompatibleAssetsPerGroup.One);
        }

        [Fact]
        public void SupportedAndUnsupportedSuffixesTogether_AreCountedAlike()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            fixture.AddNamedAsset(
                date,
                isSent: true,
                "0001",
                "0002",
                "0003",
                "004",
                "00050",
                "0006-1",
                "0007 (2)",
                "eight",
                string.Empty);

            Assert.Equal(3, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(3, TokenPositionCountFromThisCensus(fixture));
        }

        /// <remarks>
        /// A row recording no extension keeps its whole remainder after the marker, which is the
        /// committed rule and a shape Stage A found in the real archive.
        /// </remarks>
        [Fact]
        public void OccurrenceWithNoRecordedExtension_IsSupportedByBothSides()
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
                fileName: SequenceTestFixture.Name(date, "0007"),
                extension: MatchingTestWorkspace.NoExtension);

            Assert.Equal(1, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(1, TokenPositionCountFromThisCensus(fixture));
        }

        /// <remarks>
        /// Where the recorded extension is not how the name ends, neither census removes it, so the
        /// remainder is not four digits and carries no supported evidence.
        /// </remarks>
        [Fact]
        public void RecordedExtensionThatIsNotTheNameEnding_IsUnsupportedByBothSides()
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
                fileName: SequenceTestFixture.Name(date, "0007") + ".jpg",
                extension: ".mp4");

            Assert.Equal(0, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(0, TokenPositionCountFromThisCensus(fixture));
        }

        /// <remarks>
        /// The grammar rests on the name and the stored date agreeing about which characters are a date.
        /// Both censuses refuse a workspace where they do not, rather than reading a token from a name
        /// they cannot trust.
        /// </remarks>
        [Fact]
        public void MarkerDateDisagreeingWithTheStoredDate_IsAnIntegrityFailureForBothSides()
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

            Assert.Throws<InvalidOperationException>(() => SupportedFileCountFromStageB1(fixture));
            Assert.Throws<InvalidOperationException>(() => fixture.AnalyseAssignments());
        }

        /// <remarks>
        /// Grammar first, collapse second. Stage B1 counts three supported files, because each is graded
        /// on its own name; this census then collapses the two carrying one payload at one recovered
        /// position into a single occupied position, leaving two. If support were decided after the
        /// collapse, or the collapse allowed to change it, the two figures could not stand together like
        /// this.
        /// </remarks>
        [Fact]
        public void AcquisitionDuplicateOfASupportedToken_IsGradedBeforeItIsCollapsed()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var second = fixture.AddSource();
            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.AddCopy(assetID, sha, date, isSent: true, SequenceTestFixture.Token(10));
            fixture.AddCopy(assetID, sha, date, isSent: true, SequenceTestFixture.Token(20));
            fixture.AddCopy(
                assetID,
                sha,
                date,
                isSent: true,
                SequenceTestFixture.Token(20),
                mediaSourceID: second);

            Assert.Equal(3, SupportedFileCountFromStageB1(fixture, second));
            Assert.Equal(2, TokenPositionCountFromThisCensus(fixture));

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.PooledPopulation.TokenPositionsPerGroup.Two);
            Assert.Equal(1, census.PooledPopulation.OccurrencesPerGroup.Two);
            Assert.Equal(2, census.AssetMultiplicity.MaximumTokenPositionsForOneAsset);
        }

        /// <remarks>
        /// The companion case: a second store holding the same payload under an unsupported name adds a
        /// file Stage B1 does not count and a position this census does not create. The collapse is not
        /// what rejected it — the grammar was.
        /// </remarks>
        [Fact]
        public void UnsupportedDuplicateName_AddsNeitherASupportedFileNorAPosition()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var second = fixture.AddSource();
            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.AddCopy(assetID, sha, date, isSent: true, SequenceTestFixture.Token(10));
            fixture.AddCopy(
                assetID, sha, date, isSent: true, "0010-1", mediaSourceID: second);

            Assert.Equal(1, SupportedFileCountFromStageB1(fixture, second));
            Assert.Equal(1, TokenPositionCountFromThisCensus(fixture));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>Stage B1's supported dated file count over the same workspace.</summary>
        private static int SupportedFileCountFromStageB1(
            SequenceTestFixture fixture, params long[] extraSourceIDs)
        {
            var sources = new List<long> { fixture.SourceID };
            sources.AddRange(extraSourceIDs);

            return new WaSequenceScopeCensusService().Analyse(
                new WaSequenceScopeCensusRequest
                {
                    DatabasePath = fixture.Workspace.DatabasePath,
                    DeviceGroups =
                    [
                        .. sources.Select(
                            mediaSourceID => new DeviceGroupAssignment
                            {
                                MediaSourceID = mediaSourceID,
                                DeviceGroupID = 1,
                            }),
                    ],
                })
                .Reconciliation.SupportedFileCount;
        }

        /// <summary>
        /// This census's token positions for a single-message group, read from its public surface.
        /// </summary>
        /// <remarks>
        /// With one message, <c>PossibleTokenPositionCount = T - M + 1 = T</c>, and a group with no
        /// supported token is impossible, so it contributes nothing and the maximum is zero. That makes
        /// the figure exactly the count of positions the grammar recognised.
        /// </remarks>
        private static int TokenPositionCountFromThisCensus(SequenceTestFixture fixture)
        {
            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.PooledPopulation.MessageCount);

            return census.PositionAmbiguity.PossibleTokenPositionCountPerGroup.Maximum;
        }
    }
}
