using Chatcheology.Data.Media;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// Pins Stage 7.1's own statement of the approved suffix grammar against the committed Stage B1
    /// census.
    /// </summary>
    /// <remarks>
    /// The fourth grammar-equivalence class. The grammar is stated independently in each stage rather
    /// than shared, because extracting a helper would mean editing frozen paths whose real-run evidence
    /// is preserved — the reason the repository already gives twice, in
    /// <c>WaSequenceScopeCensusService.StripExtension</c> and
    /// <c>WithinDirectionAssignmentCensusService.ReadSupportedToken</c>. Stage A, B1 and B2A/B2B each
    /// have their own equivalence test; those exercise their own copies and say nothing about this one,
    /// and two copies being textually alike today is not a property any test enforces tomorrow.
    /// <para>
    /// Load-bearing here in its own way: this grammar decides every persisted <c>SequenceToken</c>,
    /// every <c>DateTokenKeyUniqueToPayload</c> verdict, every group's token positions and therefore
    /// every order annotation. A single suffix graded differently moves all of them, and the result is
    /// durable rather than a report a human reads once.
    /// <para>
    /// Both sides are exercised over one synthetic workspace. Stage B1 reports supported dated files
    /// directly; Stage 7.1's occurrence index counts the same population, so the two figures must agree
    /// file for file.
    /// </para>
    /// </para>
    /// </remarks>
    public class Phase7SequenceTokenGrammarEquivalenceTests
    {
        [Fact]
        public void SupportedTokensAtBothEndsOfTheDomain_AreRecognisedByBothSides()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddNamedAsset(date, isSent: true, "0000", "9999");

            Assert.Equal(2, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(2, SupportedFileCountFromStageSevenOne(fixture));
        }

        /// <remarks>
        /// Wrong width in both directions, two shapes of decoration, non-numeric, empty, and
        /// non-ASCII digits. None is the approved grammar, so neither side may read a token from any of
        /// them.
        /// </remarks>
        [Fact]
        public void UnsupportedSuffixShapes_AreRejectedByBothSides()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);

            fixture.AddNamedAsset(
                date,
                isSent: true,
                "000",
                "00100",
                "0001-1",
                "0001 (2)",
                "abcd",
                "٠١٢٣",
                string.Empty);

            Assert.Equal(0, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(0, SupportedFileCountFromStageSevenOne(fixture));
        }

        [Fact]
        public void SupportedAndUnsupportedSuffixesTogether_AreCountedAlike()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

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
            Assert.Equal(3, SupportedFileCountFromStageSevenOne(fixture));
        }

        /// <remarks>
        /// A row recording no extension keeps its whole remainder after the marker, which is the
        /// committed rule and a shape Stage A found in the real archive.
        /// </remarks>
        [Fact]
        public void OccurrenceWithNoRecordedExtension_IsSupportedByBothSides()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = Phase7TestFixture.HashOf(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                date,
                isSent: true,
                fileName: Phase7TestFixture.Name(date, "0007"),
                extension: MatchingTestWorkspace.NoExtension);

            Assert.Equal(1, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(1, SupportedFileCountFromStageSevenOne(fixture));
        }

        /// <remarks>
        /// Where the recorded extension is not how the name ends, neither side removes it, so the
        /// remainder is not four digits and carries no supported evidence.
        /// </remarks>
        [Fact]
        public void RecordedExtensionThatIsNotTheNameEnding_IsUnsupportedByBothSides()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = Phase7TestFixture.HashOf(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                date,
                isSent: true,
                fileName: Phase7TestFixture.Name(date, "0007") + ".jpg",
                extension: ".mp4");

            Assert.Equal(0, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(0, SupportedFileCountFromStageSevenOne(fixture));
        }

        /// <remarks>
        /// A decorated prefix does not stop the marker being located, so the suffix is graded on its own
        /// terms by both sides.
        /// </remarks>
        [Fact]
        public void DecoratedPrefix_IsGradedOnItsSuffixByBothSides()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = Phase7TestFixture.HashOf(91);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                date,
                isSent: true,
                fileName: "copy of " + Phase7TestFixture.Name(date, "0011") + ".jpg",
                extension: ".jpg");

            Assert.Equal(1, SupportedFileCountFromStageB1(fixture));
            Assert.Equal(1, SupportedFileCountFromStageSevenOne(fixture));
        }

        /// <remarks>
        /// The grammar rests on the name and the stored date agreeing about which characters are a date.
        /// Both sides refuse a workspace where they do not, rather than reading a token from a name they
        /// cannot trust.
        /// </remarks>
        [Fact]
        public void MarkerDateDisagreeingWithTheStoredDate_IsAnIntegrityFailureForBothSides()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddAssetWithDisagreeingMarker(date, date.AddDays(-3));

            Assert.Throws<InvalidOperationException>(() => SupportedFileCountFromStageB1(fixture));

            Assert.Throws<InvalidOperationException>(
                () => SupportedFileCountFromStageSevenOne(fixture));
        }

        /// <remarks>
        /// Grammar first, collapse second. Both sides count three supported <em>files</em>, because each
        /// is graded on its own name; the group model then collapses the two carrying one payload at one
        /// recovered position into a single occupied position. If support were decided after the
        /// collapse the two figures could not stand together like this.
        /// </remarks>
        [Fact]
        public void AcquisitionDuplicateOfASupportedToken_IsGradedBeforeItIsCollapsed()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            var second = fixture.AddSource();
            var sha = Phase7TestFixture.HashOf(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.AddCopy(assetID, sha, date, isSent: true, Phase7TestFixture.Token(10));
            fixture.AddCopy(assetID, sha, date, isSent: true, Phase7TestFixture.Token(20));
            fixture.AddCopy(
                assetID,
                sha,
                date,
                isSent: true,
                Phase7TestFixture.Token(20),
                mediaSourceID: second);

            Assert.Equal(3, SupportedFileCountFromStageB1(fixture, second));
            Assert.Equal(3, SupportedFileCountFromStageSevenOne(fixture));

            var result = fixture.Run();

            // Three occurrence rows, two token positions: M = 1, T = 2, window two wide.
            Assert.Equal(3, result.OccurrenceRowCount);
            Assert.Equal("1,0,1,0,0,0,2", fixture.Snapshot(result.RunID, attachment));
        }

        /// <remarks>
        /// The direct unit form of the same rule, over the shapes the workspace fixtures cannot express
        /// as easily — an empty remainder, a remainder that is exactly the extension, and a token at
        /// each end of the domain.
        /// </remarks>
        [Fact]
        public void TryReadSupportedToken_GradesEveryShapeAsTheCommittedRuleDoes()
        {
            (string FileName, string? Extension, int? Expected)[] cases =
            [
                ("IMG-20260302-WA0000.jpg", ".jpg", 0),
                ("IMG-20260302-WA9999.jpg", ".jpg", 9999),
                ("IMG-20260302-WA0007", null, 7),
                ("IMG-20260302-WA0007.jpg", ".mp4", null),
                ("IMG-20260302-WA007.jpg", ".jpg", null),
                ("IMG-20260302-WA00070.jpg", ".jpg", null),
                ("IMG-20260302-WA.jpg", ".jpg", null),
                ("IMG-20260302-WA0007-1.jpg", ".jpg", null),
                ("IMG-20260302-WAabcd.jpg", ".jpg", null),
                ("IMG-20260302-WA٠١٢٣.jpg", ".jpg", null),
            ];

            foreach (var (fileName, extension, expected) in cases)
            {
                Assert.True(
                    WhatsAppNameMarker.TryLocate(fileName, out var suffixStart, out _),
                    fileName);

                var supported = Phase7SequenceTokenGrammar.TryReadSupportedToken(
                    fileName, extension, suffixStart, out var token);

                Assert.Equal(expected is not null, supported);

                if (expected is { } value)
                {
                    Assert.Equal(value, token);
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>Stage B1's supported dated file count over the same workspace.</summary>
        private static int SupportedFileCountFromStageB1(
            Phase7TestFixture fixture, params long[] extraSourceIDs)
        {
            var sources = new List<long> { fixture.SourceID };
            sources.AddRange(extraSourceIDs);

            return new WaSequenceScopeCensusService().Analyse(
                new WaSequenceScopeCensusRequest
                {
                    DatabasePath = fixture.DatabasePath,
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
        /// Stage 7.1's supported dated file count, read from the occurrence index's own pass over the
        /// same workspace.
        /// </summary>
        private static int SupportedFileCountFromStageSevenOne(Phase7TestFixture fixture) =>
            Phase7OccurrenceIndex.Read(fixture.DatabasePath, CancellationToken.None)
                .SupportedTokenFileCount;
    }
}
