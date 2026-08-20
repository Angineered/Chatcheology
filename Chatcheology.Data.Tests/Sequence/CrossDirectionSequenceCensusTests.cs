using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests what the census measures: the cohort's structure, which dates can supply an independent
    /// observation, and what the observations then say.
    /// </summary>
    /// <remarks>
    /// The fact most of these tests exist to pin: the frozen analysis derives candidates from a
    /// message's date and direction alone, so several relations on one date and direction are one piece
    /// of evidence. A test that let such relations contribute observations would be testing the wrong
    /// census.
    /// </remarks>
    public class CrossDirectionSequenceCensusTests
    {
        // ---------------------------------------------------------------------------------------
        // The clean shape.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void CleanCrossDirectionDate_ProducesOneConcordantObservation()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, outgoingToken: 11, incomingToken: 14);

            var census = fixture.Analyse();

            Assert.Equal(2, census.CohortStructure.CohortRelationCount);
            Assert.Equal(2, census.CohortStructure.QualifyingGroupCount);
            Assert.Equal(2, census.CohortStructure.DistinctCandidateAssetCount);
            Assert.Equal(1, census.CohortStructure.DistinctCohortDateCount);
            Assert.Equal(1, census.CohortStructure.BothDirectionDateCount);
            Assert.Equal(
                1, census.CohortStructure.CrossDirectionDates.BothDirectionGroupsSingleton);

            Assert.Equal(1, census.StrictDateTokenCardinality.StrictOrderableDateCount);
            Assert.Equal(1, census.PrimaryOrder.ObservationCount);
            Assert.Equal(1, census.PrimaryOrder.ConcordantCount);
            Assert.Equal(0, census.PrimaryOrder.DiscordantCount);
        }

        [Fact]
        public void EarlierMessageHoldingTheHigherToken_IsDiscordant()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, outgoingToken: 40, incomingToken: 9);

            var census = fixture.Analyse();

            Assert.Equal(1, census.PrimaryOrder.ObservationCount);
            Assert.Equal(0, census.PrimaryOrder.ConcordantCount);
            Assert.Equal(1, census.PrimaryOrder.DiscordantCount);
        }

        /// <remarks>
        /// Order comes from the committed message sequence number, never from which side is outgoing.
        /// The same two tokens flip verdict when the incoming message is the earlier one.
        /// </remarks>
        [Fact]
        public void MessageOrderComesFromSequenceNumberNotDirection()
        {
            using var outgoingFirst = new SequenceTestFixture();
            outgoingFirst.AddCleanDate(
                SequenceTestFixture.FirstDate, outgoingToken: 5, incomingToken: 8);

            using var incomingFirst = new SequenceTestFixture();
            incomingFirst.AddCleanDate(
                SequenceTestFixture.FirstDate,
                outgoingToken: 5,
                incomingToken: 8,
                outgoingFirst: false);

            Assert.Equal(1, outgoingFirst.Analyse().PrimaryOrder.ConcordantCount);
            Assert.Equal(1, incomingFirst.Analyse().PrimaryOrder.DiscordantCount);
        }

        [Fact]
        public void MatchingCensusIsCarriedThrough_AndAgreesWithTheCohortCount()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, outgoingToken: 1, incomingToken: 2);

            var census = fixture.Analyse();

            Assert.Equal(MatchingTestWorkspace.ConversationID, census.MatchingCensus.ConversationID);
            Assert.True(census.MatchingCensus.LocalParticipantIDSupplied);
            Assert.Equal(
                census.CohortStructure.CohortRelationCount,
                census.MatchingCensus.UniqueExactDateAndDirectionCompatibleCandidateCount);
        }

        // ---------------------------------------------------------------------------------------
        // C0: structure, and the shapes that cannot supply an observation.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Three outgoing messages on one date all name the one compatible asset. That is one piece of
        /// evidence proposed for three messages, and the census must not turn it into observations.
        /// </remarks>
        [Fact]
        public void MultiRelationGroup_IsCountedInC0AndExcludedFromTheStrictSample()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");
            fixture.AddOutgoing(date, "09:02:00");
            fixture.AddIncoming(date, "09:03:00");

            fixture.AddTokenAsset(date, isSent: true, 20);
            fixture.AddTokenAsset(date, isSent: false, 30);

            var census = fixture.Analyse();

            Assert.Equal(4, census.CohortStructure.CohortRelationCount);
            Assert.Equal(2, census.CohortStructure.QualifyingGroupCount);
            Assert.Equal(1, census.CohortStructure.SingletonGroupCount);
            Assert.Equal(1, census.CohortStructure.MultiRelationGroupCount);
            Assert.Equal(3, census.CohortStructure.RelationsInMultiRelationGroups);
            Assert.Equal(3, census.CohortStructure.MaximumRelationsInOneGroup);
            Assert.Equal(
                1, census.CohortStructure.CrossDirectionDates.OneSingletonOneMultiRelation);
            Assert.Equal(
                0, census.CohortStructure.CrossDirectionDates.BothDirectionGroupsSingleton);

            Assert.Equal(0, census.StrictDateTokenCardinality.SingletonBothDirectionDateCount);
            Assert.Equal(0, census.PrimaryOrder.ObservationCount);
        }

        [Fact]
        public void BothDirectionGroupsHoldingSeveralRelations_AreTheirOwnCategory()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");
            fixture.AddIncoming(date, "09:02:00");
            fixture.AddIncoming(date, "09:03:00");

            fixture.AddTokenAsset(date, isSent: true, 20);
            fixture.AddTokenAsset(date, isSent: false, 30);

            var census = fixture.Analyse();

            Assert.Equal(1, census.CohortStructure.CrossDirectionDates.BothMultiRelation);
            Assert.Equal(2, census.CohortStructure.RelationsPerGroup.Two);
            Assert.Equal(2, census.CohortStructure.RelationsPerGroup.Median);
            Assert.Equal(0, census.PrimaryOrder.ObservationCount);
        }

        [Fact]
        public void DateCarryingOneDirectionOnly_SuppliesNoObservation()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 12);

            var census = fixture.Analyse();

            Assert.Equal(1, census.CohortStructure.OutgoingOnlyDateCount);
            Assert.Equal(0, census.CohortStructure.IncomingOnlyDateCount);
            Assert.Equal(0, census.CohortStructure.BothDirectionDateCount);
            Assert.Equal(0, census.PrimaryOrder.ObservationCount);
        }

        [Fact]
        public void IncomingOnlyDate_IsCountedAsSuch()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddIncoming(date);
            fixture.AddTokenAsset(date, isSent: false, 12);

            var census = fixture.Analyse();

            Assert.Equal(1, census.CohortStructure.IncomingOnlyDateCount);
            Assert.Equal(0, census.CohortStructure.OutgoingOnlyDateCount);
        }

        /// <remarks>
        /// Two compatible assets on one date leave the attachment with no unique candidate, so it never
        /// enters the cohort at all.
        /// </remarks>
        [Fact]
        public void AttachmentWithTwoCompatibleCandidates_IsNotInTheCohort()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);
            fixture.AddTokenAsset(date, isSent: true, 2);

            var census = fixture.Analyse();

            Assert.Equal(0, census.CohortStructure.CohortRelationCount);
            Assert.Equal(0, census.CohortStructure.QualifyingGroupCount);
        }

        /// <remarks>
        /// A contradicting copy makes the only same-direction asset incompatible, so the relation is
        /// absent even though the date holds media.
        /// </remarks>
        [Fact]
        public void AttachmentWhoseOnlyCandidateContradictsDirection_IsNotInTheCohort()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: false, 1);

            Assert.Equal(0, fixture.Analyse().CohortStructure.CohortRelationCount);
        }

        [Fact]
        public void SystemMessageAttachment_HasNoDirectionAndNoCohortRelation()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.Workspace.AddMediaAttachment(date, senderParticipantID: null);
            fixture.AddTokenAsset(date, isSent: true, 1);

            Assert.Equal(0, fixture.Analyse().CohortStructure.CohortRelationCount);
        }

        // ---------------------------------------------------------------------------------------
        // C1: token cardinality and the funnel.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void SeveralDistinctTokensOnOneSide_ExcludesTheDateWithoutChoosingAToken()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddIncoming(date);

            fixture.AddTokenAsset(date, isSent: true, 7, 9, 11);
            fixture.AddTokenAsset(date, isSent: false, 20);

            var census = fixture.Analyse();

            Assert.Equal(1, census.StrictDateTokenCardinality.SingletonBothDirectionDateCount);
            Assert.Equal(1, census.StrictDateTokenCardinality.SeveralDistinctTokens);
            Assert.Equal(1, census.StrictDateTokenCardinality.ExactlyOneDistinctToken);
            Assert.Equal(1, census.StrictDateTokenCardinality.DatesExcludedSeveralTokens);
            Assert.Equal(0, census.StrictDateTokenCardinality.StrictOrderableDateCount);
            Assert.Equal(0, census.PrimaryOrder.ObservationCount);

            Assert.Equal(1, census.StrictDateTokenCardinality.DistinctTokenCounts.One);
            Assert.Equal(1, census.StrictDateTokenCardinality.DistinctTokenCounts.ThreeToFive);
        }

        [Fact]
        public void NoSupportedTokenOnOneSide_ExcludesTheDate()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddIncoming(date);

            fixture.AddNamedAsset(date, isSent: true, "0001-1");
            fixture.AddTokenAsset(date, isSent: false, 20);

            var census = fixture.Analyse();

            Assert.Equal(1, census.StrictDateTokenCardinality.NoSupportedToken);
            Assert.Equal(1, census.StrictDateTokenCardinality.DatesExcludedNoSupportedToken);
            Assert.Equal(0, census.StrictDateTokenCardinality.StrictOrderableDateCount);
            Assert.Equal(1, census.StrictDateTokenCardinality.DistinctTokenCounts.Zero);
        }

        /// <remarks>
        /// Equal tokens across two different assets are possible — Stage B1 found one
        /// <c>(date, token)</c> key held by more than one asset — and are reported rather than ordered.
        /// </remarks>
        [Fact]
        public void EqualTokens_AreReportedAndNeverOrdered()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddIncoming(date);

            fixture.AddTokenAsset(date, isSent: true, 42);
            fixture.AddTokenAsset(date, isSent: false, 42);

            var census = fixture.Analyse();

            Assert.Equal(1, census.StrictDateTokenCardinality.DatesExcludedEqualToken);
            Assert.Equal(0, census.StrictDateTokenCardinality.StrictOrderableDateCount);
            Assert.Equal(0, census.PrimaryOrder.ObservationCount);
        }

        [Fact]
        public void ExclusionsFollowTheStatedPrecedence()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddIncoming(date);

            // One side carries nothing, the other carries several: the no-token exclusion wins.
            fixture.AddNamedAsset(date, isSent: true, "abcd");
            fixture.AddTokenAsset(date, isSent: false, 5, 6);

            var census = fixture.Analyse();

            Assert.Equal(1, census.StrictDateTokenCardinality.DatesExcludedNoSupportedToken);
            Assert.Equal(0, census.StrictDateTokenCardinality.DatesExcludedSeveralTokens);
        }

        // ---------------------------------------------------------------------------------------
        // Direction provenance of the token evidence.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The asset is compatible because a sent-folder copy supports it, but that copy's suffix is not
        /// the approved grammar, so every token the relation has comes from a copy recording no
        /// direction at all. Filtering those out would silently redefine the relation.
        /// </remarks>
        [Fact]
        public void TokensSeenOnlyOnUnknownDirectionCopies_AreClassifiedUnknownOnly()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = SequenceTestFixture.HashOf(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);
            fixture.AddCopy(assetID, sha, date, isSent: true, "0001-copy");
            fixture.AddCopy(assetID, sha, date, isSent: null, SequenceTestFixture.Token(31));

            var census = fixture.Analyse();

            Assert.Equal(1, census.GroupWeightedTokenProvenance.UnknownOnly);
            Assert.Equal(0, census.GroupWeightedTokenProvenance.AgreeingOnly);
            Assert.Equal(1, census.RelationWeightedTokenProvenance.UnknownOnly);
        }

        [Fact]
        public void TokensOnBothAgreeingAndUnknownCopies_AreClassifiedAsBoth()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = SequenceTestFixture.HashOf(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);
            fixture.AddCopy(assetID, sha, date, isSent: true, SequenceTestFixture.Token(31));
            fixture.AddCopy(assetID, sha, date, isSent: null, SequenceTestFixture.Token(31));

            var census = fixture.Analyse();

            Assert.Equal(1, census.GroupWeightedTokenProvenance.AgreeingAndUnknown);
        }

        /// <remarks>
        /// The two weightings differ exactly where the cohort repeats one asset, which is the whole
        /// reason they are reported apart.
        /// </remarks>
        [Fact]
        public void RelationAndGroupWeightedProvenance_DifferOnRepeatedEvidence()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");
            fixture.AddOutgoing(date, "09:02:00");

            fixture.AddTokenAsset(date, isSent: true, 12);

            var census = fixture.Analyse();

            Assert.Equal(3, census.RelationWeightedTokenProvenance.AgreeingOnly);
            Assert.Equal(3, census.RelationWeightedTokenProvenance.Total);
            Assert.Equal(1, census.GroupWeightedTokenProvenance.AgreeingOnly);
            Assert.Equal(1, census.GroupWeightedTokenProvenance.Total);
        }

        [Fact]
        public void RelationWithoutSupportedTokens_IsCountedInBothWeightings()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddNamedAsset(date, isSent: true, string.Empty);

            var census = fixture.Analyse();

            Assert.Equal(1, census.RelationWeightedTokenProvenance.NoSupportedToken);
            Assert.Equal(1, census.GroupWeightedTokenProvenance.NoSupportedToken);
        }

        // ---------------------------------------------------------------------------------------
        // C4: displacement.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void DisplacementIsBandedByMagnitudeAndKeptApartByVerdict()
        {
            using var fixture = new SequenceTestFixture();

            // Concordant, difference 1.
            fixture.AddCleanDate(SequenceTestFixture.Day(0), outgoingToken: 10, incomingToken: 11);

            // Concordant, difference 30.
            fixture.AddCleanDate(SequenceTestFixture.Day(7), outgoingToken: 10, incomingToken: 40);

            // Discordant, magnitude 2.
            fixture.AddCleanDate(SequenceTestFixture.Day(14), outgoingToken: 60, incomingToken: 58);

            var census = fixture.Analyse();

            Assert.Equal(3, census.PrimaryOrder.ObservationCount);
            Assert.Equal(2, census.PrimaryOrder.ConcordantCount);
            Assert.Equal(1, census.PrimaryOrder.DiscordantCount);

            Assert.Equal(1, census.Displacement.Concordant.One);
            Assert.Equal(1, census.Displacement.Concordant.TwentySixToFifty);
            Assert.Equal(2, census.Displacement.Concordant.Total);

            Assert.Equal(1, census.Displacement.Discordant.Two);
            Assert.Equal(1, census.Displacement.Discordant.Total);
        }

        [Theory]
        [InlineData(3, "ThreeToFive")]
        [InlineData(5, "ThreeToFive")]
        [InlineData(6, "SixToTen")]
        [InlineData(10, "SixToTen")]
        [InlineData(11, "ElevenToTwentyFive")]
        [InlineData(25, "ElevenToTwentyFive")]
        [InlineData(26, "TwentySixToFifty")]
        [InlineData(50, "TwentySixToFifty")]
        [InlineData(51, "MoreThanFifty")]
        public void DisplacementBandEdges_FallWhereStated(int difference, string band)
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(
                SequenceTestFixture.FirstDate, outgoingToken: 100, incomingToken: 100 + difference);

            var concordant = fixture.Analyse().Displacement.Concordant;

            var counts = new Dictionary<string, int>
            {
                ["ThreeToFive"] = concordant.ThreeToFive,
                ["SixToTen"] = concordant.SixToTen,
                ["ElevenToTwentyFive"] = concordant.ElevenToTwentyFive,
                ["TwentySixToFifty"] = concordant.TwentySixToFifty,
                ["MoreThanFifty"] = concordant.MoreThanFifty,
            };

            Assert.Equal(1, counts[band]);
            Assert.Equal(1, concordant.Total);
        }

        // ---------------------------------------------------------------------------------------
        // C3: the exact chance baseline.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void OneConcordantObservation_HasAnExactProbabilityOfAHalf()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, outgoingToken: 1, incomingToken: 2);

            var order = fixture.Analyse().PrimaryOrder;

            Assert.Equal("1", order.ExactOneSidedProbabilityNumerator);
            Assert.Equal(1, order.ExactOneSidedProbabilityDenominatorExponent);
            Assert.Equal("5.00000000000e-01", order.ExactOneSidedProbability);
        }

        [Fact]
        public void NoObservation_HasNoProbabilityAtAll()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddOutgoing(SequenceTestFixture.FirstDate);
            fixture.AddTokenAsset(SequenceTestFixture.FirstDate, isSent: true, 5);

            var order = fixture.Analyse().PrimaryOrder;

            Assert.Equal(0, order.ObservationCount);
            Assert.Null(order.ExactOneSidedProbabilityNumerator);
            Assert.Null(order.ExactOneSidedProbability);
            Assert.Equal(0, order.ExactOneSidedProbabilityDenominatorExponent);
        }

        [Fact]
        public void EveryObservationDiscordant_GivesAProbabilityOfOne()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.Day(0), outgoingToken: 20, incomingToken: 10);
            fixture.AddCleanDate(SequenceTestFixture.Day(7), outgoingToken: 20, incomingToken: 10);

            var order = fixture.Analyse().PrimaryOrder;

            Assert.Equal(0, order.ConcordantCount);
            Assert.Equal("4", order.ExactOneSidedProbabilityNumerator);
            Assert.Equal("1.00000000000e+00", order.ExactOneSidedProbability);
        }

        /// <remarks>
        /// Twenty observations with eighteen concordant is the pre-registered power floor met at the
        /// pre-registered concordance threshold. The exact figure it produces is 211 / 2^20, and it is
        /// pinned here so a change in the arithmetic cannot pass unnoticed.
        /// </remarks>
        [Fact]
        public void TwentyObservationsWithEighteenConcordant_GiveTheExactPinnedProbability()
        {
            using var fixture = new SequenceTestFixture();

            for (var index = 0; index < 20; index++)
            {
                var concordant = index >= 2;

                fixture.AddCleanDate(
                    SequenceTestFixture.Day(index * 7),
                    outgoingToken: concordant ? 100 : 300,
                    incomingToken: concordant ? 200 : 150);
            }

            var order = fixture.Analyse().PrimaryOrder;

            Assert.Equal(20, order.ObservationCount);
            Assert.Equal(18, order.ConcordantCount);
            Assert.Equal(2, order.DiscordantCount);
            Assert.Equal("211", order.ExactOneSidedProbabilityNumerator);
            Assert.Equal(20, order.ExactOneSidedProbabilityDenominatorExponent);
            Assert.Equal("2.01225280761e-04", order.ExactOneSidedProbability);
        }

        // ---------------------------------------------------------------------------------------
        // C6: the sensitivity view.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void SensitivityCountsOnlyObservationsWhoseTokensSitOnAgreeingCopies()
        {
            using var fixture = new SequenceTestFixture();

            // A clean date: both tokens on direction-agreeing copies.
            fixture.AddCleanDate(SequenceTestFixture.Day(0), outgoingToken: 10, incomingToken: 20);

            // A second date whose outgoing token is only on an unknown-direction copy.
            var date = SequenceTestFixture.Day(7);
            fixture.AddOutgoing(date, "10:00:00");
            fixture.AddIncoming(date, "10:05:00");

            var sha = SequenceTestFixture.HashOf(91);
            var assetID = fixture.Workspace.AddMediaAsset(sha);
            fixture.AddCopy(assetID, sha, date, isSent: true, "0005-copy");
            fixture.AddCopy(assetID, sha, date, isSent: null, SequenceTestFixture.Token(5));

            fixture.AddTokenAsset(date, isSent: false, 60);

            var census = fixture.Analyse();

            Assert.Equal(2, census.PrimaryOrder.ObservationCount);
            Assert.Equal(2, census.PrimaryOrder.ConcordantCount);

            Assert.Equal(1, census.DirectionAgreeingSensitivity.ObservationCount);
            Assert.Equal(1, census.DirectionAgreeingSensitivity.ConcordantCount);
        }

        // ---------------------------------------------------------------------------------------
        // C5: the direction/token namespace diagnostic.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void SeparatedDirectionRanges_AreClassifiedByWhichSitsBelow()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddTokenAsset(date, isSent: true, 10, 11);
            fixture.AddTokenAsset(date, isSent: false, 40, 41);

            var diagnostic = fixture.Analyse().DirectionNamespace;

            Assert.Equal(4, diagnostic.KnownDirectionSupportedOccurrenceCount);
            Assert.Equal(1, diagnostic.BothDirectionClassDateCount);
            Assert.Equal(1, diagnostic.OutgoingRangeEntirelyBelowIncomingDateCount);
            Assert.Equal(0, diagnostic.IncomingRangeEntirelyBelowOutgoingDateCount);
            Assert.Equal(0, diagnostic.OverlapOrInterleaveDateCount);
            Assert.Equal(0, diagnostic.SingletonInvolvedDateCount);
            Assert.Equal(1, diagnostic.TransitionCounts.One);
        }

        [Fact]
        public void IncomingRangeBelowOutgoing_IsItsOwnClass()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddTokenAsset(date, isSent: true, 40, 41);
            fixture.AddTokenAsset(date, isSent: false, 10, 11);

            var diagnostic = fixture.Analyse().DirectionNamespace;

            Assert.Equal(1, diagnostic.IncomingRangeEntirelyBelowOutgoingDateCount);
            Assert.Equal(0, diagnostic.OutgoingRangeEntirelyBelowIncomingDateCount);
        }

        [Fact]
        public void InterleavedDirectionTokens_AreCountedAsOverlapWithTheirTransitions()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddTokenAsset(date, isSent: true, 10, 30);
            fixture.AddTokenAsset(date, isSent: false, 20, 40);

            var diagnostic = fixture.Analyse().DirectionNamespace;

            Assert.Equal(1, diagnostic.OverlapOrInterleaveDateCount);
            Assert.Equal(2, diagnostic.OutgoingOnlyTokenCount);
            Assert.Equal(2, diagnostic.IncomingOnlyTokenCount);
            Assert.Equal(0, diagnostic.BothClassTokenCount);
            Assert.Equal(1, diagnostic.TransitionCounts.ThreeToFive);
        }

        /// <remarks>
        /// The singleton class is taken first, so a date where one direction contributes a single token
        /// is never described as a separated or overlapping range.
        /// </remarks>
        [Fact]
        public void SingletonRange_TakesPrecedenceOverTheRangeComparison()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddTokenAsset(date, isSent: true, 10);
            fixture.AddTokenAsset(date, isSent: false, 40, 41);

            var diagnostic = fixture.Analyse().DirectionNamespace;

            Assert.Equal(1, diagnostic.SingletonInvolvedDateCount);
            Assert.Equal(0, diagnostic.OutgoingRangeEntirelyBelowIncomingDateCount);
        }

        /// <remarks>
        /// A token seen in both classes says nothing about which class came first at that position, so
        /// it breaks the chain instead of contributing a change. Sorted tokens here are
        /// 10 (out), 20 (both), 30 (in): without the rule this would read as two transitions.
        /// </remarks>
        [Fact]
        public void SharedTokenBreaksTheTransitionChainAndIsReportedSeparately()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddTokenAsset(date, isSent: true, 10, 20);
            fixture.AddTokenAsset(date, isSent: false, 20, 30);

            var diagnostic = fixture.Analyse().DirectionNamespace;

            Assert.Equal(1, diagnostic.DatesContainingSharedToken);
            Assert.Equal(1, diagnostic.BothClassTokenCount);
            Assert.Equal(1, diagnostic.OutgoingOnlyTokenCount);
            Assert.Equal(1, diagnostic.IncomingOnlyTokenCount);
            Assert.Equal(1, diagnostic.TransitionCounts.Zero);
            Assert.Equal(1, diagnostic.OverlapOrInterleaveDateCount);
        }

        [Fact]
        public void DateWithOneDirectionClassOnly_IsNotADiagnosticDate()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddTokenAsset(date, isSent: true, 10, 11);

            var diagnostic = fixture.Analyse().DirectionNamespace;

            Assert.Equal(2, diagnostic.KnownDirectionSupportedOccurrenceCount);
            Assert.Equal(0, diagnostic.BothDirectionClassDateCount);
        }

        [Fact]
        public void UnknownDirectionOccurrences_StayOutOfTheDiagnostic()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddTokenAsset(date, isSent: null, 10, 11);
            fixture.AddTokenAsset(date, isSent: false, 20);

            var diagnostic = fixture.Analyse().DirectionNamespace;

            Assert.Equal(1, diagnostic.KnownDirectionSupportedOccurrenceCount);
            Assert.Equal(0, diagnostic.BothDirectionClassDateCount);
        }

        /// <remarks>
        /// A payload with no bytes is never candidate evidence, and it is kept out of this diagnostic
        /// for the same reason.
        /// </remarks>
        [Fact]
        public void ZeroByteAssetOccurrences_StayOutOfTheDiagnostic()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            var sha = SequenceTestFixture.HashOf(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha, sizeBytes: 0);
            fixture.AddCopy(assetID, sha, date, isSent: true, SequenceTestFixture.Token(10));

            fixture.AddTokenAsset(date, isSent: false, 20);

            var diagnostic = fixture.Analyse().DirectionNamespace;

            Assert.Equal(1, diagnostic.KnownDirectionSupportedOccurrenceCount);
            Assert.Equal(0, diagnostic.BothDirectionClassDateCount);
        }

        [Fact]
        public void UnsupportedSuffixes_ContributeNothingToTheDiagnostic()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddNamedAsset(date, isSent: true, "0010", "00100", "abcd", string.Empty);
            fixture.AddNamedAsset(date, isSent: false, "0020");

            var diagnostic = fixture.Analyse().DirectionNamespace;

            Assert.Equal(2, diagnostic.KnownDirectionSupportedOccurrenceCount);
            Assert.Equal(1, diagnostic.BothDirectionClassDateCount);
            Assert.Equal(1, diagnostic.SingletonInvolvedDateCount);
        }

        // ---------------------------------------------------------------------------------------
        // Dates are never compared across the boundary.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TokensOnDifferentDates_AreNeverCompared()
        {
            using var fixture = new SequenceTestFixture();

            fixture.AddOutgoing(SequenceTestFixture.Day(0));
            fixture.AddTokenAsset(SequenceTestFixture.Day(0), isSent: true, 90);

            fixture.AddIncoming(SequenceTestFixture.Day(7));
            fixture.AddTokenAsset(SequenceTestFixture.Day(7), isSent: false, 10);

            var census = fixture.Analyse();

            Assert.Equal(2, census.CohortStructure.DistinctCohortDateCount);
            Assert.Equal(0, census.CohortStructure.BothDirectionDateCount);
            Assert.Equal(0, census.PrimaryOrder.ObservationCount);
        }

        /// <remarks>
        /// A copy dated the day before the message is adjacent-date evidence, which the frozen analysis
        /// keeps out of the exact-date candidate set. It cannot become sequence evidence here either.
        /// </remarks>
        [Fact]
        public void AdjacentDateCopy_IsNotSequenceEvidence()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.Day(1);

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date.AddDays(-1), isSent: true, 10);

            var census = fixture.Analyse();

            Assert.Equal(0, census.CohortStructure.CohortRelationCount);
        }
    }
}
