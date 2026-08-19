using System.Globalization;
using Chatcheology.Data.Media;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// Tests the Stage A file name suffix census: what it classifies, counts and refuses to
    /// interpret.
    /// </summary>
    /// <remarks>
    /// The census exists to say what recovered names look like after the marker Phase 5 already
    /// recognises, so that a supported grammar can be argued from evidence instead of proposed from
    /// an example. Nothing here narrows a candidate, orders anything, or decides what a suffix
    /// means.
    /// </remarks>
    public class FileNameSuffixCensusTests
    {
        private const string HashA = "AAAA000000000000000000000000000000000000000000000000000000000001";
        private const string HashB = "BBBB000000000000000000000000000000000000000000000000000000000002";
        private const string HashC = "CCCC000000000000000000000000000000000000000000000000000000000003";

        // ---------------------------------------------------------------------------------------
        // Agreement with the committed classifier.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The committed classifier's own cases, run through the census. Where it reads a date the
        /// census must locate the same marker; the mirrored scan exists precisely so this can be
        /// asserted rather than assumed.
        /// </remarks>
        [Theory]
        [InlineData("IMG-20260724-WA0004.jpg")]
        [InlineData("VID-20220128-WA0003.mp4")]
        [InlineData("DOC-20230820-WA0016.pdf")]
        [InlineData("PTT-20240229-WA0001.opus")]
        [InlineData("IMG-20260724-WA0004-2.jpg")]
        public void NamesTheCommittedClassifierDates_HaveTheirMarkerLocated(string fileName)
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, fileName);

            var census = Analyse(workspace);

            Assert.Equal(1, census.MediaFileWithFileDateCount);
            Assert.Equal(1, census.MarkerLocatedCount);
            Assert.Equal(0, census.DatedFilesWithNoLocatableMarker);
        }

        /// <remarks>
        /// The classifier's null cases, including the two structural refusals worth stating: eight
        /// digits at the very start of a name are never a date, because the rule requires a hyphen
        /// before them, and a wrong marker is not a marker.
        /// </remarks>
        [Theory]
        [InlineData("IMG-20261332-WA0004.jpg")]
        [InlineData("IMG-20260000-WA0004.jpg")]
        [InlineData("IMG-20230229-WA0004.jpg")]
        [InlineData("IMG-00000000-WA0004.jpg")]
        [InlineData("20260724.jpg")]
        [InlineData("IMG_20260724_120000.jpg")]
        [InlineData("Scan-20260724-Invoice.pdf")]
        [InlineData("20260724-WA0004.jpg")]
        [InlineData("IMG-20260724-XY0004.jpg")]
        [InlineData("IMG-2026072-WA0004.jpg")]
        [InlineData("Receipt 0821234567 copy.pdf")]
        public void NamesTheCommittedClassifierRejects_AreCountedAsUndated(string fileName)
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, fileName);

            var census = Analyse(workspace);

            Assert.Equal(1, census.MediaFileWithNullFileDateCount);
            Assert.Equal(0, census.MarkerLocatedCount);
            Assert.Equal(0, census.DatedFilesWithNoLocatableMarker);
            Assert.Equal(1, census.UndatedFeatures.NameCount);
        }

        /// <remarks>
        /// A structurally correct group whose digits are not a date does not stop the scan, so a
        /// later valid group in the same name still wins — and the suffix measured is the one after
        /// that later marker.
        /// </remarks>
        [Fact]
        public void WhenAnEarlierGroupIsNotADate_TheLaterValidGroupIsUsed()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithFile(
                sourceID, HashA, "IMG-20261332-WA0004-20260724-WA0007.jpg");

            var census = Analyse(workspace);

            Assert.Equal(1, census.MarkerLocatedCount);
            Assert.Equal(1, Count(census, SuffixSyntaxClass.PurelyNumericSuffix));
            Assert.Equal("0007", census.PurelyNumericWidths.Widths.Single().MinimumDigitString);
        }

        // ---------------------------------------------------------------------------------------
        // Suffix syntax classes and widths.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("IMG-20260724-WA.jpg", SuffixSyntaxClass.EmptySuffix)]
        [InlineData("IMG-20260724-WA0004.jpg", SuffixSyntaxClass.PurelyNumericSuffix)]
        [InlineData("IMG-20260724-WA0004-2.jpg", SuffixSyntaxClass.NumericPrefixWithTrailingDecoration)]
        [InlineData("IMG-20260724-WAxyz.jpg", SuffixSyntaxClass.NonNumericSuffix)]
        public void SuffixesAreClassifiedBySyntaxAlone(string fileName, SuffixSyntaxClass expected)
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, fileName);

            var census = Analyse(workspace);

            Assert.Equal(1, Count(census, expected));
            Assert.Equal(1, census.SuffixClasses.Sum(suffixClass => suffixClass.FileCount));
        }

        [Fact]
        public void PurelyNumericWidths_ReportTheSpreadAndItsBounds()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "IMG-20260724-WA0011.jpg");
            workspace.AddAssetWithFile(sourceID, HashC, "IMG-20260724-WA00121.jpg");

            var widths = Analyse(workspace).PurelyNumericWidths;

            Assert.Equal(3, widths.TotalObservations);
            Assert.Equal(4, widths.DominantWidth);
            Assert.Equal(2, widths.DominantWidthCount);
            Assert.Equal(3, widths.DistinctDigitStrings);

            Assert.Equal([4, 5], widths.Widths.Select(width => width.Width));
            Assert.Equal("0004", widths.Widths[0].MinimumDigitString);
            Assert.Equal("0011", widths.Widths[0].MaximumDigitString);
            Assert.Equal("00121", widths.Widths[1].MinimumDigitString);
        }

        /// <remarks>
        /// Pinned decision 8. A decorated suffix contributes its leading digits to its own
        /// distribution and to nothing else, so a decorated population cannot quietly inflate the
        /// dominant width of the purely numeric one.
        /// </remarks>
        [Fact]
        public void DecoratedNumericPrefixes_AreNeverMergedIntoThePurelyNumericWidths()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA00004.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "IMG-20260724-WA0004-2.jpg");
            workspace.AddAssetWithFile(sourceID, HashC, "IMG-20260724-WA0005-2.jpg");

            var census = Analyse(workspace);

            Assert.Equal(1, census.PurelyNumericWidths.TotalObservations);
            Assert.Equal(5, census.PurelyNumericWidths.DominantWidth);

            Assert.Equal(2, census.DecoratedNumericPrefixWidths.TotalObservations);
            Assert.Equal(4, census.DecoratedNumericPrefixWidths.DominantWidth);
            Assert.Equal(2, census.DecoratedNumericPrefixWidths.DistinctDigitStrings);
        }

        // ---------------------------------------------------------------------------------------
        // Extension handling.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Pinned decision 2. The committed extension is lower-cased when it is derived, so a name
        /// ending in upper case is the ordinary case rather than the exotic one, and the comparison
        /// has to ignore case for the suffix to be cut in the right place.
        /// </remarks>
        [Fact]
        public void AnExtensionRecordedInLowerCase_StillMatchesAnUpperCaseName()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.JPG");

            var census = Analyse(workspace);

            Assert.Equal(0, census.ExtensionDoesNotMatchNameEndingCount);
            Assert.Equal(1, Count(census, SuffixSyntaxClass.PurelyNumericSuffix));
        }

        [Fact]
        public void ANameWithNoExtension_KeepsItsWholeRemainder()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004");

            var census = Analyse(workspace);

            Assert.Equal(1, census.NullExtensionCount);
            Assert.Equal(1, Count(census, SuffixSyntaxClass.PurelyNumericSuffix));
        }

        /// <remarks>
        /// A workspace written by different rules, where the recorded extension is not how the name
        /// ends. The census counts it and cuts nothing, so the mismatch shows up as decoration
        /// rather than being silently absorbed.
        /// </remarks>
        [Fact]
        public void AnExtensionThatIsNotHowTheNameEnds_IsCountedAndNotStripped()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var assetID = workspace.AddMediaAsset(HashA);

            workspace.AddMediaFile(
                sourceID, assetID, HashA, "IMG-20260724-WA0004.jpg", extension: ".png");

            var census = Analyse(workspace);

            Assert.Equal(1, census.ExtensionDoesNotMatchNameEndingCount);
            Assert.Equal(1, Count(census, SuffixSyntaxClass.NumericPrefixWithTrailingDecoration));
        }

        // ---------------------------------------------------------------------------------------
        // Shape signatures.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Pinned decisions 3 and 4: the allowlisted punctuation survives literally, including a run
        /// of it, and anything outside the allowlist collapses into one class token so no character
        /// from a recovered name can reach a report.
        /// </remarks>
        [Theory]
        [InlineData("IMG-20260724-WA0004 (1).jpg", "D4 (D1)")]
        [InlineData("IMG-20260724-WA0004---2.jpg", "D4---D1")]
        [InlineData("IMG-20260724-WA0004_copy.jpg", "D4_A4")]
        [InlineData("IMG-20260724-WA0004[2].jpg", "D4[D1]")]
        [InlineData("IMG-20260724-WA0004+x.jpg", "D4X1A1")]
        [InlineData("IMG-20260724-WA0004++.jpg", "D4X2")]
        [InlineData("IMG-20260724-WAabc.jpg", "A3")]
        [InlineData("IMG-20260724-WAab.jpg", "A2")]
        public void SuffixShapes_AreNormalisedToClassAndRunLength(
            string fileName, string expectedSignature)
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, fileName);

            var signature = Assert.Single(Analyse(workspace).SuffixShapeSignatures);

            Assert.Equal(expectedSignature, signature.Signature);
            Assert.Equal(1, signature.FileCount);
            Assert.Equal(1, signature.AssetCount);
        }

        /// <remarks>
        /// The privacy guarantee stated as a property rather than as a claim: whatever letters and
        /// digits a name carries, a signature can only ever contain the three class letters, run
        /// lengths and allowlisted punctuation.
        /// </remarks>
        [Fact]
        public void SignaturesCarryNoCharacterFromTheNameTheyDescribe()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WAzq (7).jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "quirky-photo-zz.png");

            var census = Analyse(workspace);

            var signatures = census.SuffixShapeSignatures
                .Concat(census.UndatedBasenameShapeSignatures)
                .Select(signature => signature.Signature);

            foreach (var signature in signatures)
            {
                Assert.All(
                    signature,
                    character => Assert.True(
                        character is 'A' or 'D' or 'X'
                        || char.IsAsciiDigit(character)
                        || "-_.()[] ".Contains(character, StringComparison.Ordinal),
                        $"Signature '{signature}' contains '{character}'."));
            }
        }

        /// <remarks>
        /// Twenty-two shapes, so the table is capped and the remainder pooled. The two rarest share
        /// a count, which is what makes the ordinal tie-break observable rather than theoretical.
        /// </remarks>
        [Fact]
        public void ShapeTables_ReportTheTopTwentyPlusAPooledRow()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var hash = 0;

            for (var shape = 1; shape <= 22; shape++)
            {
                // Shape n is n letters long, so every shape is distinct; the commoner ones simply
                // occur more often.
                var occurrences = shape <= 20 ? 22 - shape : 1;

                for (var occurrence = 0; occurrence < occurrences; occurrence++)
                {
                    workspace.AddAssetWithFile(
                        sourceID,
                        Hash(++hash),
                        $"IMG-20260724-WA{new string('a', shape)}.jpg");
                }
            }

            var signatures = Analyse(workspace).SuffixShapeSignatures;

            Assert.Equal(21, signatures.Count);
            Assert.Equal("Other", signatures[^1].Signature);
            Assert.Equal(2, signatures[^1].FileCount);

            Assert.Equal(
                signatures.Take(20).Select(signature => signature.FileCount).OrderByDescending(count => count),
                signatures.Take(20).Select(signature => signature.FileCount));
        }

        // ---------------------------------------------------------------------------------------
        // Per-asset consistency.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Pinned decision 5. Three outcomes, none of them normalised into agreement: a value seen
        /// at two widths is its own finding, distinguishable afterwards from a genuine numbering
        /// disagreement.
        /// </remarks>
        [Theory]
        [InlineData("IMG-20260724-WA0004.jpg", "IMG-20260725-WA0004.jpg", 1, 0, 0)]
        [InlineData("IMG-20260724-WA0004.jpg", "IMG-20260725-WA4.jpg", 0, 1, 0)]
        [InlineData("IMG-20260724-WA0004.jpg", "IMG-20260725-WA0005.jpg", 0, 0, 1)]
        [InlineData("IMG-20260724-WA0000.jpg", "IMG-20260725-WA00.jpg", 0, 1, 0)]
        public void NumericObservationsOnOneAsset_FallIntoThreeOutcomes(
            string firstName,
            string secondName,
            int exactSame,
            int sameValueDifferentWidth,
            int differentValue)
        {
            using var workspace = new NameCensusTestWorkspace();
            var first = workspace.AddMediaSource("WhatsAppMediaDirectory", "First");
            var second = workspace.AddMediaSource("WhatsAppMediaDirectory", "Second");

            var assetID = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(first, assetID, HashA, firstName);
            workspace.AddMediaFile(second, assetID, HashA, secondName);

            var agreement = Analyse(workspace).NumericAgreement;

            Assert.Equal(exactSame, agreement.ExactSameDigitString);
            Assert.Equal(sameValueDifferentWidth, agreement.SameValueDifferentWidth);
            Assert.Equal(differentValue, agreement.DifferentNumericValue);
            Assert.Equal(1, agreement.Total);
        }

        /// <remarks>
        /// The precedence rule made observable: three observations where two agree exactly and the
        /// third differs only in width land in the middle outcome, while adding a genuinely
        /// different value moves the same asset to the last one.
        /// </remarks>
        [Theory]
        [InlineData("IMG-20260726-WA0003.jpg", 0, 1, 0, 2)]
        [InlineData("IMG-20260726-WA0004.jpg", 0, 0, 1, 3)]
        public void AnAssetWithSeveralObservations_LandsInExactlyOneOutcome(
            string thirdName,
            int exactSame,
            int sameValueDifferentWidth,
            int differentValue,
            int distinctDigitStrings)
        {
            using var workspace = new NameCensusTestWorkspace();
            var first = workspace.AddMediaSource("WhatsAppMediaDirectory", "First");
            var second = workspace.AddMediaSource("WhatsAppMediaDirectory", "Second");
            var third = workspace.AddMediaSource("WhatsAppMediaDirectory", "Third");

            var assetID = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(first, assetID, HashA, "IMG-20260724-WA0003.jpg");
            workspace.AddMediaFile(second, assetID, HashA, "IMG-20260725-WA3.jpg");
            workspace.AddMediaFile(third, assetID, HashA, thirdName);

            var census = Analyse(workspace);

            Assert.Equal(exactSame, census.NumericAgreement.ExactSameDigitString);
            Assert.Equal(sameValueDifferentWidth, census.NumericAgreement.SameValueDifferentWidth);
            Assert.Equal(differentValue, census.NumericAgreement.DifferentNumericValue);

            // The uncompressed facts, reported beside the outcome so nothing it hides is lost:
            // three observations always mean three digit strings unless two of them are identical.
            var bucket = Assert.Single(census.DistinctDigitStringsPerAsset);

            Assert.Equal(distinctDigitStrings, bucket.DistinctCount);
            Assert.Equal(1, bucket.AssetCount);
        }

        [Fact]
        public void AnAssetWithOneObservation_IsNotCountedAsAgreeingOrDisagreeing()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var census = Analyse(workspace);

            Assert.Equal(0, census.NumericAgreement.Total);
            Assert.Equal(1, census.AssetsWithAnyPurelyNumericSuffix);
            Assert.Equal(0, census.AssetsWithNoPurelyNumericSuffix);
        }

        /// <remarks>
        /// Pinned decision 6. Windows file systems being case-insensitive, two copies whose names
        /// differ only in case would otherwise read as an asset carrying two genuinely different
        /// names.
        /// </remarks>
        [Fact]
        public void NamesDifferingOnlyByCase_AreCountedSeparately()
        {
            using var workspace = new NameCensusTestWorkspace();
            var first = workspace.AddMediaSource("WhatsAppMediaDirectory", "First");
            var second = workspace.AddMediaSource("WhatsAppMediaDirectory", "Second");

            var assetID = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(first, assetID, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddMediaFile(second, assetID, HashA, "img-20260724-WA0004.jpg");

            var census = Analyse(workspace);

            Assert.Equal(0, census.AssetsWithOneDistinctFileName);
            Assert.Equal(1, census.AssetsWithSeveralDistinctFileNames);
            Assert.Equal(1, census.AssetsWhoseNamesDifferOnlyByCase);
        }

        /// <remarks>
        /// Pinned decision 7. The zero-byte payload is the largest single-name cluster a real
        /// archive holds, so it is counted like any other asset and reported separately rather than
        /// being dropped.
        /// </remarks>
        [Fact]
        public void TheZeroByteAsset_IsIncludedAndReportedSeparately()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            var assetID = workspace.AddMediaAsset(HashA, sizeBytes: 0);
            workspace.AddMediaFile(sourceID, assetID, HashA, "IMG-20260724-WA0004.jpg", sizeBytes: 0);
            workspace.AddMediaFile(sourceID, assetID, HashA, "IMG-20260725-WA0005.jpg", sizeBytes: 0);

            var census = Analyse(workspace);

            Assert.Equal(1, census.ZeroByteAssetCount);
            Assert.Equal(2, census.ZeroBytePhysicalFileCount);
            Assert.Equal(2, census.ZeroByteAssetDistinctNameCount);
            Assert.Equal(1, census.AssetsWithSeveralDistinctFileNames);
        }

        // ---------------------------------------------------------------------------------------
        // The undated names.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The case that explains a null date on an otherwise WhatsApp-shaped name: the complete
        /// structure is present and the eight digits are not a real date. The earlier features
        /// cannot identify it, because they ask about any eight-digit run and may be satisfied by
        /// different runs in the same name.
        /// </remarks>
        [Fact]
        public void AFullStructureWithAnImpossibleDate_IsIdentifiedByItsOwnFeature()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20261332-WA0004.jpg");

            var features = Analyse(workspace).UndatedFeatures;

            Assert.Equal(1, features.NameCount);
            Assert.Equal(1, features.ContainsEightDigitRun);
            Assert.Equal(1, features.ContainsHyphenPrefixedEightDigitRun);
            Assert.Equal(0, features.ContainsValidCalendarDateRun);
            Assert.Equal(1, features.ContainsDashWAMarker);
            Assert.Equal(1, features.ContainsWAFollowedByDigits);
            Assert.Equal(1, features.ContainsFullStructure);
            Assert.Equal(1, features.ContainsFullStructureWithInvalidDate);
            Assert.Equal(0, features.MatchedNoFeature);
        }

        /// <remarks>
        /// Eight digits at the start of a name carry a real date and the marker follows them, yet
        /// the committed rule refuses it for want of a preceding hyphen. The features record that
        /// shape without explaining it away.
        /// </remarks>
        [Fact]
        public void AValidDateWithNoPrecedingHyphen_IsRecordedAsSuch()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "20260724-WA0004.jpg");

            var features = Analyse(workspace).UndatedFeatures;

            Assert.Equal(1, features.ContainsEightDigitRun);
            Assert.Equal(0, features.ContainsHyphenPrefixedEightDigitRun);
            Assert.Equal(1, features.ContainsValidCalendarDateRun);
            Assert.Equal(1, features.ContainsDashWAMarker);
            Assert.Equal(0, features.ContainsFullStructure);
            Assert.Equal(0, features.ContainsFullStructureWithInvalidDate);
        }

        [Fact]
        public void ANameWithNoneOfTheFeatures_IsCountedAsMatchingNone()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "holiday photo.png");

            var features = Analyse(workspace).UndatedFeatures;

            Assert.Equal(1, features.NameCount);
            Assert.Equal(1, features.MatchedNoFeature);
            Assert.Equal(0, features.ContainsEightDigitRun);
        }

        /// <remarks>
        /// The consistency check the brief calls for: inside a WhatsApp media directory a null date
        /// means no occurrence of the full structure can hold a valid date, so these two counts must
        /// agree. A divergence would mean this census and the committed classifier disagree.
        /// </remarks>
        [Fact]
        public void WithinAWhatsAppSource_FullStructureCountsAgreeWithTheInvalidDateCount()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20261332-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "IMG-20230229-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, HashC, "holiday photo.png");

            var features = Analyse(workspace).UndatedFeatures;

            Assert.Equal(2, features.ContainsFullStructure);
            Assert.Equal(
                features.ContainsFullStructure, features.ContainsFullStructureWithInvalidDate);
        }

        /// <remarks>
        /// A source whose type is not a WhatsApp media directory has no date read for any of its
        /// files, however they are named. Reporting the type is what keeps that from looking like a
        /// naming finding.
        /// </remarks>
        [Fact]
        public void ASourceOfAnotherType_HasNoDatesAndIsReportedAsSuch()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource("GenericMediaDirectory", "Generic");
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var census = Analyse(workspace);
            var source = Assert.Single(census.MediaSources);

            Assert.False(source.IsWhatsAppMediaDirectory);
            Assert.Equal("GenericMediaDirectory", source.SourceType);
            Assert.Equal(1, source.MediaFileWithNullFileDateCount);
            Assert.Equal(0, census.MarkerLocatedCount);

            // The name carries a real date and the full structure; only the source type kept it out,
            // which is why the invalid-date feature stays at zero here.
            Assert.Equal(1, census.UndatedFeatures.ContainsValidCalendarDateRun);
            Assert.Equal(1, census.UndatedFeatures.ContainsFullStructure);
            Assert.Equal(0, census.UndatedFeatures.ContainsFullStructureWithInvalidDate);
        }

        [Fact]
        public void PerSourceFigures_AreReportedInSourceOrder()
        {
            using var workspace = new NameCensusTestWorkspace();
            var first = workspace.AddMediaSource("WhatsAppMediaDirectory", "First");
            var second = workspace.AddMediaSource("WhatsAppMediaDirectory", "Second");

            workspace.AddAssetWithFile(first, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(second, HashB, "holiday.png");
            workspace.AddAssetWithFile(second, HashC, "IMG-20260725-WA0005.jpg");

            var sources = Analyse(workspace).MediaSources;

            Assert.Equal([first, second], sources.Select(source => source.MediaSourceID));
            Assert.Equal(1, sources[0].MediaFileCount);
            Assert.Equal(1, sources[0].MediaFileWithFileDateCount);
            Assert.Equal(2, sources[1].MediaFileCount);
            Assert.Equal(1, sources[1].MediaFileWithNullFileDateCount);
        }

        [Fact]
        public void TheSameLogicalDataInsertedInADifferentOrder_ProducesIdenticalOutput() =>
            Assert.Equal(DescribeCensus(ascending: true), DescribeCensus(ascending: false));

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static FileNameSuffixCensus Analyse(NameCensusTestWorkspace workspace) =>
            new FileNameSuffixCensusService().Analyse(workspace.DatabasePath);

        private static int Count(FileNameSuffixCensus census, SuffixSyntaxClass suffixClass) =>
            census.SuffixClasses.Single(counts => counts.SuffixClass == suffixClass).FileCount;

        private static string Hash(int number) =>
            number.ToString("X4", CultureInfo.InvariantCulture).PadLeft(64, '0');

        private static string DescribeCensus(bool ascending)
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            string[] names =
            [
                "IMG-20260724-WA0004.jpg",
                "IMG-20260725-WA0005-2.jpg",
                "IMG-20260726-WAxyz.jpg",
                "holiday photo.png",
            ];

            var ordered = ascending ? names : names.Reverse().ToArray();
            var hash = 0;

            foreach (var name in ordered)
            {
                workspace.AddAssetWithFile(sourceID, Hash(++hash), name);
            }

            var census = Analyse(workspace);

            return string.Join(
                "|",
                [
                    ..census.SuffixClasses.Select(
                        suffixClass => $"{suffixClass.SuffixClass}={suffixClass.FileCount}"),
                    ..census.SuffixShapeSignatures.Select(
                        signature => $"{signature.Signature}={signature.FileCount}"),
                    $"undated={census.UndatedFeatures.NameCount}",
                    $"widths={census.PurelyNumericWidths.DominantWidth}",
                ]);
        }
    }
}
