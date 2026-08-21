using Chatcheology.Data.Media;
using Chatcheology.Data.Sequence;
using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Pins the direction-sequence gate's own reading of the approved suffix grammar against the
    /// committed Stage B1 census.
    /// </summary>
    /// <remarks>
    /// The grammar is stated independently in each census rather than shared, because extracting a
    /// helper would mean editing paths whose real-run evidence is preserved. Stage B2A and Stage B2B
    /// each have their own equivalence test; those exercise their own copies and say nothing about this
    /// one, and two copies being textually alike today is not a property any test enforces tomorrow.
    /// <para>
    /// Load-bearing here in a particular way: this grammar decides which token positions exist, and
    /// therefore the emitted sequence's composition and run count — which is what every exact reference
    /// quantity is conditioned on. A single suffix graded differently moves the whole gate.
    /// </para>
    /// <para>
    /// Both sides are exercised through their public surfaces over one synthetic workspace. Stage B1
    /// reports supported dated files directly; the gate reports supported observations per source, and
    /// with no duplicate positions the two must agree.
    /// </para>
    /// </remarks>
    public class DirectionSequenceGateGrammarEquivalenceTests
    {
        [Fact]
        public void SupportedTokensAtBothEndsOfTheDomainAreRecognisedByBothSides()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddNamed(date, "0000", isSent: true);
            fixture.AddNamed(date, "9999", isSent: true);

            Assert.Equal(2, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(2, SupportedObservationCountFromTheGate(fixture));
        }

        /// <remarks>
        /// Wrong width in both directions, two shapes of decoration, non-numeric, and empty. None is the
        /// approved grammar, so neither census may read a token from any of them.
        /// </remarks>
        [Fact]
        public void UnsupportedSuffixShapesAreRejectedByBothSides()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date);

            foreach (var suffix in new[] { "000", "00100", "0001-1", "0001 (2)", "abcd", "" })
            {
                fixture.AddNamed(date, suffix, isSent: true);
            }

            Assert.Equal(0, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(0, SupportedObservationCountFromTheGate(fixture));

            // The date is still a pair of the gate's universe; it simply emits nothing.
            var population = DirectionSequenceGateFixture
                .ScopeOf(fixture.Analyse(), ScopeLevel.SourceDate)
                .PairPopulation;

            Assert.Equal(1, population.PairCount);
            Assert.Equal(1, population.PairsWithMessageSymbols);
            Assert.Equal(0, population.PairsWithTokenPositions);
            Assert.Equal(1, population.Degenerate.NoTokenPositionPairCount);
        }

        [Fact]
        public void SupportedAndUnsupportedSuffixesTogetherAreCountedAlike()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date);

            foreach (var suffix in
                     new[] { "0001", "0002", "0003", "004", "00050", "0006-1", "0007 (2)", "eight", "" })
            {
                fixture.AddNamed(date, suffix, isSent: true);
            }

            Assert.Equal(3, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(3, SupportedObservationCountFromTheGate(fixture));
        }

        /// <remarks>
        /// A row recording no extension keeps its whole remainder after the marker, which is the
        /// committed rule and a shape Stage A found in the real archive.
        /// </remarks>
        [Fact]
        public void ObservationWithNoRecordedExtensionIsSupportedByBothSides()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = MatchingTestData.Hash(90);
            var mediaAssetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                mediaAssetID,
                sha,
                date,
                isSent: true,
                fileName: DirectionSequenceGateFixture.Name(date, "0007"),
                extension: MatchingTestWorkspace.NoExtension);

            Assert.Equal(1, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(1, SupportedObservationCountFromTheGate(fixture));
        }

        /// <remarks>
        /// Grammar first, collapse second. Stage B1 counts three supported files, because each is graded
        /// on its own name; the gate then collapses the two sharing one recovered position within the
        /// device group, leaving two logical positions. If support were decided after the collapse the
        /// two figures could not stand together like this.
        /// </remarks>
        [Fact]
        public void AnAcquisitionDuplicateIsGradedBeforeItIsCollapsed()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;
            var second = fixture.AddSource();

            fixture.AddOutgoing(date);

            fixture.AddToken(date, 10, isSent: true);
            fixture.AddToken(date, 20, isSent: true);
            fixture.AddToken(date, 20, isSent: true, mediaSourceID: second);

            Assert.Equal(3, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(3, SupportedObservationCountFromTheGate(fixture));

            var group = fixture.Analyse().DeviceGroups.Single();

            Assert.Equal(3, group.LogicalPositionsBeforeCollapse);
            Assert.Equal(2, group.LogicalPositionsAfterCollapse);
        }

        /// <remarks>
        /// The companion case: a second store holding the same recovered position under an unsupported
        /// name adds a file Stage B1 does not count and a position the gate does not create. The
        /// collapse is not what rejected it — the grammar was.
        /// </remarks>
        [Fact]
        public void AnUnsupportedDuplicateNameAddsNeitherASupportedFileNorAPosition()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;
            var second = fixture.AddSource();

            fixture.AddOutgoing(date);

            fixture.AddToken(date, 10, isSent: true);
            fixture.AddNamed(date, "0010-1", isSent: true, mediaSourceID: second);

            Assert.Equal(1, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(1, SupportedObservationCountFromTheGate(fixture));
            Assert.Equal(1, fixture.Analyse().DeviceGroups.Single().LogicalPositionsAfterCollapse);
        }

        /// <remarks>
        /// The grammar rests on the name and the stored date agreeing about which characters are a date.
        /// Both censuses refuse a workspace where they do not, rather than reading a token from a name
        /// they cannot trust.
        /// </remarks>
        [Fact]
        public void AMarkerDateDisagreeingWithTheStoredDateIsRefusedByBothSides()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = MatchingTestData.Hash(90);
            var mediaAssetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                mediaAssetID,
                sha,
                date,
                isSent: true,
                fileName: DirectionSequenceGateFixture.Name(date.AddDays(-3), "0001") + ".jpg",
                extension: ".jpg");

            Assert.Throws<InvalidOperationException>(() => SupportedFileCountFromStageB1(fixture));
            Assert.Throws<InvalidOperationException>(() => fixture.Analyse());
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>Stage B1's supported dated file count over the same workspace.</summary>
        private static int SupportedFileCountFromStageB1(DirectionSequenceGateFixture fixture) =>
            new WaSequenceScopeCensusService().Analyse(
                    new WaSequenceScopeCensusRequest
                    {
                        DatabasePath = fixture.Workspace.DatabasePath,
                        DeviceGroups = fixture.OneGroupForEverySource(),
                    })
                .Reconciliation.SupportedFileCount;

        /// <summary>The gate's own supported-observation count, summed over sources.</summary>
        private static int SupportedObservationCountFromTheGate(
            DirectionSequenceGateFixture fixture) =>
            fixture.Analyse().Sources.Sum(source => source.SupportedTokenObservationCount);
    }
}
