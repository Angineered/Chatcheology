using System.Globalization;
using System.Text;
using Chatcheology.Data.Media;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// Tests what the sequence scope census measures, and what it refuses to conclude.
    /// </summary>
    /// <remarks>
    /// Every name here is invented. No recovered file name, path or hash from a real archive appears in
    /// this project.
    /// <para>
    /// The census answers one question — at what scope, if any, does the four-digit sequence behave
    /// consistently — and nothing here narrows a candidate, compares anything to message order, or reads
    /// a token as an attachment ordinal.
    /// </para>
    /// </remarks>
    public class WaSequenceScopeCensusTests
    {
        private const string HashA = "AAAA000000000000000000000000000000000000000000000000000000000001";
        private const string HashB = "BBBB000000000000000000000000000000000000000000000000000000000002";
        private const string HashC = "CCCC000000000000000000000000000000000000000000000000000000000003";
        private const string HashEmpty = "E300000000000000000000000000000000000000000000000000000000000000";

        // ---------------------------------------------------------------------------------------
        // Supported extraction.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("IMG-20260724-WA0004.jpg", "0004")]
        [InlineData("VID-20220128-WA0000.mp4", "0000")]
        [InlineData("PTT-20240229-WA0158.opus", "0158")]
        [InlineData("AUD-20250101-WA9999.m4a", "9999")]
        public void AFourDigitSuffix_IsSupportedAndItsTokenPreserved(string fileName, string token)
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, fileName);

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(1, census.Reconciliation.SupportedFileCount);
            Assert.Equal(0, census.Reconciliation.UnsupportedDatedFileCount);
            Assert.Equal(token, Assert.Single(census.TokenCurve.Rows).Token);
        }

        /// <remarks>
        /// The decorated, short, long, empty and non-numeric suffixes together. A rule written for any of
        /// them would be a rule fitted to noise, so each is dated and unsupported rather than being
        /// coerced into a token.
        /// </remarks>
        [Theory]
        [InlineData("IMG-20260724-WA0004-2.jpg")]
        [InlineData("IMG-20260724-WA004.jpg")]
        [InlineData("IMG-20260724-WA00045.jpg")]
        [InlineData("IMG-20260724-WA.jpg")]
        [InlineData("IMG-20260724-WAxyz.jpg")]
        [InlineData("IMG-20260724-WA0004 (1).jpg")]
        public void ASuffixThatIsNotExactlyFourDigits_IsDatedButUnsupported(string fileName)
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, fileName);

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(1, census.Reconciliation.DatedFileCount);
            Assert.Equal(0, census.Reconciliation.SupportedFileCount);
            Assert.Equal(1, census.Reconciliation.UnsupportedDatedFileCount);
            Assert.Empty(census.TokenCurve.Rows);
        }

        /// <remarks>
        /// A document with no extension at all is a real WhatsApp shape, and the rule Stage A settled
        /// keeps its whole remainder rather than cutting at a full stop that is not there.
        /// </remarks>
        [Fact]
        public void AFileWithNoRecordedExtension_KeepsItsWholeRemainderAndIsSupported()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "DOC-20230820-WA0016");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(1, census.Reconciliation.SupportedFileCount);
            Assert.Equal(
                1, census.Reconciliation.SupportedObservationsFromNullExtensionFiles);
            Assert.Equal("0016", Assert.Single(census.TokenCurve.Rows).Token);
        }

        /// <remarks>
        /// The token is carried as a number and rendered back through invariant <c>D4</c>. That is a
        /// lossless round trip only because the grammar fixes the width at four, so it is proved over the
        /// whole domain rather than asserted.
        /// </remarks>
        [Fact]
        public void EveryFourDigitTokenSurvivesTheRoundTrip()
        {
            for (var value = 0; value < 10_000; value++)
            {
                var text = value.ToString("D4", CultureInfo.InvariantCulture);

                Assert.Equal(4, text.Length);
                Assert.Equal(value, int.Parse(text, CultureInfo.InvariantCulture));
            }
        }

        [Fact]
        public void TokensAreOrderedByValueWithLeadingZeroesPreserved()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0010.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "IMG-20260724-WA0000.jpg");
            workspace.AddAssetWithFile(sourceID, HashC, "IMG-20260724-WA0158.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(
                ["0000", "0010", "0158"],
                census.TokenCurve.Rows.Select(row => row.Token));
            Assert.Equal("0000", census.TokenCurve.MinimumToken);
            Assert.Equal("0158", census.TokenCurve.MaximumToken);
            Assert.False(census.TokenCurve.ObservedSetIsContiguous);
        }

        // ---------------------------------------------------------------------------------------
        // File names, compared ordinally.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Stage A established that no single payload carries names differing only by case. It
        /// established nothing about names compared across different payloads, which is what a scoped key
        /// does, so the ignore-case figure is reported and never gated on zero.
        /// </remarks>
        [Fact]
        public void NamesDifferingOnlyByCaseAcrossAssets_AreTwoNamesAndReportedAsSuch()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "img-20260724-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));
            var level = Level(census, ScopeLevel.DateToken);

            Assert.Equal(1, level.KeyCount);
            Assert.Equal(2, level.MaximumDistinctFileNamesOnOneKey);
            Assert.Equal(1, level.KeysWhereIgnoringCaseChangesTheNameCount);
        }

        // ---------------------------------------------------------------------------------------
        // The scope ladder.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The case the brief is written around. One colliding key at the pooled date level splits into
        /// two colliding keys once the device group is added, so the descriptive totals rise while every
        /// gated quantity stays exactly level. Reading a rise in R1, R2 or R3 as a finding would be
        /// reading an artefact of refinement.
        /// </remarks>
        [Fact]
        public void RefinementCanRaiseTheDescriptiveTotalsWhileTheGatesStayLevel()
        {
            using var workspace = new NameCensusTestWorkspace();
            var census = TwoAssetsInTwoDeviceGroups(workspace);

            var pooled = Level(census, ScopeLevel.DateToken).Ambiguity;
            var byGroup = Level(census, ScopeLevel.DeviceGroupDateToken).Ambiguity;
            var bySource = Level(census, ScopeLevel.SourceDateToken).Ambiguity;

            Assert.Equal(1, pooled.MultiAssetKeyCount);
            Assert.Equal(2, byGroup.MultiAssetKeyCount);
            Assert.Equal(2, bySource.MultiAssetKeyCount);

            Assert.Equal(1, pooled.AssetPairMagnitude);
            Assert.Equal(2, byGroup.AssetPairMagnitude);

            Assert.Equal(1, pooled.ExcessAmbiguity);
            Assert.Equal(2, byGroup.ExcessAmbiguity);

            foreach (var metrics in new[] { pooled, byGroup, bySource })
            {
                Assert.Equal(4, metrics.FilesInMultiAssetKeys);
                Assert.Equal(2, metrics.MaximumDistinctAssetsOnOneKey);
                Assert.Equal(2, metrics.AssetsInMultiAssetKeys);
            }
        }

        /// <remarks>
        /// The gates hold on every fixture that produces a collision at all, on both variants. They are
        /// theorems under refinement rather than observations, so a violation would be a defect in the
        /// census; the checker is private and this is how it is exercised.
        /// </remarks>
        [Fact]
        public void TheGatedQuantitiesNeverRiseAsScopeIsAdded()
        {
            using var workspace = new NameCensusTestWorkspace();
            var census = TwoAssetsInTwoDeviceGroups(workspace);

            var levels = new[]
            {
                ScopeLevel.Token, ScopeLevel.DateToken, ScopeLevel.DeviceGroupDateToken,
                ScopeLevel.SourceDateToken,
            }.Select(level => Level(census, level)).ToList();

            for (var index = 1; index < levels.Count; index++)
            {
                foreach (var select in new Func<ScopeKeyUniqueness, ScopeAmbiguityMetrics>[]
                         {
                             level => level.Ambiguity,
                             level => level.AmbiguityExcludingZeroByte,
                         })
                {
                    var above = select(levels[index - 1]);
                    var below = select(levels[index]);

                    Assert.True(above.FilesInMultiAssetKeys >= below.FilesInMultiAssetKeys);
                    Assert.True(
                        above.MaximumDistinctAssetsOnOneKey >= below.MaximumDistinctAssetsOnOneKey);
                    Assert.True(above.AssetsInMultiAssetKeys >= below.AssetsInMultiAssetKeys);
                }
            }
        }

        /// <remarks>
        /// The token-only level's summary is reported in full, pair magnitude included; only its
        /// cross-tab is omitted, because keys spanning a large share of the archive's payloads produce a
        /// table with no readable structure.
        /// </remarks>
        [Fact]
        public void TheTokenOnlyLevel_ReportsItsSummaryButNoCrossTab()
        {
            using var workspace = new NameCensusTestWorkspace();
            var census = TwoAssetsInTwoDeviceGroups(workspace);

            Assert.Equal(1, Level(census, ScopeLevel.Token).Ambiguity.AssetPairMagnitude);
            Assert.DoesNotContain(census.Collisions, collision => collision.Level == ScopeLevel.Token);
            Assert.Equal(
                [ScopeLevel.DateToken, ScopeLevel.DeviceGroupDateToken, ScopeLevel.SourceDateToken],
                census.Collisions.Select(collision => collision.Level));
        }

        // ---------------------------------------------------------------------------------------
        // Collision characterisation.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// One recovered name over two payloads says nothing about the counter — it is not evidence that
        /// a token was reused across different names — but it is still two payloads behind one scoped
        /// token, so it stays in every ambiguity total rather than being subtracted from them.
        /// </remarks>
        [Fact]
        public void OneNameOverTwoPayloads_IsRecordedAsItsShapeAndStaysInTheTotals()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "IMG-20260724-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));
            var collision = Collision(census, ScopeLevel.DateToken);

            Assert.Equal(1, collision.MultiAssetKeyCount);
            Assert.Equal(1, collision.KeysWithOneDistinctFileName);
            Assert.Equal(0, collision.KeysWithSeveralDistinctFileNames);
            Assert.Equal(1, collision.KeysWhereExtensionsAreAllEqual);

            var metrics = Level(census, ScopeLevel.DateToken).Ambiguity;

            Assert.Equal(1, metrics.MultiAssetKeyCount);
            Assert.Equal(2, metrics.FilesInMultiAssetKeys);
            Assert.Equal(2, metrics.AssetsInMultiAssetKeys);
        }

        [Fact]
        public void ACollisionWhoseMembersRecordDifferentExtensions_IsSeparatedFromOneThatDoesNot()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "VID-20260724-WA0004.mp4");
            workspace.AddAssetWithFile(sourceID, HashC, "IMG-20260725-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, Hash(4), "AUD-20260725-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));
            var collision = Collision(census, ScopeLevel.DateToken);

            Assert.Equal(2, collision.MultiAssetKeyCount);
            Assert.Equal(1, collision.KeysWhereExtensionsDiffer);
            Assert.Equal(1, collision.KeysWhereExtensionsAreAllEqual);
            Assert.Equal(2, collision.KeysWithSeveralDistinctFileNames);
        }

        // ---------------------------------------------------------------------------------------
        // The empty payload.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Every empty file has the same hash, so they deduplicate to one payload standing behind however
        /// many copies the acquisition produced. Left inside a collision figure it would carry that
        /// figure, so both variants are produced from the same walk.
        /// </remarks>
        [Fact]
        public void AZeroBytePayloadInACollision_IsCountedInOneVariantAndNotTheOther()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var empty = workspace.AddMediaAsset(HashEmpty, sizeBytes: 0);
            workspace.AddMediaFile(sourceID, empty, HashEmpty, "VID-20260724-WA0004.mp4", sizeBytes: 0);

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));
            var level = Level(census, ScopeLevel.DateToken);

            Assert.Equal(1, level.Ambiguity.MultiAssetKeyCount);
            Assert.Equal(2, level.Ambiguity.AssetsInMultiAssetKeys);
            Assert.Equal(0, level.AmbiguityExcludingZeroByte.MultiAssetKeyCount);
            Assert.Equal(0, level.AmbiguityExcludingZeroByte.AssetsInMultiAssetKeys);
            Assert.Equal(1, level.MultiAssetKeysInvolvingZeroByteAsset);

            Assert.Equal(1, census.ZeroByte.ZeroByteAssetCount);
            Assert.Equal(1, census.ZeroByte.PhysicalFileCount);
            Assert.Equal(1, census.ZeroByte.SupportedFileCount);
            Assert.Null(census.ZeroByte.AllSupportedObservationsCarryTheSameToken);
        }

        [Fact]
        public void AZeroBytePayloadCarryingTwoTokens_IsReportedAsDisagreeing()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            var empty = workspace.AddMediaAsset(HashEmpty, sizeBytes: 0);
            workspace.AddMediaFile(sourceID, empty, HashEmpty, "IMG-20260724-WA0004.jpg", sizeBytes: 0);
            workspace.AddMediaFile(sourceID, empty, HashEmpty, "IMG-20260724-WA0005.jpg", sizeBytes: 0);

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(2, census.ZeroByte.SupportedFileCount);
            Assert.Equal(2, census.ZeroByte.DistinctTokenCount);
            Assert.Equal(2, census.ZeroByte.DistinctFileNameCount);
            Assert.False(census.ZeroByte.AllSupportedObservationsCarryTheSameToken);
        }

        // ---------------------------------------------------------------------------------------
        // The two date sets.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void WhenEveryDateCarriesBothPopulations_TheTwoDateSetsCoincide()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "IMG-20260725-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(2, census.DatePopulation.SupportedDateCount);
            Assert.Equal(2, census.DatePopulation.DatedNonZeroByteDateCount);
            Assert.Equal(2, census.DatePopulation.IntersectionCount);
            Assert.Equal(0, census.DatePopulation.SupportedOnlyCount);
            Assert.Equal(0, census.DatePopulation.DatedNonZeroByteOnlyCount);
        }

        /// <remarks>
        /// A date whose only supported file is an empty payload is in the supported set and not in the
        /// dated-non-empty set. It has to be accounted for by that payload, and the accounting is checked
        /// rather than asserted in prose.
        /// </remarks>
        [Fact]
        public void ADateCarriedOnlyByTheEmptyPayload_IsAccountedForByIt()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var empty = workspace.AddMediaAsset(HashEmpty, sizeBytes: 0);
            workspace.AddMediaFile(sourceID, empty, HashEmpty, "IMG-20260801-WA0004.jpg", sizeBytes: 0);

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(2, census.DatePopulation.SupportedDateCount);
            Assert.Equal(1, census.DatePopulation.DatedNonZeroByteDateCount);
            Assert.Equal(1, census.DatePopulation.IntersectionCount);
            Assert.Equal(1, census.DatePopulation.SupportedOnlyCount);
            Assert.Equal(1, census.DatePopulation.SupportedOnlyAccountedByZeroByteAsset);
            Assert.Equal(0, census.DatePopulation.DatedNonZeroByteOnlyCount);
        }

        /// <remarks>
        /// The mirror case: a date carried only by an unsupported dated file is in the dated-non-empty set
        /// and not in the supported set.
        /// </remarks>
        [Fact]
        public void ADateCarriedOnlyByAnUnsupportedFile_IsAccountedForByIt()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "IMG-20260801-WA0004-2.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(1, census.DatePopulation.SupportedDateCount);
            Assert.Equal(2, census.DatePopulation.DatedNonZeroByteDateCount);
            Assert.Equal(1, census.DatePopulation.IntersectionCount);
            Assert.Equal(1, census.DatePopulation.DatedNonZeroByteOnlyCount);
            Assert.Equal(
                1, census.DatePopulation.DatedNonZeroByteOnlyAccountedByUnsupportedFiles);
            Assert.Equal(0, census.DatePopulation.SupportedOnlyCount);
        }

        // ---------------------------------------------------------------------------------------
        // Locality of disagreement.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// One payload per class, built to sit in exactly one of them. Most-local-wins means the classes
        /// partition the population, so the four counts and their total are asserted together.
        /// </remarks>
        [Fact]
        public void EachLocalityClassIsReachedByItsOwnShape()
        {
            using var workspace = new NameCensusTestWorkspace();
            var legacy = workspace.AddMediaSource();
            var current = workspace.AddMediaSource();
            var other = workspace.AddMediaSource();

            // D: two tokens inside one source on one date.
            var withinSource = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(legacy, withinSource, HashA, "IMG-20260724-WA0001.jpg");
            workspace.AddMediaFile(legacy, withinSource, HashA, "IMG-20260724-WA0002.jpg");

            // C: two tokens inside one device group on one date, across its two sources.
            var withinGroup = workspace.AddMediaAsset(HashB);
            workspace.AddMediaFile(legacy, withinGroup, HashB, "IMG-20260724-WA0003.jpg");
            workspace.AddMediaFile(current, withinGroup, HashB, "IMG-20260724-WA0004.jpg");

            // B: two tokens on one date, across device groups.
            var acrossGroups = workspace.AddMediaAsset(HashC);
            workspace.AddMediaFile(legacy, acrossGroups, HashC, "IMG-20260724-WA0005.jpg");
            workspace.AddMediaFile(other, acrossGroups, HashC, "IMG-20260724-WA0006.jpg");

            // A: two tokens, but never on the same date.
            var acrossDates = workspace.AddMediaAsset(Hash(4));
            workspace.AddMediaFile(legacy, acrossDates, Hash(4), "IMG-20260724-WA0007.jpg");
            workspace.AddMediaFile(legacy, acrossDates, Hash(4), "IMG-20260725-WA0008.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(
                workspace, (legacy, 1), (current, 1), (other, 2));
            var locality = census.NumericValueLocality;

            Assert.Equal(4, locality.AssetsWithSeveralDistinctTokens);
            Assert.Equal(1, locality.WithinOneSourceAndDate);
            Assert.Equal(1, locality.WithinOneDeviceGroupAndDate);
            Assert.Equal(1, locality.AcrossDeviceGroupsOnly);
            Assert.Equal(1, locality.AcrossDatesOnly);
            Assert.Equal(locality.AssetsWithSeveralDistinctTokens, locality.ClassTotal);
        }

        /// <remarks>
        /// A payload disagreeing at two scopes at once lands in the more local of them, once, rather than
        /// being counted twice or landing in the coarser class.
        /// </remarks>
        [Fact]
        public void APayloadDisagreeingAtTwoScopes_LandsInTheMoreLocalClassOnly()
        {
            using var workspace = new NameCensusTestWorkspace();
            var legacy = workspace.AddMediaSource();
            var other = workspace.AddMediaSource();

            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(legacy, asset, HashA, "IMG-20260724-WA0001.jpg");
            workspace.AddMediaFile(legacy, asset, HashA, "IMG-20260724-WA0002.jpg");
            workspace.AddMediaFile(other, asset, HashA, "IMG-20260725-WA0003.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (legacy, 1), (other, 2));
            var locality = census.NumericValueLocality;

            Assert.Equal(1, locality.AssetsWithSeveralDistinctTokens);
            Assert.Equal(1, locality.WithinOneSourceAndDate);
            Assert.Equal(0, locality.WithinOneDeviceGroupAndDate);
            Assert.Equal(0, locality.AcrossDeviceGroupsOnly);
            Assert.Equal(0, locality.AcrossDatesOnly);
        }

        [Fact]
        public void APayloadCarryingOneTokenEverywhere_IsNotInTheDisagreementPopulation()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(sourceID, asset, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddMediaFile(sourceID, asset, HashA, "IMG-20260724-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(0, census.NumericValueLocality.AssetsWithSeveralDistinctTokens);
        }

        // ---------------------------------------------------------------------------------------
        // Same-payload agreement across sources, and its pair statistics.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Three sources on one date contribute three unordered pairs, which is why the pair figures are
        /// larger than the payload-and-date figures and are labelled separately. Two of those sources
        /// share a device group; the third does not.
        /// </remarks>
        [Fact]
        public void ThreeSourcesOnOneDate_ContributeThreeUnorderedPairs()
        {
            using var workspace = new NameCensusTestWorkspace();
            var legacy = workspace.AddMediaSource();
            var current = workspace.AddMediaSource();
            var other = workspace.AddMediaSource();

            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(legacy, asset, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddMediaFile(current, asset, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddMediaFile(other, asset, HashA, "IMG-20260724-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(
                workspace, (legacy, 1), (current, 1), (other, 2));
            var agreement = census.SameAssetAgreement;

            Assert.Equal(1, agreement.AssetDateCount);
            Assert.Equal(1, agreement.AssetDatesInSeveralSources);
            Assert.Equal(1, agreement.AssetDatesInSeveralDeviceGroups);
            Assert.Equal(1, agreement.MultiSourceAssetDatesAllTokensEqual);

            Assert.Equal(3, agreement.SourcePairCount);
            Assert.Equal(1, agreement.SameDeviceGroupPairsAllTokensEqual);
            Assert.Equal(2, agreement.CrossDeviceGroupPairsAllTokensEqual);
            Assert.Equal(0, agreement.SameDeviceGroupPairsTokensDiffer);
            Assert.Equal(0, agreement.CrossDeviceGroupPairsTokensDiffer);
        }

        /// <remarks>
        /// A pair disagrees when the union of what its two sources contributed holds more than one token,
        /// which is the definition the brief settled and the reason no per-source token set is needed.
        /// </remarks>
        [Fact]
        public void APairWhoseUnionHoldsTwoTokens_IsCountedAsDiffering()
        {
            using var workspace = new NameCensusTestWorkspace();
            var legacy = workspace.AddMediaSource();
            var other = workspace.AddMediaSource();

            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(legacy, asset, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddMediaFile(other, asset, HashA, "IMG-20260724-WA0005.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (legacy, 1), (other, 2));
            var agreement = census.SameAssetAgreement;

            Assert.Equal(1, agreement.SourcePairCount);
            Assert.Equal(1, agreement.CrossDeviceGroupPairsTokensDiffer);
            Assert.Equal(0, agreement.CrossDeviceGroupPairsAllTokensEqual);
            Assert.Equal(1, agreement.MultiSourceAssetDatesTokensDiffer);
            Assert.Equal(2, agreement.MaximumDistinctTokensOnOneAssetDateAcrossSources);
        }

        [Fact]
        public void APayloadInOneSourceOnly_ContributesNoPair()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(1, census.SameAssetAgreement.AssetDatesInOneSource);
            Assert.Equal(0, census.SameAssetAgreement.SourcePairCount);
        }

        // ---------------------------------------------------------------------------------------
        // Duplicate copies inside one scope and date.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("IMG-20260724-WA0004.jpg", 1, 0)]
        [InlineData("IMG-20260724-WA0005.jpg", 0, 1)]
        public void SeveralCopiesInOneSourceAndDate_AreReportedAsAgreeingOrNot(
            string secondName, int expectedSame, int expectedDiffer)
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(sourceID, asset, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddMediaFile(sourceID, asset, HashA, secondName);

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));
            var duplicates = census.DuplicateCopies.Single(
                agreement => agreement.Scope == ScopeLevel.SourceDate);

            Assert.Equal(1, duplicates.GroupsWithSeveralSupportedCopies);
            Assert.Equal(expectedSame, duplicates.GroupsWhereAllCopiesCarryTheSameToken);
            Assert.Equal(expectedDiffer, duplicates.GroupsWhereCopiesCarryDifferentTokens);
        }

        /// <remarks>
        /// Two stores of one handset make a device-group grouping where neither source has several copies
        /// of its own, which is the case that distinguishes the two denominators.
        /// </remarks>
        [Fact]
        public void CopiesSplitAcrossTwoSourcesOfOneGroup_CountOnlyAtTheGroupLevel()
        {
            using var workspace = new NameCensusTestWorkspace();
            var legacy = workspace.AddMediaSource();
            var current = workspace.AddMediaSource();

            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(legacy, asset, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddMediaFile(current, asset, HashA, "IMG-20260724-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (legacy, 1), (current, 1));

            Assert.Equal(
                0,
                census.DuplicateCopies
                    .Single(agreement => agreement.Scope == ScopeLevel.SourceDate)
                    .GroupsWithSeveralSupportedCopies);
            Assert.Equal(
                1,
                census.DuplicateCopies
                    .Single(agreement => agreement.Scope == ScopeLevel.DeviceGroupDate)
                    .GroupsWithSeveralSupportedCopies);
        }

        // ---------------------------------------------------------------------------------------
        // Ranges, continuity and gaps.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// A complete run from <c>0000</c> and a two-token group four apart, banded by size so the second
        /// cannot be read through the first. Nothing here says why a value inside a range is absent.
        /// </remarks>
        [Fact]
        public void ContinuityAndGapsAreBandedByGroupSize()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithFile(sourceID, Hash(1), "IMG-20260724-WA0000.jpg");
            workspace.AddAssetWithFile(sourceID, Hash(2), "IMG-20260724-WA0001.jpg");
            workspace.AddAssetWithFile(sourceID, Hash(3), "IMG-20260724-WA0002.jpg");

            workspace.AddAssetWithFile(sourceID, Hash(4), "IMG-20260725-WA0000.jpg");
            workspace.AddAssetWithFile(sourceID, Hash(5), "IMG-20260725-WA0005.jpg");

            workspace.AddAssetWithFile(sourceID, Hash(6), "IMG-20260726-WA0007.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            var three = Continuity(census, ScopeLevel.SourceDate, GroupSizeBand.ThreeToFive);
            Assert.Equal(1, three.GroupCount);
            Assert.Equal(1, three.GroupsStartingAtLowestToken);
            Assert.Equal(1, three.GroupsWithNoInternalMissingTokens);
            Assert.Equal(1, three.GroupsWhereMaximumPlusOneEqualsDistinctCount);

            var two = Continuity(census, ScopeLevel.SourceDate, GroupSizeBand.Two);
            Assert.Equal(1, two.GroupCount);
            Assert.Equal(1, two.GroupsWithInternalMissingTokens);
            Assert.Equal(1, two.GapCountThreeToFive);
            Assert.Equal(4, two.TotalUnobservedValuesInsideRanges);

            var one = Continuity(census, ScopeLevel.SourceDate, GroupSizeBand.One);
            Assert.Equal(1, one.GroupCount);
            Assert.Equal(1, one.GroupsStartingHigher);
            Assert.Equal(1, one.GapCountZero);
            Assert.Equal(0, one.GroupsWhereMaximumPlusOneEqualsDistinctCount);
            Assert.Equal(1, one.GroupsContiguousButNotStartingAtLowestToken);
            Assert.Equal([7], one.ObservedMinima.Select(minimum => minimum.Value));
        }

        /// <remarks>
        /// The signed difference is reported exactly rather than banded, because it is two-sided: two
        /// tokens collapsing onto one payload and one token carrying two payloads are different findings.
        /// </remarks>
        [Fact]
        public void TheSignedAssetMinusTokenDifference_IsReportedExactlyAndOrderedNumerically()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            // One payload, two tokens: -1.
            var shared = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(sourceID, shared, HashA, "IMG-20260724-WA0001.jpg");
            workspace.AddMediaFile(sourceID, shared, HashA, "IMG-20260724-WA0002.jpg");

            // Two payloads, one token: +1.
            workspace.AddAssetWithFile(sourceID, HashB, "IMG-20260725-WA0001.jpg");
            workspace.AddAssetWithFile(sourceID, HashC, "IMG-20260725-WA0001.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(
                [-1, 1],
                census.SourceDateAssetsMinusTokens.Select(value => value.Value));
            Assert.All(
                census.SourceDateAssetsMinusTokens, value => Assert.Equal(1, value.Count));
        }

        /// <remarks>
        /// The lower median is stated rather than left to the reader, so an even population cannot produce
        /// a fractional count of files.
        /// </remarks>
        [Fact]
        public void AnEvenPopulationTakesTheLowerOfTheTwoMiddleValues()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            AddCopies(workspace, sourceID, "20260721", 1);
            AddCopies(workspace, sourceID, "20260722", 2);
            AddCopies(workspace, sourceID, "20260723", 3);
            AddCopies(workspace, sourceID, "20260724", 4);

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));
            var population = census.GroupPopulations.Single(
                group => group.Scope == ScopeLevel.Date && group.PartitionID is null);

            Assert.Equal(4, population.GroupCount);
            Assert.Equal(1, population.FilesPerGroup.Minimum);
            Assert.Equal(2, population.FilesPerGroup.Median);
            Assert.Equal(4, population.FilesPerGroup.Maximum);
            Assert.Equal(population.GroupCount, population.FilesPerGroup.BandTotal);
        }

        // ---------------------------------------------------------------------------------------
        // The token curve.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The headline diagnostic counts device-group-and-date groups rather than physical files, because
        /// two stores of one handset hold the same write twice and a file count would read that as
        /// frequency.
        /// </remarks>
        [Fact]
        public void TheCurveCountsDeviceGroupAndDateGroupsRatherThanOverlappingCopies()
        {
            using var workspace = new NameCensusTestWorkspace();
            var legacy = workspace.AddMediaSource();
            var current = workspace.AddMediaSource();

            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(legacy, asset, HashA, "IMG-20260724-WA0000.jpg");
            workspace.AddMediaFile(current, asset, HashA, "IMG-20260724-WA0000.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (legacy, 1), (current, 1));
            var row = Assert.Single(census.TokenCurve.Rows);

            Assert.Equal(2, row.PhysicalFileCount);
            Assert.Equal(1, row.DistinctMediaAssetCount);
            Assert.Equal(1, row.DistinctFileDateCount);
            Assert.Equal(1, row.DistinctDeviceGroupDateGroupCount);
            Assert.Equal(2, row.DistinctSourceDateGroupCount);

            Assert.Equal(1, census.TokenCurve.TotalDeviceGroupDateGroups);
            Assert.Equal(1, census.TokenCurve.GroupsContainingLowestToken);

            var perGroup = Assert.Single(row.PerDeviceGroup);
            Assert.Equal(1, perGroup.DeviceGroupID);
            Assert.Equal(2, perGroup.PhysicalFileCount);
            Assert.Equal(1, perGroup.DistinctFileDateCount);
        }

        [Fact]
        public void TheCurveDescribesItsOwnMonotonicityWithoutTestingIt()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            // 0000 on two dates, 0001 on one: a declining curve.
            workspace.AddAssetWithFile(sourceID, Hash(1), "IMG-20260724-WA0000.jpg");
            workspace.AddAssetWithFile(sourceID, Hash(2), "IMG-20260725-WA0000.jpg");
            workspace.AddAssetWithFile(sourceID, Hash(3), "IMG-20260724-WA0001.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(2, census.TokenCurve.TotalDeviceGroupDateGroups);
            Assert.Equal(2, census.TokenCurve.GroupsContainingLowestToken);
            Assert.Equal(1, census.TokenCurve.GroupsContainingSecondToken);
            Assert.Equal(0, census.TokenCurve.AdjacentAscentCount);
            Assert.Equal(0, census.TokenCurve.LargestAscent);
            Assert.Equal(0, census.TokenCurve.TokensExceedingLowestToken);
        }

        [Fact]
        public void AnAscendingCurveIsCountedRatherThanRejected()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithFile(sourceID, Hash(1), "IMG-20260724-WA0000.jpg");
            workspace.AddAssetWithFile(sourceID, Hash(2), "IMG-20260724-WA0001.jpg");
            workspace.AddAssetWithFile(sourceID, Hash(3), "IMG-20260725-WA0001.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(1, census.TokenCurve.AdjacentAscentCount);
            Assert.Equal(1, census.TokenCurve.LargestAscent);
            Assert.Equal(1, census.TokenCurve.TokensExceedingLowestToken);
        }

        // ---------------------------------------------------------------------------------------
        // Token reuse.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TokenReuseIsReportedPooledAndPerPartition()
        {
            using var workspace = new NameCensusTestWorkspace();
            var legacy = workspace.AddMediaSource();
            var other = workspace.AddMediaSource();

            workspace.AddAssetWithFile(legacy, Hash(1), "IMG-20260724-WA0000.jpg");
            workspace.AddAssetWithFile(legacy, Hash(2), "IMG-20260725-WA0000.jpg");
            workspace.AddAssetWithFile(other, Hash(3), "IMG-20260726-WA0001.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (legacy, 1), (other, 2));

            var pooled = census.TokenReuse.Single(
                reuse => reuse.Scope == ScopeLevel.Date && reuse.PartitionID is null);

            Assert.Equal(1, pooled.TokensOnOneDateOnly);
            Assert.Equal(1, pooled.TokensOnSeveralDates);
            Assert.Equal(2, pooled.DistinctDatesPerToken.Maximum);

            var perSource = census.TokenReuse.Single(
                reuse => reuse.Scope == ScopeLevel.SourceDate && reuse.PartitionID == other);

            Assert.Equal(1, perSource.TokensOnOneDateOnly);
            Assert.Equal(0, perSource.TokensOnSeveralDates);
        }

        // ---------------------------------------------------------------------------------------
        // Reconciliation and reporting.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ThePopulationFiguresReconcileWithThemselves()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "IMG-20260724-WA0004-2.jpg");
            workspace.AddAssetWithFile(sourceID, HashC, "holiday photo.png");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));
            var reconciliation = census.Reconciliation;

            Assert.Equal(3, reconciliation.MediaFileCount);
            Assert.Equal(2, reconciliation.DatedFileCount);
            Assert.Equal(1, reconciliation.UndatedFileCount);
            Assert.Equal(1, reconciliation.SupportedFileCount);
            Assert.Equal(1, reconciliation.UnsupportedDatedFileCount);
            Assert.Equal(3, reconciliation.MediaAssetCount);
            Assert.Equal(1, reconciliation.AssetsWithSupportedEvidence);
            Assert.Equal(2, reconciliation.AssetsWithoutSupportedEvidence);
        }

        [Fact]
        public void EverySourceIsReportedInIdentifierOrderWithItsTypeAndGroup()
        {
            using var workspace = new NameCensusTestWorkspace();
            var legacy = workspace.AddMediaSource();
            var other = workspace.AddMediaSource();
            workspace.AddAssetWithFile(legacy, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(other, HashB, "IMG-20260724-WA0005.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (legacy, 7), (other, 3));

            Assert.Equal(
                [legacy, other], census.MediaSources.Select(source => source.MediaSourceID));
            Assert.Equal([7, 3], census.MediaSources.Select(source => source.DeviceGroupID));
            Assert.All(census.MediaSources, source => Assert.True(source.IsWhatsAppMediaDirectory));
            Assert.All(census.MediaSources, source => Assert.Equal(1, source.SupportedFileCount));
        }

        /// <remarks>
        /// A source of another type yields no date at all, so its files are undated for a reason that has
        /// nothing to do with how they are named. The census reports the type rather than letting the
        /// reader infer it, and does not refuse the workspace.
        /// </remarks>
        [Fact]
        public void ASourceOfAnotherType_ContributesNoDatesAndIsReportedAsSuch()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource(sourceType: "UnknownLayout");
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.False(Assert.Single(census.MediaSources).IsWhatsAppMediaDirectory);
            Assert.Equal(1, census.Reconciliation.UndatedFileCount);
            Assert.Equal(0, census.Reconciliation.SupportedFileCount);
        }

        [Fact]
        public void AWorkspaceWithNoMedia_ProducesAnEmptyButWellFormedCensus()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(0, census.Reconciliation.MediaFileCount);
            Assert.Empty(census.TokenCurve.Rows);
            Assert.Null(census.TokenCurve.MinimumToken);
            Assert.True(census.TokenCurve.ObservedSetIsContiguous);
            Assert.Equal(4, census.KeyUniqueness.Count);
            Assert.Equal(0, census.MaximumTokenGroupDistinctTokenCount);
            Assert.Equal(0, census.ZeroByte.ZeroByteAssetCount);
        }

        /// <remarks>
        /// The highest observed token is reconciled against the population that produced it. No date and
        /// no surrogate identifier is emitted, which is why this is a reconciliation rather than an
        /// exemplar.
        /// </remarks>
        [Fact]
        public void TheGroupHoldingTheHighestToken_IsReconciledWithoutBeingIdentified()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithFile(sourceID, Hash(1), "IMG-20260724-WA0000.jpg");
            workspace.AddAssetWithFile(sourceID, Hash(2), "IMG-20260724-WA0001.jpg");
            workspace.AddAssetWithFile(sourceID, Hash(3), "IMG-20260725-WA0158.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(1, census.MaximumTokenGroupDistinctTokenCount);
            Assert.Equal(1, census.MaximumTokenGroupInclusiveWidth);
            Assert.Equal(1, census.MaximumTokenGroupDistinctAssetCount);
            Assert.Equal(1, census.MaximumTokenGroupPhysicalFileCount);
        }

        [Fact]
        public void TheSameLogicalDataInsertedInADifferentOrder_ProducesIdenticalOutput() =>
            Assert.Equal(DescribeCensus(ascending: true), DescribeCensus(ascending: false));

        // ---------------------------------------------------------------------------------------
        // Fixtures and helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Two payloads sharing one name, one date and one token, held by two sources in two device
        /// groups.
        /// </summary>
        private static WaSequenceScopeCensus TwoAssetsInTwoDeviceGroups(
            NameCensusTestWorkspace workspace)
        {
            var first = workspace.AddMediaSource();
            var second = workspace.AddMediaSource();

            var left = workspace.AddMediaAsset(HashA);
            var right = workspace.AddMediaAsset(HashB);

            foreach (var sourceID in new[] { first, second })
            {
                workspace.AddMediaFile(sourceID, left, HashA, "IMG-20260724-WA0004.jpg");
                workspace.AddMediaFile(sourceID, right, HashB, "IMG-20260724-WA0004.jpg");
            }

            return WaSequenceScopeTestRunner.Analyse(workspace, (first, 1), (second, 2));
        }

        private static void AddCopies(
            NameCensusTestWorkspace workspace, long sourceID, string date, int copies)
        {
            var hash = Hash(int.Parse(date, CultureInfo.InvariantCulture) % 1000);
            var asset = workspace.AddMediaAsset(hash);

            for (var copy = 0; copy < copies; copy++)
            {
                workspace.AddMediaFile(sourceID, asset, hash, $"IMG-{date}-WA0004.jpg");
            }
        }

        private static ScopeKeyUniqueness Level(WaSequenceScopeCensus census, ScopeLevel level) =>
            census.KeyUniqueness.Single(uniqueness => uniqueness.Level == level);

        private static CollisionCharacterisation Collision(
            WaSequenceScopeCensus census, ScopeLevel level) =>
            census.Collisions.Single(collision => collision.Level == level);

        private static ContinuityByGroupSize Continuity(
            WaSequenceScopeCensus census, ScopeLevel scope, GroupSizeBand band) =>
            census.Continuity.Single(
                continuity => continuity.Scope == scope && continuity.Band == band);

        /// <remarks>
        /// Synthetic and distinct per number: the first four characters carry it, the rest pad to the
        /// sixty-four hexadecimal characters the schema requires.
        /// </remarks>
        private static string Hash(int number) =>
            number.ToString("D4", CultureInfo.InvariantCulture).PadRight(64, '0');

        /// <remarks>
        /// The same logical archive built in two insertion orders. Row identifiers, and therefore every
        /// internal index, differ between them; every reported figure must not.
        /// </remarks>
        private static string DescribeCensus(bool ascending)
        {
            using var workspace = new NameCensusTestWorkspace();
            var first = workspace.AddMediaSource();
            var second = workspace.AddMediaSource();

            var names = new[]
            {
                (Source: first, Hash: Hash(1), Name: "IMG-20260724-WA0000.jpg"),
                (Source: first, Hash: Hash(2), Name: "IMG-20260724-WA0001.jpg"),
                (Source: second, Hash: Hash(2), Name: "IMG-20260724-WA0002.jpg"),
                (Source: second, Hash: Hash(3), Name: "VID-20260725-WA0000.mp4"),
                (Source: first, Hash: Hash(3), Name: "VID-20260725-WA0000.mp4"),
            };

            var assets = new Dictionary<string, long>(StringComparer.Ordinal);
            var ordered = names.ToList();

            if (!ascending)
            {
                ordered.Reverse();
            }

            foreach (var entry in ordered)
            {
                if (!assets.TryGetValue(entry.Hash, out var asset))
                {
                    asset = workspace.AddMediaAsset(entry.Hash);
                    assets[entry.Hash] = asset;
                }

                workspace.AddMediaFile(entry.Source, asset, entry.Hash, entry.Name);
            }

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (first, 1), (second, 1));
            var description = new StringBuilder();

            description.AppendLine(CultureInfo.InvariantCulture,
                $"files {census.Reconciliation.SupportedFileCount}");
            description.AppendLine(CultureInfo.InvariantCulture,
                $"assets {census.Reconciliation.AssetsWithSupportedEvidence}");
            description.AppendLine(CultureInfo.InvariantCulture,
                $"dates {census.DatePopulation.SupportedDateCount}");

            foreach (var level in census.KeyUniqueness)
            {
                description.AppendLine(CultureInfo.InvariantCulture,
                    $"{level.Level} {level.KeyCount} {level.KeysWithSeveralMediaAssets} " +
                    $"{level.Ambiguity.FilesInMultiAssetKeys} " +
                    $"{level.Ambiguity.AssetPairMagnitude} " +
                    $"{level.AmbiguityExcludingZeroByte.MultiAssetKeyCount}");
            }

            foreach (var row in census.TokenCurve.Rows)
            {
                description.AppendLine(CultureInfo.InvariantCulture,
                    $"{row.Token} {row.PhysicalFileCount} {row.DistinctMediaAssetCount} " +
                    $"{row.DistinctFileDateCount} {row.DistinctDeviceGroupDateGroupCount}");
            }

            foreach (var group in census.GroupPopulations)
            {
                description.AppendLine(CultureInfo.InvariantCulture,
                    $"{group.Scope} {group.PartitionID} {group.GroupCount} " +
                    $"{group.FilesPerGroup.Median} {group.DistinctTokensPerGroup.Maximum}");
            }

            foreach (var joint in census.RangeAndPopulationJoints)
            {
                foreach (var cell in joint.Cells)
                {
                    description.AppendLine(CultureInfo.InvariantCulture,
                        $"{joint.Name} {joint.Scope} {cell.Row} {cell.Column} {cell.Count}");
                }
            }

            description.AppendLine(CultureInfo.InvariantCulture,
                $"locality {census.NumericValueLocality.ClassTotal} " +
                $"{census.NumericValueLocality.WithinOneSourceAndDate} " +
                $"{census.NumericValueLocality.AcrossDatesOnly}");
            description.AppendLine(CultureInfo.InvariantCulture,
                $"pairs {census.SameAssetAgreement.SourcePairCount} " +
                $"{census.SameAssetAgreement.SameDeviceGroupPairsAllTokensEqual}");

            return description.ToString();
        }
    }
}
