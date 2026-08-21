using System.Globalization;
using System.Security.Cryptography;
using Chatcheology.Data.Media;
using Chatcheology.Data.Sequence;
using Chatcheology.Data.Tests.Matching;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests what the Stage B2C-0 gate refuses, and the guarantees that matter more than any figure it
    /// produces: it changes nothing, decides nothing, computes no alignment outcome, and repeats itself
    /// exactly.
    /// </summary>
    public class DirectionSequenceGateSafetyTests
    {
        // ---------------------------------------------------------------------------------------
        // Nothing is written, decided or persisted.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheGateResolvesNothingAndLeavesTheWorkspaceUnchanged()
        {
            using var fixture = Populated();

            var before = DescribeWorkspaceState(fixture);

            fixture.Analyse();

            Assert.Equal(before, DescribeWorkspaceState(fixture));
            Assert.Equal(2, fixture.Workspace.ScalarLongReadOnly("PRAGMA user_version;"));

            Assert.Equal(
                3,
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
        public void TheGateLeavesTheDatabaseFileByteIdentical()
        {
            using var fixture = Populated();

            fixture.Workspace.CloseBuildingConnection();

            var before = HashFile(fixture.Workspace.DatabasePath);

            fixture.Analyse();

            Assert.Equal(before, HashFile(fixture.Workspace.DatabasePath));
        }

        [Fact]
        public void TheGateLeavesNoSqliteCompanionFileBehind()
        {
            using var fixture = Populated();

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
        public void TwoRunsOverOneUnchangedWorkspaceProduceIdenticalFigures()
        {
            using var fixture = Populated();

            Assert.Equal(Describe(fixture.Analyse()), Describe(fixture.Analyse()));
        }

        /// <remarks>
        /// The gate reads a name for one purpose. Changing every other part of the name, the path and
        /// the source root, while leaving the marker and the four digits intact, must leave every figure
        /// where it was.
        /// </remarks>
        [Fact]
        public void PrefixesPathsAndRootsTakeNoPartBeyondTheToken()
        {
            using var fixture = Populated();

            var before = Describe(fixture.Analyse());

            fixture.Workspace.Execute(
                """
                UPDATE MediaFile
                SET FileName = 'VID' || substr(FileName, 4),
                    RelativePath = 'elsewhere/' || MediaFileID || '.jpg';

                UPDATE MediaSource SET RootPath = 'SomewhereElse', DisplayName = 'Renamed';
                """);

            Assert.Equal(before, Describe(fixture.Analyse()));
        }

        // ---------------------------------------------------------------------------------------
        // The B2C-1 boundary.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The gate must be blind to its own outcome. Its reference quantities depend on the message
        /// pattern and on the token side's composition and run count, and on nothing else — so
        /// reversing the emitted token sequence, which changes the observed alignment completely while
        /// leaving that class untouched, must move no figure the gate reports.
        /// <para>
        /// This is the strongest available check that no observed-order quantity has crept in: a census
        /// that had computed one could not survive it.
        /// </para>
        /// </remarks>
        [Fact]
        public void ReversingTheEmittedTokenOrderMovesNoFigureTheGateReports()
        {
            using var forward = new DirectionSequenceGateFixture();
            using var reversed = new DirectionSequenceGateFixture();

            foreach (var (fixture, sent) in
                     new[] { (forward, true), (reversed, false) })
            {
                var date = DirectionSequenceGateFixture.FirstDate;

                fixture.AddOutgoing(date, "09:00:00");
                fixture.AddIncoming(date, "09:01:00");

                // O O I I one way round and I I O O the other: the same composition, the same run
                // count, and opposite observed orders.
                fixture.AddToken(date, 1, isSent: sent);
                fixture.AddToken(date, 2, isSent: sent);
                fixture.AddToken(date, 3, isSent: !sent);
                fixture.AddToken(date, 4, isSent: !sent);
            }

            Assert.Equal(Describe(forward.Analyse()), Describe(reversed.Analyse()));
        }

        /// <remarks>
        /// The stronger form of the same property, and the one that matters more. Reversal is a
        /// symmetry, so a census invariant under it might still be invariant for the wrong reason —
        /// and so is the direction swap. These two orders are related by neither:
        /// <code>
        /// O O I O I I     o = 3, i = 3, r = 4
        /// O I I O O I     o = 3, i = 3, r = 4
        ///
        /// reverse(OOIOII) = IIOIOO      swap(OOIOII) = IIOIOO
        /// reverse(OIIOOI) = IOOIIO      swap(OIIOOI) = IOOIIO
        /// </code>
        /// so neither sequence is the other's reversal or swap. They share one reference class and
        /// nothing else.
        /// <para>
        /// The test has teeth because the observed alignment genuinely differs between them. Against
        /// the message pattern <c>OIO</c> used here, <c>OOIOII</c> holds two monotone embeddings and
        /// <c>OIIOOI</c> holds four — so the graded co-primary statistic would separate them at B2C-1.
        /// Every figure B2C-0 reports must nonetheless be identical, because the gate conditions on
        /// the class and never on the order within it.
        /// </para>
        /// </remarks>
        [Fact]
        public void TwoUnrelatedTokenOrdersOfOneReferenceClassMoveNoFigureTheGateReports()
        {
            // true is outgoing, and the fixture lays these out in ascending token order.
            bool[] first = [true, true, false, true, false, false];
            bool[] second = [true, false, false, true, true, false];

            // The premise of the test, asserted rather than assumed: one class, two orders, and
            // neither symmetry relating them.
            Assert.Equal(
                DirectionSequenceReference.CountOutgoing(first),
                DirectionSequenceReference.CountOutgoing(second));

            Assert.Equal(
                DirectionSequenceReference.RunCount(first),
                DirectionSequenceReference.RunCount(second));

            Assert.NotEqual(first, second);
            Assert.NotEqual(first.Reverse().ToArray(), second);
            Assert.NotEqual(first.Select(symbol => !symbol).ToArray(), second);

            using var one = new DirectionSequenceGateFixture();
            using var other = new DirectionSequenceGateFixture();

            foreach (var (fixture, layout) in new[] { (one, first), (other, second) })
            {
                var date = DirectionSequenceGateFixture.FirstDate;

                fixture.AddOutgoing(date, "09:00:00");
                fixture.AddIncoming(date, "09:01:00");
                fixture.AddOutgoing(date, "09:02:00");

                for (var position = 0; position < layout.Length; position++)
                {
                    fixture.AddToken(date, position + 1, isSent: layout[position]);
                }
            }

            var census = one.Analyse();

            // The pair really is classified, so the comparison is over a populated census rather than
            // two censuses that agree by both being empty.
            var scope = DirectionSequenceGateFixture.ScopeOf(census, ScopeLevel.SourceDate);

            Assert.Equal(1, scope.PairPopulation.ClassifiedPairCount);
            Assert.Equal(3, scope.PairPopulation.MessageObservationsClassified);
            Assert.Equal(1, scope.Determinacy.CrossTabulationTotal);

            Assert.Equal(Describe(census), Describe(other.Analyse()));
        }

        // ---------------------------------------------------------------------------------------
        // Device-group validation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void NoDeviceGroupAssignmentAtAllIsRefused()
        {
            using var fixture = Populated();

            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Analyse([]));

            Assert.Contains("No device-group assignment", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ASourceAssignedTwiceIsRefused()
        {
            using var fixture = Populated();

            var duplicated = fixture.OneGroupForEverySource();
            duplicated.Add(
                new DeviceGroupAssignment { MediaSourceID = fixture.SourceID, DeviceGroupID = 999 });

            var failure = Assert.Throws<InvalidOperationException>(
                () => fixture.Analyse(duplicated));

            Assert.Contains("more than once", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ASourceLeftUnassignedIsRefused()
        {
            using var fixture = Populated();

            var partial = fixture.OneGroupForEverySource()
                .Where(assignment => assignment.MediaSourceID != fixture.SourceID)
                .ToList();

            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Analyse(partial));

            Assert.Contains("assigned to no", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AMappingNamingASourceTheWorkspaceDoesNotHoldIsRefused()
        {
            using var fixture = Populated();

            var foreign = fixture.OneGroupForEverySource();
            foreign.Add(new DeviceGroupAssignment { MediaSourceID = 4242, DeviceGroupID = 1 });

            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Analyse(foreign));

            Assert.Contains("does not contain", failure.Message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------
        // Request validation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AConversationTheWorkspaceDoesNotHoldIsRefused()
        {
            using var fixture = Populated();

            var failure = Assert.Throws<InvalidOperationException>(
                () => new DirectionSequenceGateService().Analyse(
                    new DirectionSequenceGateRequest
                    {
                        DatabasePath = fixture.Workspace.DatabasePath,
                        ConversationID = 4242,
                        LocalParticipantID = MatchingTestWorkspace.LocalParticipantID,
                        DeviceGroups = fixture.OneGroupForEverySource(),
                    }));

            Assert.Contains("no conversation 4242", failure.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// A participant of another conversation would make every message read as incoming, which looks
        /// exactly like a correct answer. Refused rather than measured.
        /// </remarks>
        [Fact]
        public void AParticipantOfAnotherConversationIsRefused()
        {
            using var fixture = Populated();

            var failure = Assert.Throws<InvalidOperationException>(
                () => new DirectionSequenceGateService().Analyse(
                    new DirectionSequenceGateRequest
                    {
                        DatabasePath = fixture.Workspace.DatabasePath,
                        ConversationID = MatchingTestWorkspace.ConversationID,
                        LocalParticipantID = MatchingTestWorkspace.OutsiderParticipantID,
                        DeviceGroups = fixture.OneGroupForEverySource(),
                    }));

            Assert.Contains("does not belong to conversation", failure.Message,
                StringComparison.Ordinal);
        }

        /// <remarks>
        /// An unresolved attachment on a message with no sender leaves its direction unknown. That is a
        /// stop condition, not a figure to carry forward, so no census is returned at all.
        /// </remarks>
        [Fact]
        public void AnAttachmentWithNoMessageDirectionStopsTheCensus()
        {
            using var fixture = Populated();

            fixture.AddSenderlessAttachment(DirectionSequenceGateFixture.FirstDate);

            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Analyse());

            Assert.Contains("names no sender", failure.Message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------
        // Stage A provenance.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Declared preserved coverage that agrees with the recount turns the recount into a
        /// reconciliation, and the census says so.
        /// </remarks>
        [Fact]
        public void DeclaredStageACoverageThatAgreesIsReconciledAndCarriedForward()
        {
            using var fixture = Populated();

            var recount = fixture.Analyse();

            Assert.False(recount.StageATokenCoverageReconciled);
            Assert.Null(recount.Sources[0].DeclaredStageASupportedTokenObservationCount);

            var declared = recount.Sources
                .Select(
                    source => new StageATokenCoverageDeclaration
                    {
                        MediaSourceID = source.MediaSourceID,
                        PhysicalObservationCount = source.PhysicalObservationCount,
                        SupportedTokenObservationCount = source.SupportedTokenObservationCount,
                    })
                .ToList();

            var reconciled = fixture.Analyse(stageACoverage: declared);

            Assert.True(reconciled.StageATokenCoverageReconciled);

            Assert.Equal(
                recount.Sources[0].SupportedTokenObservationCount,
                reconciled.Sources[0].DeclaredStageASupportedTokenObservationCount);
        }

        [Fact]
        public void DeclaredStageACoverageThatDisagreesWithTheRecountIsRefused()
        {
            using var fixture = Populated();

            var failure = Assert.Throws<InvalidOperationException>(
                () => fixture.Analyse(
                    stageACoverage:
                    [
                        new StageATokenCoverageDeclaration
                        {
                            MediaSourceID = fixture.SourceID,
                            PhysicalObservationCount = 999,
                            SupportedTokenObservationCount = 999,
                        },
                    ]));

            Assert.Contains("was declared to hold", failure.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// A declaration covering only some sources would reconcile part of the token population and
        /// silently accept the rest, so it is refused even where every figure it does state is right.
        /// </remarks>
        [Fact]
        public void PartiallyDeclaredStageACoverageIsRefused()
        {
            using var fixture = Populated();

            var first = fixture.Analyse().Sources[0];

            var failure = Assert.Throws<InvalidOperationException>(
                () => fixture.Analyse(
                    stageACoverage:
                    [
                        new StageATokenCoverageDeclaration
                        {
                            MediaSourceID = first.MediaSourceID,
                            PhysicalObservationCount = first.PhysicalObservationCount,
                            SupportedTokenObservationCount =
                                first.SupportedTokenObservationCount,
                        },
                    ]));

            Assert.Contains("does not name MediaSource", failure.Message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------
        // Workspace integrity.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ADatedCopyWithNoLocatableMarkerIsRefused()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date);

            fixture.Workspace.AddAssetWithCopy(
                fixture.SourceID, MatchingTestData.Hash(90), date, isSent: true);

            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Analyse());

            Assert.Contains("no locatable", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnUnhashedMediaFileIsRefused()
        {
            using var fixture = Populated();

            fixture.Workspace.Execute("UPDATE MediaFile SET SHA256 = NULL WHERE MediaFileID = 1;");

            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Analyse());

            Assert.Contains("no SHA-256", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AWorkspaceAtTheWrongSchemaVersionIsRefused()
        {
            using var fixture = Populated();

            fixture.Workspace.Execute("PRAGMA user_version = 1;");

            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Analyse());

            Assert.Contains(
                "a direction-sequence gate census", failure.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// An undated row carries no date to key a logical position on, so it is counted as a physical
        /// observation and nothing more. It is not an error and it is not a position.
        /// </remarks>
        [Fact]
        public void AnUndatedCopyIsCountedWithoutBecomingAPosition()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddToken(date, 7, isSent: true);
            fixture.AddUndated(isSent: true);

            var source = fixture.Analyse().Sources.Single();

            Assert.Equal(2, source.PhysicalObservationCount);
            Assert.Equal(1, source.DatedObservationCount);
            Assert.Equal(1, source.SupportedTokenObservationCount);
            Assert.Equal(1, source.LogicalPositionsAfterCollapse);
        }

        // ---------------------------------------------------------------------------------------
        // Cancellation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ACancelledTokenThrowsRatherThanReturningAPartialCensus()
        {
            using var fixture = Populated();
            using var cancellation = new CancellationTokenSource();

            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => fixture.Analyse(cancellationToken: cancellation.Token));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>A workspace holding both directions, two sources and a mix of token shapes.</summary>
        private static DirectionSequenceGateFixture Populated()
        {
            var fixture = new DirectionSequenceGateFixture();
            var second = fixture.AddSource();

            var first = DirectionSequenceGateFixture.FirstDate;
            fixture.AddOutgoing(first, "09:00:00");
            fixture.AddIncoming(first, "09:01:00");

            fixture.AddToken(first, 1, isSent: true);
            fixture.AddToken(first, 2, isSent: true);
            fixture.AddToken(first, 3, isSent: false);
            fixture.AddToken(first, 4, isSent: false);
            fixture.AddToken(first, 2, isSent: true, mediaSourceID: second);
            fixture.AddToken(first, 9, isSent: null, mediaSourceID: second);
            fixture.AddNamed(first, "0010-1", isSent: true, mediaSourceID: second);

            var later = DirectionSequenceGateFixture.Day(4);
            fixture.AddOutgoing(later, "10:00:00");
            fixture.AddToken(later, 5, isSent: true);

            return fixture;
        }

        /// <summary>
        /// Every reported figure, flattened, so two runs can be compared as one value.
        /// </summary>
        private static string Describe(DirectionSequenceGateCensus census) =>
            string.Join(
                "|",
                [
                    $"{census.ConversationID}:{census.LocalParticipantID}:" +
                    $"{census.StageATokenCoverageReconciled}",
                    Describe(census.MessagePopulation),
                    string.Join(";", census.Sources.Select(Describe)),
                    string.Join(";", census.DeviceGroups.Select(Describe)),
                    Describe(census.CrossSourceOverlap),
                    string.Join(";", census.Scopes.Select(Describe)),
                ]);

        private static string Describe(DirectionSequenceMessagePopulation population) =>
            string.Join(
                ",",
                [
                    population.WorkspaceUnresolvedAttachmentCount,
                    population.ConversationUnresolvedAttachmentCount,
                    population.ConsideredAttachmentCount,
                    population.UnknownDirectionAttachmentCount,
                    population.DistinctAttachmentDateCount,
                    population.OutgoingAttachmentCount,
                    population.IncomingAttachmentCount,
                    population.MultiAttachmentMessageCount,
                    population.MessageWithAttachmentCount,
                    population.MaximumAttachmentsOnOneMessage,
                    population.OutgoingOnlyDateCount,
                    population.IncomingOnlyDateCount,
                    population.BothDirectionDateCount,
                ]) +
            Describe(population.OrdinalDistribution) +
            Describe(population.SequenceLengthDistribution) +
            Describe(population.TransitionCountDistribution) +
            Describe(population.RunCountDistribution) +
            Describe(population.SequenceLength);

        private static string Describe(DirectionSequenceSourcePopulation source) =>
            string.Join(
                ",",
                [
                    source.MediaSourceID,
                    source.DeviceGroupID,
                    source.PhysicalObservationCount,
                    source.DatedObservationCount,
                    source.SupportedTokenObservationCount,
                    source.DirectionCapableObservationCount,
                    source.SupportedObservationsWithoutDirectionCount,
                    source.RecordsAnyDirection ? 1 : 0,
                    source.DistinctSupportedDateCount,
                    source.LogicalPositionsBeforeCollapse,
                    source.LogicalPositionsAfterCollapse,
                    source.DirectionLabelledLogicalPositionCount,
                    source.LogicalPositionsWithoutDirectionCount,
                    source.ConflictingLogicalPositionCount,
                    source.SharedLogicalPositionCount,
                    source.SourceOnlyLogicalPositionCount,
                ]) +
            $"[{source.EarliestSupportedDate}-{source.LatestSupportedDate}]";

        private static string Describe(DirectionSequenceDeviceGroupPopulation group) =>
            string.Join(
                ",",
                [
                    group.DeviceGroupID,
                    group.SourceCount,
                    group.DirectionCapableSourceCount,
                    group.DirectionBlindSourceCount,
                    group.PhysicalObservationCount,
                    group.SupportedTokenObservationCount,
                    group.DirectionCapableObservationCount,
                    group.DirectionBlindSourceObservationCount,
                    group.DistinctSupportedDateCount,
                    group.LogicalPositionsBeforeCollapse,
                    group.LogicalPositionsAfterCollapse,
                    group.DirectionLabelledLogicalPositionCount,
                    group.LogicalPositionsWithoutDirectionCount,
                    group.ConflictingLogicalPositionCount,
                    group.PositionsKnownOnlyFromDirectionBlindSources,
                    group.ReducesToOneDirectionCapableSource ? 1 : 0,
                ]);

        private static string Describe(DirectionSequenceCrossSourceOverlap overlap) =>
            string.Join(
                ",",
                [
                    overlap.DistinctLogicalPositionCount,
                    overlap.SharedLogicalPositionCount,
                    overlap.SingleSourceLogicalPositionCount,
                    overlap.AgreeingPositionCount,
                    overlap.ConflictingPositionCount,
                    overlap.OneSideDirectionKnownPositionCount,
                    overlap.NoDirectionKnownPositionCount,
                    overlap.ConflictingPositionsWithinOneDeviceGroup,
                    overlap.ConflictingPositionsSpanningDeviceGroups,
                ]) +
            overlap.ConflictingShareOfSharedPositions.ToString(
                "R", CultureInfo.InvariantCulture);

        private static string Describe(DirectionSequenceScopeCensus scope) =>
            string.Join(
                ",",
                [
                    (int)scope.Scope,
                    scope.PairPopulation.ScopeKeyCount,
                    scope.PairPopulation.PairCount,
                    scope.PairPopulation.PairsWithMessageSymbols,
                    scope.PairPopulation.PairsWithTokenPositions,
                    scope.PairPopulation.ConflictingLogicalPositionCount,
                    scope.PairPopulation.ExcludedByDirectionConflictPairCount,
                    scope.PairPopulation.MessageObservationsLostToDirectionConflict,
                    scope.PairPopulation.SupplyInsufficientPairCount,
                    scope.PairPopulation.ClassifiedPairCount,
                    scope.PairPopulation.MessageObservationsClassified,
                    scope.PairPopulation.Degenerate.NoTokenPositionPairCount,
                    scope.PairPopulation.Degenerate.NoMessageSymbolPairCount,
                    scope.PairPopulation.Degenerate.SingleMessageSymbolPairCount,
                    scope.PairPopulation.Degenerate.NoOutgoingTokenPositionPairCount,
                    scope.PairPopulation.Degenerate.NoIncomingTokenPositionPairCount,
                    scope.PairPopulation.Degenerate.SingleArrangementPairCount,
                    scope.PairPopulation.Degenerate.DegeneratePairCount,
                    scope.PairPopulation.Degenerate.MessageObservationsInDegeneratePairs,
                    scope.Supply.PairsBecomingInsufficientAfterCollapse,
                    scope.Supply.PairsBecomingSufficientAfterCollapse,
                    scope.Supply.MessageObservationsLostToCollapse,
                    scope.Burstiness.NotOrderInformativePairCount,
                    scope.Burstiness.WeaklyOrderInformativePairCount,
                    scope.Burstiness.StrictlyOrderInformativePairCount,
                    scope.Reference.InformativeUnderExchangeableReferenceCount,
                    scope.Reference.DeterminateUnderExchangeableReferenceCount,
                    scope.Reference.InformativeLostToRunConditioningCount,
                    scope.Determinacy.BinaryDeterminatePairCount,
                    scope.Determinacy.BinaryInformativePairCount,
                    scope.Determinacy.GradedDeterminatePairCount,
                    scope.Determinacy.GradedInformativePairCount,
                    scope.Determinacy.BinaryInformativeAndGradedInformative,
                    scope.Determinacy.BinaryDeterminateAndGradedInformative,
                    scope.Determinacy.BinaryInformativeAndGradedDeterminate,
                    scope.Determinacy.BinaryDeterminateAndGradedDeterminate,
                ]) +
            Describe(scope.Dilution.MessageSymbolDistribution) +
            Describe(scope.Dilution.TokenPositions) +
            Describe(scope.Dilution.ConversationShare) +
            Describe(scope.Supply.BeforeCollapse) +
            Describe(scope.Supply.AfterCollapse) +
            Describe(scope.Burstiness.TokenRunCounts) +
            Describe(scope.Burstiness.ObservedLessExpectedTokenRunCount) +
            Describe(scope.Burstiness.MessageRunCountDistribution) +
            Describe(scope.Burstiness.MessageTransitionCountDistribution) +
            Describe(scope.Reference);

        private static string Describe(DirectionSequenceSupplyCounts supply) =>
            $"[{supply.Population},{supply.SupplySufficientPairCount}," +
            $"{supply.SupplyInsufficientPairCount}," +
            $"{supply.MessageObservationsInSupplyInsufficientPairs}]" +
            Describe(supply.OutgoingShortfallDistribution) +
            Describe(supply.IncomingShortfallDistribution);

        private static string Describe(DirectionSequenceReferenceCensus reference) =>
            $"[{reference.Population}," +
            $"{reference.SumOfConditionalAdmissionProbability.ToString("R", CultureInfo.InvariantCulture)}," +
            $"{reference.SumOfConditionalAdmissionProbabilityOverInformative.ToString("R", CultureInfo.InvariantCulture)}," +
            $"{reference.SumOfExpectedEmbeddingShare.ToString("R", CultureInfo.InvariantCulture)}," +
            $"{reference.SumOfExpectedEmbeddingShareOverInformative.ToString("R", CultureInfo.InvariantCulture)}]" +
            $"[{reference.Bands.BandTotal},{reference.Bands.ExactlyZero}," +
            $"{reference.Bands.AboveZeroToFiveHundredths}," +
            $"{reference.Bands.AboveFiveHundredthsToOneQuarter}," +
            $"{reference.Bands.AboveOneQuarterToOneHalf}," +
            $"{reference.Bands.AboveOneHalfToThreeQuarters}," +
            $"{reference.Bands.AboveThreeQuartersToNinetyFiveHundredths}," +
            $"{reference.Bands.AboveNinetyFiveHundredthsBelowOne},{reference.Bands.ExactlyOne}]" +
            string.Join(
                ";",
                reference.BandRows.Select(
                    row =>
                        $"{(int)row.Band}:{row.PairCount}:{row.MessageObservationCount}:" +
                        $"{row.TokenPositionCount}:{row.DistinctScopeKeyCount}:" +
                        $"{row.DistinctDateCount}" +
                        Describe(row.MessageSequenceLength) +
                        Describe(row.TransitionCountDistribution))) +
            Describe(reference.ExchangeableLessConditionalAdmission);

        private static string Describe(DirectionSequenceRatioSummary summary) =>
            $"<{summary.Population},{summary.Minimum.ToString("R", CultureInfo.InvariantCulture)}," +
            $"{summary.Median.ToString("R", CultureInfo.InvariantCulture)}," +
            $"{summary.Maximum.ToString("R", CultureInfo.InvariantCulture)}," +
            $"{summary.Negative},{summary.Zero},{summary.Positive},{summary.BandTotal}," +
            $"{summary.MagnitudeAtMostFiveHundredths},{summary.MagnitudeAtMostOneQuarter}," +
            $"{summary.MagnitudeAtMostOneHalf},{summary.MagnitudeAtMostNinetyFiveHundredths}," +
            $"{summary.MagnitudeBelowOne},{summary.MagnitudeExactlyOne}," +
            $"{summary.MagnitudeAboveOne}>";

        private static string Describe(DirectionSequenceDifferenceSummary summary) =>
            $"<{summary.Population},{summary.Minimum.ToString("R", CultureInfo.InvariantCulture)}," +
            $"{summary.Median.ToString("R", CultureInfo.InvariantCulture)}," +
            $"{summary.Maximum.ToString("R", CultureInfo.InvariantCulture)}," +
            $"{summary.Negative},{summary.Zero},{summary.Positive},{summary.BandTotal}," +
            $"{summary.MagnitudeAtMostOne},{summary.MagnitudeAtMostTwo}," +
            $"{summary.MagnitudeAtMostFive},{summary.MagnitudeAtMostTen}," +
            $"{summary.MagnitudeAtMostTwentyFive},{summary.MagnitudeAboveTwentyFive}>";

        private static string Describe(CountSummary summary) =>
            $"({summary.Population},{summary.Minimum},{summary.Median},{summary.Maximum}," +
            $"{summary.BandTotal})";

        private static string Describe(IReadOnlyList<ValueCount> distribution) =>
            "{" + string.Join(",", distribution.Select(row => $"{row.Value}={row.Count}")) + "}";

        private static string DescribeWorkspaceState(DirectionSequenceGateFixture fixture) =>
            string.Join(
                "|",
                [
                    Count(fixture, "Message"),
                    Count(fixture, "Attachment"),
                    Count(fixture, "MediaFile"),
                    Count(fixture, "MediaAsset"),
                    Count(fixture, "MediaAssetFile"),
                    Count(fixture, "MediaSource"),
                    fixture.Workspace.ScalarLongReadOnly("SELECT COUNT(*) FROM sqlite_master;")
                        .ToString(CultureInfo.InvariantCulture),
                ]);

        private static string Count(DirectionSequenceGateFixture fixture, string table) =>
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
