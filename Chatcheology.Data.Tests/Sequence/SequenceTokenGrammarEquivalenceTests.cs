using Chatcheology.Data.Media;
using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests that this census reads the approved suffix grammar exactly as the committed Stage B1
    /// census does.
    /// </summary>
    /// <remarks>
    /// The rule is stated in both services rather than shared, because extracting a helper would mean
    /// editing a path whose real-run evidence is preserved. That is a drift risk taken knowingly, and
    /// this is what contains it: both services run over one workspace and must agree about which
    /// occurrences carry supported evidence. If either changes alone, these tests fail rather than the
    /// two quietly disagreeing.
    /// <para>
    /// The comparison is against Stage B1's supported occurrence count, restricted to the population
    /// the two censuses share: dated, known-direction occurrences on assets that hold bytes.
    /// </para>
    /// </remarks>
    public class SequenceTokenGrammarEquivalenceTests
    {
        [Fact]
        public void SupportedAndUnsupportedSuffixes_AreClassifiedIdentically()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            // Supported: the approved four ASCII digits, at both ends of the domain.
            fixture.AddNamedAsset(date, isSent: true, "0000", "0001", "9999");

            // Unsupported: decorated, too wide, too narrow, non-numeric, empty.
            fixture.AddNamedAsset(
                date, isSent: true, "0001-1", "0001 (2)", "00010", "001", "abcd", string.Empty);

            Assert.Equal(3, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(
                3, fixture.Analyse().DirectionNamespace.KnownDirectionSupportedOccurrenceCount);
        }

        /// <remarks>
        /// A row recording no extension keeps its whole remainder after the marker, which is the
        /// committed rule. Stage A found real occurrences of this shape.
        /// </remarks>
        [Fact]
        public void OccurrenceWithNoRecordedExtension_IsSupportedBySidesAlike()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

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
            Assert.Equal(
                1, fixture.Analyse().DirectionNamespace.KnownDirectionSupportedOccurrenceCount);
        }

        /// <remarks>
        /// Where the recorded extension is not how the name ends, neither census removes it, so the
        /// remainder is not four digits and carries no supported evidence.
        /// </remarks>
        [Fact]
        public void RecordedExtensionThatIsNotTheNameEnding_IsRemovedBySidesAlike()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

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
            Assert.Equal(
                0, fixture.Analyse().DirectionNamespace.KnownDirectionSupportedOccurrenceCount);
        }

        /// <remarks>
        /// The one place the two populations differ by design: Stage B1 counts every supported dated
        /// occurrence, while this census's diagnostic counts only those with known direction on an asset
        /// holding bytes. The difference is stated here so it cannot be mistaken for drift.
        /// </remarks>
        [Fact]
        public void UnknownDirectionAndZeroByteOccurrences_CountForStageB1AndNotForThisDiagnostic()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddNamedAsset(date, isSent: true, "0001");
            fixture.AddNamedAsset(date, isSent: null, "0002");

            var zeroByte = MatchingTestData.Hash(90);
            var zeroByteAsset = fixture.Workspace.AddMediaAsset(zeroByte, sizeBytes: 0);
            fixture.AddCopy(zeroByteAsset, zeroByte, date, isSent: true, "0003");

            Assert.Equal(3, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(
                1, fixture.Analyse().DirectionNamespace.KnownDirectionSupportedOccurrenceCount);
        }

        private static int SupportedFileCountFromStageB1(SequenceTestFixture fixture) =>
            new WaSequenceScopeCensusService().Analyse(
                new WaSequenceScopeCensusRequest
                {
                    DatabasePath = fixture.Workspace.DatabasePath,
                    DeviceGroups =
                    [
                        new DeviceGroupAssignment
                        {
                            MediaSourceID = fixture.SourceID,
                            DeviceGroupID = 1,
                        },
                    ],
                })
                .Reconciliation.SupportedFileCount;
    }
}
