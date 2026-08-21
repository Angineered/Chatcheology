using Chatcheology.Data.Media;
using Chatcheology.Data.Sequence;
using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests what the Stage B2C-0 gate measures: the message population, the token population, scope
    /// and collapse, supply adequacy, burstiness and the two determinacy classifications.
    /// </summary>
    /// <remarks>
    /// Nothing here checks an alignment outcome, because the gate computes none. What is checked is that
    /// the population it reports is the population it should have read, and that the exact reference
    /// quantities land on the pairs they belong to.
    /// </remarks>
    public class DirectionSequenceGateCensusTests
    {
        // ---------------------------------------------------------------------------------------
        // The message side.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// One symbol per unresolved attachment, and every unresolved attachment of the conversation
        /// considered — which is what "candidate availability was not an inclusion rule" means. The
        /// date here holds no media at all, so a census that filtered on candidate evidence would
        /// report an empty message side.
        /// </remarks>
        [Fact]
        public void EveryUnresolvedAttachmentOfTheConversationIsConsidered()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");
            fixture.AddOutgoing(DirectionSequenceGateFixture.Day(3), "10:00:00");

            var population = fixture.Analyse().MessagePopulation;

            Assert.Equal(3, population.ConversationUnresolvedAttachmentCount);
            Assert.Equal(3, population.ConsideredAttachmentCount);
            Assert.True(population.EveryConversationUnresolvedAttachmentConsidered);
            Assert.Equal(0, population.UnknownDirectionAttachmentCount);
            Assert.Equal(2, population.OutgoingAttachmentCount);
            Assert.Equal(1, population.IncomingAttachmentCount);
            Assert.Equal(2, population.DistinctAttachmentDateCount);
            Assert.Equal(1, population.BothDirectionDateCount);
            Assert.Equal(1, population.OutgoingOnlyDateCount);
            Assert.Equal(0, population.IncomingOnlyDateCount);
        }

        /// <remarks>
        /// The expected workspace invariant: one attachment per imported placeholder, so ordinal one
        /// throughout. Censused rather than assumed.
        /// </remarks>
        [Fact]
        public void OneAttachmentPerMessageLeavesEveryOrdinalAtOne()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            var population = fixture.Analyse().MessagePopulation;

            Assert.Equal(
                [1], population.OrdinalDistribution.Select(row => row.Value).ToArray());

            Assert.Equal(2, population.OrdinalDistribution.Single().Count);
            Assert.Equal(0, population.MultiAttachmentMessageCount);
            Assert.Equal(2, population.MessageWithAttachmentCount);
            Assert.Equal(1, population.MaximumAttachmentsOnOneMessage);
        }

        /// <remarks>
        /// A message carrying three attachments keeps three symbols, in ordinal order, rather than
        /// being collapsed to one. The date's sequence length is therefore four, not two.
        /// </remarks>
        [Fact]
        public void AMultiAttachmentMessageKeepsOneSymbolPerAttachment()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddMessageWithAttachments(date, outgoing: true, attachmentCount: 3, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            var population = fixture.Analyse().MessagePopulation;

            Assert.Equal(4, population.ConsideredAttachmentCount);
            Assert.Equal(3, population.OutgoingAttachmentCount);
            Assert.Equal(1, population.IncomingAttachmentCount);
            Assert.Equal(1, population.MultiAttachmentMessageCount);
            Assert.Equal(3, population.MaximumAttachmentsOnOneMessage);

            Assert.Equal(
                [1, 2, 3], population.OrdinalDistribution.Select(row => row.Value).ToArray());

            // Four symbols on one date, and O O O I is two runs and one transition.
            Assert.Equal(4, population.SequenceLengthDistribution.Single().Value);
            Assert.Equal(2, population.RunCountDistribution.Single().Value);
            Assert.Equal(1, population.TransitionCountDistribution.Single().Value);
        }

        /// <remarks>
        /// Attachments are ordered by message sequence and then ordinal, not by wall-clock time or by
        /// identifier. Two messages inserted in one order and timed in the other must still produce the
        /// insertion-order pattern, which here is the one holding two runs rather than one.
        /// </remarks>
        [Fact]
        public void SymbolsFollowMessageSequenceRatherThanTimestamp()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "23:00:00");
            fixture.AddOutgoing(date, "08:00:00");
            fixture.AddIncoming(date, "09:00:00");

            var population = fixture.Analyse().MessagePopulation;

            Assert.Equal(3, population.SequenceLengthDistribution.Single().Value);
            Assert.Equal(2, population.RunCountDistribution.Single().Value);
        }

        // ---------------------------------------------------------------------------------------
        // Token grammar and ordering.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Exactly four ASCII digits after <c>-WA</c>, with the recorded extension removed only when
        /// the name really ends with it. Everything else is unsupported and emits no position.
        /// </remarks>
        [Theory]
        [InlineData("0007", true)]
        [InlineData("0000", true)]
        [InlineData("9999", true)]
        [InlineData("007", false)]
        [InlineData("00007", false)]
        [InlineData("0007a", false)]
        [InlineData("0007-1", false)]
        [InlineData("000A", false)]
        [InlineData("", false)]
        [InlineData(" 007", false)]
        public void OnlyTheApprovedFourDigitSuffixEmitsAPosition(string suffix, bool supported)
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddNamed(date, suffix, isSent: true);

            var source = fixture.Analyse().Sources.Single();

            Assert.Equal(1, source.DatedObservationCount);
            Assert.Equal(supported ? 1 : 0, source.SupportedTokenObservationCount);
            Assert.Equal(supported ? 1 : 0, source.LogicalPositionsAfterCollapse);
        }

        /// <remarks>
        /// The extension is removed only where the name genuinely ends with it. A name ending in
        /// <c>.jpg</c> whose recorded extension is <c>.png</c> keeps its whole remainder, which is then
        /// not four digits.
        /// </remarks>
        [Fact]
        public void AnExtensionThatIsNotTheNamesEndingIsNotRemoved()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = MatchingTestData.Hash(41);
            var mediaAssetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                mediaAssetID,
                sha,
                date,
                isSent: true,
                fileName: DirectionSequenceGateFixture.Name(date, "0007") + ".jpg",
                extension: ".png");

            Assert.Equal(0, fixture.Analyse().Sources.Single().SupportedTokenObservationCount);
        }

        /// <remarks>
        /// Positions are ordered by the token value, not by the order the rows were written. Inserted
        /// as ten, two, five, the emitted sequence is outgoing-incoming-outgoing and holds three runs;
        /// insertion order would hold two.
        /// </remarks>
        [Fact]
        public void PositionsAreOrderedByTokenValueRatherThanByInsertion()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            fixture.AddToken(date, 10, isSent: true);
            fixture.AddToken(date, 2, isSent: true);
            fixture.AddToken(date, 5, isSent: false);

            var rows = Capture(fixture);

            Assert.Equal(3, Row(rows, ScopeLevel.SourceDate).TokenRunCount);
        }

        // ---------------------------------------------------------------------------------------
        // Collapse, direction coverage and conflict.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The same logical position recovered from two sources with the same folder state collapses to
        /// one at device-group scope, and stays two physical observations. Source scope keeps them
        /// apart, because each source's own copy is a position of its own scope.
        /// </remarks>
        [Fact]
        public void EquivalentPositionsCollapseOnceWithinADeviceGroup()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;
            var second = fixture.AddSource();

            fixture.AddOutgoing(date);

            fixture.AddToken(date, 7, isSent: true);
            fixture.AddToken(date, 7, isSent: true, mediaSourceID: second);

            var census = fixture.Analyse();
            var group = census.DeviceGroups.Single();

            Assert.Equal(2, group.LogicalPositionsBeforeCollapse);
            Assert.Equal(1, group.LogicalPositionsAfterCollapse);
            Assert.Equal(1, group.DirectionLabelledLogicalPositionCount);

            Assert.Equal(1, census.CrossSourceOverlap.DistinctLogicalPositionCount);
            Assert.Equal(1, census.CrossSourceOverlap.SharedLogicalPositionCount);
            Assert.Equal(1, census.CrossSourceOverlap.AgreeingPositionCount);
            Assert.Equal(0, census.CrossSourceOverlap.ConflictingPositionCount);

            foreach (var source in census.Sources)
            {
                Assert.Equal(1, source.LogicalPositionsAfterCollapse);
                Assert.Equal(1, source.SharedLogicalPositionCount);
                Assert.Equal(0, source.SourceOnlyLogicalPositionCount);
            }
        }

        /// <remarks>
        /// One position, two sources, disagreeing folder state. The device-group pair is excluded
        /// whole — never repaired by preferring a source and never by dropping the position alone — and
        /// the message observations that go with it are reported as lost. At source scope neither
        /// source's own copies disagree, so neither pair is excluded, which is exactly the sensitivity
        /// the two scopes exist to show.
        /// </remarks>
        [Fact]
        public void AConflictingPositionExcludesItsWholeDeviceGroupPair()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;
            var second = fixture.AddSource();

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            fixture.AddToken(date, 7, isSent: true);
            fixture.AddToken(date, 7, isSent: false, mediaSourceID: second);
            fixture.AddToken(date, 8, isSent: false);

            var census = fixture.Analyse();

            Assert.Equal(1, census.CrossSourceOverlap.ConflictingPositionCount);
            Assert.Equal(1, census.CrossSourceOverlap.ConflictingPositionsWithinOneDeviceGroup);
            Assert.Equal(0, census.CrossSourceOverlap.ConflictingPositionsSpanningDeviceGroups);
            // Token eight is observed by one source only, so it is not a shared position at all:
            // the rate is over shared positions, of which there is one and it disagrees.
            Assert.Equal(1d, census.CrossSourceOverlap.ConflictingShareOfSharedPositions);

            var group = DirectionSequenceGateFixture
                .ScopeOf(census, ScopeLevel.DeviceGroupDate).PairPopulation;

            Assert.Equal(1, group.ConflictingLogicalPositionCount);
            Assert.Equal(1, group.ExcludedByDirectionConflictPairCount);
            Assert.Equal(2, group.MessageObservationsLostToDirectionConflict);
            Assert.Equal(0, group.ClassifiedPairCount);

            var perSource = DirectionSequenceGateFixture
                .ScopeOf(census, ScopeLevel.SourceDate).PairPopulation;

            Assert.Equal(0, perSource.ConflictingLogicalPositionCount);
            Assert.Equal(0, perSource.ExcludedByDirectionConflictPairCount);
            Assert.Equal(0, perSource.MessageObservationsLostToDirectionConflict);
        }

        /// <remarks>
        /// A disagreement whose two sources sit in different device groups excludes no device-group
        /// pair, because neither group's own copies disagree. It is reported separately so it is still
        /// visible at the freeze review rather than absorbed by the scope choice.
        /// </remarks>
        [Fact]
        public void ADisagreementSpanningDeviceGroupsIsReportedWithoutExcludingEitherGroup()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;
            var second = fixture.AddSource();

            fixture.AddOutgoing(date);

            fixture.AddToken(date, 7, isSent: true);
            fixture.AddToken(date, 7, isSent: false, mediaSourceID: second);

            var census = fixture.Analyse(fixture.OneGroupPerSource());

            Assert.Equal(1, census.CrossSourceOverlap.ConflictingPositionCount);
            Assert.Equal(0, census.CrossSourceOverlap.ConflictingPositionsWithinOneDeviceGroup);
            Assert.Equal(1, census.CrossSourceOverlap.ConflictingPositionsSpanningDeviceGroups);

            Assert.Equal(
                0,
                DirectionSequenceGateFixture
                    .ScopeOf(census, ScopeLevel.DeviceGroupDate)
                    .PairPopulation.ExcludedByDirectionConflictPairCount);
        }

        /// <remarks>
        /// A position no copy of which records direction emits nothing at all. It is counted as
        /// direction-coverage loss and is never read as incoming, which the token counts prove: only
        /// the labelled position supplies the outgoing message symbol.
        /// </remarks>
        [Fact]
        public void ADirectionlessPositionIsCountedRatherThanReadAsIncoming()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date);

            fixture.AddToken(date, 7, isSent: true);
            fixture.AddToken(date, 8, isSent: null);

            var source = fixture.Analyse().Sources.Single();

            Assert.Equal(2, source.SupportedTokenObservationCount);
            Assert.Equal(1, source.DirectionCapableObservationCount);
            Assert.Equal(1, source.SupportedObservationsWithoutDirectionCount);
            Assert.Equal(2, source.LogicalPositionsAfterCollapse);
            Assert.Equal(1, source.DirectionLabelledLogicalPositionCount);
            Assert.Equal(1, source.LogicalPositionsWithoutDirectionCount);

            var rows = Capture(fixture);
            var row = Row(rows, ScopeLevel.SourceDate);

            Assert.Equal(1, row.TokenPositionCount);
            Assert.Equal(1, row.TokenOutgoingCount);
            Assert.Equal(0, row.TokenIncomingCount);
        }

        /// <remarks>
        /// A source recording no folder direction anywhere cannot emit a symbol however many files it
        /// holds. Where it shares a device group with one that can, the group's emitted sequence is the
        /// capable source's own, and the census says so rather than leaving it to be inferred.
        /// </remarks>
        [Fact]
        public void ADirectionBlindSourceIsExcludedAndTheGroupSaysWhatItReducesTo()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;
            var blind = fixture.AddSource("Direction-blind synthetic source");

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            fixture.AddToken(date, 7, isSent: true);
            fixture.AddToken(date, 8, isSent: false);
            fixture.AddToken(date, 9, isSent: null, mediaSourceID: blind);
            fixture.AddToken(date, 10, isSent: null, mediaSourceID: blind);

            var census = fixture.Analyse();
            var group = census.DeviceGroups.Single();

            Assert.False(census.Sources.Single(row => row.MediaSourceID == blind)
                .RecordsAnyDirection);

            Assert.Equal(2, group.SourceCount);
            Assert.Equal(1, group.DirectionCapableSourceCount);
            Assert.Equal(1, group.DirectionBlindSourceCount);
            Assert.Equal(2, group.DirectionBlindSourceObservationCount);
            Assert.Equal(4, group.LogicalPositionsAfterCollapse);
            Assert.Equal(2, group.DirectionLabelledLogicalPositionCount);
            Assert.Equal(2, group.LogicalPositionsWithoutDirectionCount);
            Assert.Equal(2, group.PositionsKnownOnlyFromDirectionBlindSources);
            Assert.True(group.ReducesToOneDirectionCapableSource);
        }

        // ---------------------------------------------------------------------------------------
        // Supply adequacy.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Two outgoing message symbols against one outgoing token position: an outgoing shortfall of
        /// one, and a pair that is not order-testable at all.
        /// </remarks>
        [Fact]
        public void AnOutgoingShortfallMakesThePairNotOrderTestable()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");
            fixture.AddIncoming(date, "09:02:00");

            fixture.AddToken(date, 7, isSent: true);
            fixture.AddToken(date, 8, isSent: false);
            fixture.AddToken(date, 9, isSent: false);

            var scope = DirectionSequenceGateFixture
                .ScopeOf(fixture.Analyse(), ScopeLevel.SourceDate);

            Assert.Equal(1, scope.Supply.AfterCollapse.SupplyInsufficientPairCount);
            Assert.Equal(0, scope.Supply.AfterCollapse.SupplySufficientPairCount);
            Assert.Equal(3, scope.Supply.AfterCollapse.MessageObservationsInSupplyInsufficientPairs);

            Assert.Equal(
                [1],
                scope.Supply.AfterCollapse.OutgoingShortfallDistribution
                    .Select(row => row.Value)
                    .ToArray());

            Assert.Equal(1, scope.PairPopulation.SupplyInsufficientPairCount);
            Assert.Equal(0, scope.PairPopulation.ClassifiedPairCount);
        }

        [Fact]
        public void AnIncomingShortfallIsReportedOnItsOwnAxis()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddIncoming(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            fixture.AddToken(date, 7, isSent: true);
            fixture.AddToken(date, 8, isSent: false);

            var supply = DirectionSequenceGateFixture
                .ScopeOf(fixture.Analyse(), ScopeLevel.SourceDate)
                .Supply.AfterCollapse;

            Assert.Equal(1, supply.SupplyInsufficientPairCount);

            Assert.Equal(
                [0], supply.OutgoingShortfallDistribution.Select(row => row.Value).ToArray());

            Assert.Equal(
                [1], supply.IncomingShortfallDistribution.Select(row => row.Value).ToArray());
        }

        [Fact]
        public void SufficientSupplyLeavesBothShortfallsAtZero()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            // O O I I: two outgoing and two incoming positions in two runs, which is a class of two
            // arrangements rather than a determined one.
            fixture.AddToken(date, 7, isSent: true);
            fixture.AddToken(date, 8, isSent: true);
            fixture.AddToken(date, 9, isSent: false);
            fixture.AddToken(date, 10, isSent: false);

            var scope = DirectionSequenceGateFixture
                .ScopeOf(fixture.Analyse(), ScopeLevel.SourceDate);

            Assert.Equal(1, scope.Supply.AfterCollapse.SupplySufficientPairCount);
            Assert.Equal(0, scope.Supply.AfterCollapse.SupplyInsufficientPairCount);
            Assert.Equal(0, scope.PairPopulation.SupplyInsufficientPairCount);
            Assert.Equal(1, scope.PairPopulation.ClassifiedPairCount);
        }

        /// <remarks>
        /// Collapse is power-relevant, not cosmetic. Two copies of one logical position supply two
        /// outgoing symbols before collapse and one after, so a pair that looks sufficient on physical
        /// copies is insufficient on logical positions. Reporting only the second would absorb the
        /// effect instead of showing it.
        /// </remarks>
        [Fact]
        public void CollapseCanTurnASufficientPairInsufficientAndBothAreReported()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;
            var second = fixture.AddSource();

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");

            fixture.AddToken(date, 7, isSent: true);
            fixture.AddToken(date, 7, isSent: true, mediaSourceID: second);

            var supply = DirectionSequenceGateFixture
                .ScopeOf(fixture.Analyse(), ScopeLevel.DeviceGroupDate)
                .Supply;

            Assert.Equal(1, supply.BeforeCollapse.SupplySufficientPairCount);
            Assert.Equal(0, supply.BeforeCollapse.SupplyInsufficientPairCount);
            Assert.Equal(0, supply.AfterCollapse.SupplySufficientPairCount);
            Assert.Equal(1, supply.AfterCollapse.SupplyInsufficientPairCount);
            Assert.Equal(1, supply.PairsBecomingInsufficientAfterCollapse);
            Assert.Equal(0, supply.PairsBecomingSufficientAfterCollapse);
            Assert.Equal(2, supply.MessageObservationsLostToCollapse);
        }

        // ---------------------------------------------------------------------------------------
        // Degenerate pairs.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Degenerate pairs are counted by kind rather than filtered away. A date with messages and no
        /// token positions, a date with one message symbol, and a single-arrangement class are each
        /// reported, and none of them reaches the classified population.
        /// </remarks>
        [Fact]
        public void DegeneratePairsAreCensusedByKind()
        {
            using var fixture = new DirectionSequenceGateFixture();

            // A date whose scope emits nothing.
            var barren = DirectionSequenceGateFixture.FirstDate;
            fixture.AddOutgoing(barren, "09:00:00");
            fixture.AddIncoming(barren, "09:01:00");

            // One message symbol, amply supplied.
            var single = DirectionSequenceGateFixture.Day(1);
            fixture.AddOutgoing(single, "09:00:00");
            fixture.AddToken(single, 1, isSent: true);
            fixture.AddToken(single, 2, isSent: false);

            // Two message symbols against an all-outgoing token side, which is one arrangement.
            var oneSided = DirectionSequenceGateFixture.Day(2);
            fixture.AddOutgoing(oneSided, "09:00:00");
            fixture.AddOutgoing(oneSided, "09:01:00");
            fixture.AddToken(oneSided, 1, isSent: true);
            fixture.AddToken(oneSided, 2, isSent: true);

            // A date holding tokens but no message at all.
            var tokensOnly = DirectionSequenceGateFixture.Day(3);
            fixture.AddToken(tokensOnly, 1, isSent: true);

            var population = DirectionSequenceGateFixture
                .ScopeOf(fixture.Analyse(), ScopeLevel.SourceDate)
                .PairPopulation;

            Assert.Equal(4, population.PairCount);
            Assert.Equal(3, population.PairsWithMessageSymbols);
            Assert.Equal(3, population.PairsWithTokenPositions);

            Assert.Equal(1, population.Degenerate.NoTokenPositionPairCount);
            Assert.Equal(1, population.Degenerate.NoMessageSymbolPairCount);
            Assert.Equal(1, population.Degenerate.SingleMessageSymbolPairCount);
            Assert.Equal(1, population.Degenerate.NoIncomingTokenPositionPairCount);
            Assert.Equal(0, population.Degenerate.NoOutgoingTokenPositionPairCount);
            Assert.Equal(1, population.Degenerate.SingleArrangementPairCount);
            Assert.Equal(3, population.Degenerate.DegeneratePairCount);
            Assert.Equal(5, population.Degenerate.MessageObservationsInDegeneratePairs);
            Assert.Equal(0, population.ClassifiedPairCount);
        }

        // ---------------------------------------------------------------------------------------
        // The exact reference, and both determinacy classifications.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The design's named contrast, reached through the census rather than the mathematics: one
        /// outgoing and three incoming token positions in three runs, against the message pattern
        /// outgoing-then-incoming. Every arrangement of the class admits it, so the pair is binary
        /// determinate, but the embedding counts across the class are two and one, so the graded
        /// statistic can still move.
        /// </remarks>
        [Fact]
        public void ABinaryDeterminatePairCanStillBeGradedInformative()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            // The emitted sequence is I O I I: one outgoing, three incoming, three runs.
            fixture.AddToken(date, 1, isSent: false);
            fixture.AddToken(date, 2, isSent: true);
            fixture.AddToken(date, 3, isSent: false);
            fixture.AddToken(date, 4, isSent: false);

            var census = fixture.Analyse();
            var determinacy = DirectionSequenceGateFixture
                .ScopeOf(census, ScopeLevel.SourceDate)
                .Determinacy;

            Assert.Equal(1, determinacy.Population);
            Assert.Equal(1, determinacy.BinaryDeterminatePairCount);
            Assert.Equal(0, determinacy.BinaryInformativePairCount);
            Assert.Equal(0, determinacy.GradedDeterminatePairCount);
            Assert.Equal(1, determinacy.GradedInformativePairCount);
            Assert.Equal(1, determinacy.BinaryDeterminateAndGradedInformative);
            Assert.Equal(0, determinacy.BinaryInformativeAndGradedDeterminate);
            Assert.Equal(1, determinacy.CrossTabulationTotal);

            var rows = Capture(fixture);
            var row = Row(rows, ScopeLevel.SourceDate);

            Assert.Equal(2, (int)row.ArrangementCount);
            Assert.Equal(2, (int)row.AdmittingArrangementCount);
            Assert.Equal(3, (int)row.EmbeddingPairCount);
            Assert.Equal(5, (int)row.SquaredEmbeddingCount);
            Assert.Equal(1d, row.ConditionalAdmissionProbability);
            Assert.Equal(DirectionSequenceDeterminacyClass.Determinate, row.BinaryClass);
            Assert.Equal(DirectionSequenceDeterminacyClass.Informative, row.GradedClass);
            Assert.Equal(DirectionSequencePairState.Classified, row.State);
        }

        /// <remarks>
        /// The design's named graded-determinate counterexample, reached through the census: two
        /// outgoing and one incoming token position in two runs against the pattern
        /// outgoing-outgoing. The class is <c>{ OOI, IOO }</c> and both hold exactly one embedding, so
        /// the pair is determinate for both statistics.
        /// </remarks>
        [Fact]
        public void TheNamedCounterexampleIsDeterminateForBothStatistics()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");

            // The emitted sequence is O O I: two outgoing, one incoming, two runs.
            fixture.AddToken(date, 1, isSent: true);
            fixture.AddToken(date, 2, isSent: true);
            fixture.AddToken(date, 3, isSent: false);

            var rows = Capture(fixture);
            var row = Row(rows, ScopeLevel.SourceDate);

            Assert.Equal(2, (int)row.ArrangementCount);
            Assert.Equal(2, (int)row.AdmittingArrangementCount);
            Assert.Equal(2, (int)row.EmbeddingPairCount);
            Assert.Equal(2, (int)row.SquaredEmbeddingCount);
            Assert.Equal(DirectionSequenceDeterminacyClass.Determinate, row.BinaryClass);
            Assert.Equal(DirectionSequenceDeterminacyClass.Determinate, row.GradedClass);

            var determinacy = DirectionSequenceGateFixture
                .ScopeOf(fixture.Analyse(), ScopeLevel.SourceDate)
                .Determinacy;

            Assert.Equal(1, determinacy.BinaryDeterminateAndGradedDeterminate);
            Assert.Equal(0, determinacy.BinaryInformativeAndGradedDeterminate);
        }

        /// <remarks>
        /// A genuinely informative pair: some arrangements of its class admit the pattern and some do
        /// not, so <c>q_r</c> sits strictly between zero and one and both statistics can move.
        /// </remarks>
        [Fact]
        public void AGenuinelyInformativePairLandsInBothInformativePopulations()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            // The emitted sequence is O O I I: two outgoing, two incoming, two runs.
            fixture.AddToken(date, 1, isSent: true);
            fixture.AddToken(date, 2, isSent: true);
            fixture.AddToken(date, 3, isSent: false);
            fixture.AddToken(date, 4, isSent: false);

            var census = fixture.Analyse();
            var scope = DirectionSequenceGateFixture.ScopeOf(census, ScopeLevel.SourceDate);

            Assert.Equal(1, scope.Determinacy.BinaryInformativePairCount);
            Assert.Equal(1, scope.Determinacy.GradedInformativePairCount);
            Assert.Equal(1, scope.Determinacy.BinaryInformativeAndGradedInformative);
            Assert.Equal(0, scope.Determinacy.BinaryInformativeAndGradedDeterminate);

            // The class is { OOII, IIOO }, one of which admits OI, so q_r is one half.
            Assert.Equal(0.5d, scope.Reference.SumOfConditionalAdmissionProbability, 12);
            Assert.Equal(
                0.5d, scope.Reference.SumOfConditionalAdmissionProbabilityOverInformative, 12);

            Assert.Equal(1, scope.Reference.Bands.AboveOneQuarterToOneHalf);
            Assert.Equal(1, scope.Reference.Bands.BandTotal);

            var row = scope.Reference.BandRows
                .Single(band => band.Band == DirectionSequenceQrBand.AboveOneQuarterToOneHalf);

            Assert.Equal(1, row.PairCount);
            Assert.Equal(2, row.MessageObservationCount);
            Assert.Equal(4, row.TokenPositionCount);
            Assert.Equal(1, row.DistinctScopeKeyCount);
            Assert.Equal(1, row.DistinctDateCount);
        }

        /// <remarks>
        /// The composition-only reference would have called this pair informative; conditioning on the
        /// observed burstiness makes it determinate. That shrinkage is a required output, because run
        /// conditioning is a bias correction rather than a power gain and its cost has to be visible.
        /// </remarks>
        [Fact]
        public void RunConditioningShrinkageIsReported()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            // I O I I again: every arrangement of the three-run class admits OI, but only some
            // arrangements of the composition-only class do.
            fixture.AddToken(date, 1, isSent: false);
            fixture.AddToken(date, 2, isSent: true);
            fixture.AddToken(date, 3, isSent: false);
            fixture.AddToken(date, 4, isSent: false);

            var reference = DirectionSequenceGateFixture
                .ScopeOf(fixture.Analyse(), ScopeLevel.SourceDate)
                .Reference;

            Assert.Equal(1, reference.InformativeUnderExchangeableReferenceCount);
            Assert.Equal(0, reference.DeterminateUnderExchangeableReferenceCount);
            Assert.Equal(1, reference.InformativeLostToRunConditioningCount);

            // q(p, o, i) is 3/4 and q_r is 1, so the difference is negative and small.
            Assert.Equal(1, reference.ExchangeableLessConditionalAdmission.Negative);
            Assert.Equal(
                1, reference.ExchangeableLessConditionalAdmission.MagnitudeAtMostOneQuarter);
        }

        /// <remarks>
        /// Burstiness is measured against exchangeable expectation, and the message pattern's own
        /// clustering is reported beside it. Two outgoing and two incoming positions expect three runs;
        /// a fully clustered sequence holds two.
        /// </remarks>
        [Fact]
        public void BurstinessIsMeasuredAgainstExchangeableExpectation()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            fixture.AddToken(date, 1, isSent: true);
            fixture.AddToken(date, 2, isSent: true);
            fixture.AddToken(date, 3, isSent: false);
            fixture.AddToken(date, 4, isSent: false);

            var burstiness = DirectionSequenceGateFixture
                .ScopeOf(fixture.Analyse(), ScopeLevel.SourceDate)
                .Burstiness;

            Assert.Equal(1, burstiness.Population);
            Assert.Equal(2, burstiness.TokenRunCounts.Minimum);
            Assert.Equal(2, burstiness.TokenRunCounts.Maximum);
            Assert.Equal(-1d, burstiness.ObservedLessExpectedTokenRunCount.Minimum, 12);
            Assert.Equal(1, burstiness.ObservedLessExpectedTokenRunCount.Negative);
            Assert.Equal(1, burstiness.ObservedLessExpectedTokenRunCount.MagnitudeAtMostOne);

            Assert.Equal([2], burstiness.MessageRunCountDistribution
                .Select(row => row.Value)
                .ToArray());

            Assert.Equal(0, burstiness.NotOrderInformativePairCount);
            Assert.Equal(1, burstiness.WeaklyOrderInformativePairCount);
            Assert.Equal(0, burstiness.StrictlyOrderInformativePairCount);
        }

        /// <remarks>
        /// The dilution context is descriptive: how much of a date's emitted positions the conversation
        /// could account for at most. Two message symbols among eight positions is one quarter.
        /// </remarks>
        [Fact]
        public void TheConversationShareOfEmittedPositionsIsReportedAsContext()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            for (var token = 1; token <= 8; token++)
            {
                fixture.AddToken(date, token, isSent: token % 2 == 0);
            }

            var dilution = DirectionSequenceGateFixture
                .ScopeOf(fixture.Analyse(), ScopeLevel.SourceDate)
                .Dilution;

            Assert.Equal(1, dilution.Population);
            Assert.Equal(0.25d, dilution.ConversationShare.Minimum, 12);
            Assert.Equal(1, dilution.ConversationShare.MagnitudeAtMostOneQuarter);
            Assert.Equal(8, dilution.TokenPositions.Maximum);
            Assert.Equal([2], dilution.MessageSymbolDistribution
                .Select(row => row.Value)
                .ToArray());
        }

        // ---------------------------------------------------------------------------------------
        // Scope separation.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Both scopes are reported whole and neither is merged into the other. The same physical
        /// evidence at two scopes is largely the same evidence, so a summed total would manufacture
        /// weight from duplication.
        /// </remarks>
        [Fact]
        public void BothScopesAreReportedSeparatelyAndNeverMerged()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var date = DirectionSequenceGateFixture.FirstDate;
            var second = fixture.AddSource();

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            fixture.AddToken(date, 1, isSent: true);
            fixture.AddToken(date, 2, isSent: false);
            fixture.AddToken(date, 1, isSent: true, mediaSourceID: second);
            fixture.AddToken(date, 2, isSent: false, mediaSourceID: second);

            var census = fixture.Analyse();

            Assert.Equal(
                [ScopeLevel.SourceDate, ScopeLevel.DeviceGroupDate],
                census.Scopes.Select(scope => scope.Scope).ToArray());

            var perSource = DirectionSequenceGateFixture.ScopeOf(census, ScopeLevel.SourceDate);
            var perGroup = DirectionSequenceGateFixture.ScopeOf(census, ScopeLevel.DeviceGroupDate);

            Assert.Equal(2, perSource.PairPopulation.ScopeKeyCount);
            Assert.Equal(2, perSource.PairPopulation.PairCount);
            Assert.Equal(1, perGroup.PairPopulation.ScopeKeyCount);
            Assert.Equal(1, perGroup.PairPopulation.PairCount);
        }

        // ---------------------------------------------------------------------------------------
        // Privacy-safe pair rows.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Pair rows are keyed by a positional index over the gate's canonical ordering, so a rerun
        /// reproduces them exactly and none of them can be traced back to a local date. The rows are
        /// handed to a sink and are no part of the census the gate returns.
        /// </remarks>
        [Fact]
        public void PairIdentifiersAreDeterministicAndCarryNoDate()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(DirectionSequenceGateFixture.Day(5), "09:00:00");
            fixture.AddIncoming(DirectionSequenceGateFixture.FirstDate, "09:00:00");
            fixture.AddToken(DirectionSequenceGateFixture.FirstDate, 1, isSent: false);

            var first = Capture(fixture);
            var second = Capture(fixture);

            Assert.Equal(
                first.Select(row => (row.PairID, row.Scope, row.ScopeKeyID, row.State)),
                second.Select(row => (row.PairID, row.Scope, row.ScopeKeyID, row.State)));

            // Four pairs carry message symbols: two dates at each of the two scopes.
            Assert.Equal([1, 2, 3, 4], first.Select(row => row.PairID).ToArray());
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static List<DirectionSequencePairRow> Capture(DirectionSequenceGateFixture fixture)
        {
            var rows = new List<DirectionSequencePairRow>();

            fixture.Analyse(pairSink: rows.Add);

            return rows;
        }

        private static DirectionSequencePairRow Row(
            List<DirectionSequencePairRow> rows, ScopeLevel scope) =>
            rows.Single(row => row.Scope == scope);
    }
}
