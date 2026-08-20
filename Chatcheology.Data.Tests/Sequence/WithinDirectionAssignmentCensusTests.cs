using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests the within-direction assignment census: feasibility, the per-message token-position range,
    /// the exactness of the candidate union, and the arithmetic behind both.
    /// </summary>
    /// <remarks>
    /// Everything measured here is conditional on the untested sequence-order hypothesis. These tests
    /// check that the census computes the hypothesis's consequences correctly — not that the hypothesis
    /// holds, which no stage has established.
    /// </remarks>
    public class WithinDirectionAssignmentCensusTests
    {
        // ---------------------------------------------------------------------------------------
        // T >= M is necessary and sufficient.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Three messages, two token positions. No strictly increasing assignment exists, so the group
        /// is impossible however the positions are populated.
        /// </remarks>
        [Fact]
        public void FewerTokenPositionsThanMessages_IsImpossible()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");
            fixture.AddOutgoing(date, "09:02:00");

            fixture.AddTokenAsset(date, isSent: true, 10);
            fixture.AddTokenAsset(date, isSent: true, 20);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.Feasibility.TooFewTokenPositionGroups);
            Assert.Equal(0, census.Feasibility.EnoughTokenPositionGroups);
            Assert.Equal(3, census.Feasibility.MessagesInTooFewTokenPositionGroups);
            Assert.Equal(1, census.PooledSlack.Groups.Negative);
            Assert.Equal(1, census.ImpossibleGroups.Shortfall.OneToFive);
            Assert.Equal(3, census.ImpossibleGroups.MessagesInImpossibleGroups);

            // Baseline relations of an impossible group are reported, never netted off.
            Assert.Equal(6, census.ImpossibleGroups.BaselineRelationsInImpossibleGroups);
            Assert.Equal(0, census.PooledNarrowing.MessageTotal);
            Assert.Equal(0, census.AssignmentCounts.FeasibleGroupCount);
        }

        [Fact]
        public void AsManyTokenPositionsAsMessages_IsFeasibleAndForcesEveryPosition()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");

            fixture.AddTokenAsset(date, isSent: true, 10);
            fixture.AddTokenAsset(date, isSent: true, 20);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.Feasibility.EnoughTokenPositionGroups);
            Assert.Equal(1, census.PooledSlack.Groups.Zero);
            Assert.Equal(2, census.PooledSlack.Messages.Zero);

            Assert.Equal(1, census.ForcedPositions.Groups);
            Assert.Equal(2, census.ForcedPositions.Messages);
            Assert.Equal(2, census.ForcedPositions.MessagesWhereTokenHoldsOneAsset);
            Assert.Equal(0, census.ForcedPositions.MessagesWhereTokenHoldsSeveralAssets);

            Assert.Equal(2, census.PositionAmbiguity.MessagesByPossibleTokenPositionCount.One);

            // Each message is forced onto one position holding one asset, from a baseline of two.
            Assert.Equal(2, census.PooledNarrowing.UniqueCandidateUnderSequenceOrderHypothesis);
            Assert.Equal(4, census.PooledNarrowing.BaselineCandidateRelations);
            Assert.Equal(2, census.PooledNarrowing.SequenceCompatibleCandidateRelations);
        }

        [Fact]
        public void MoreTokenPositionsThanMessages_LeavesPositionalFreedom()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");

            fixture.AddTokenAsset(date, isSent: true, 10);
            fixture.AddTokenAsset(date, isSent: true, 20);
            fixture.AddTokenAsset(date, isSent: true, 30);
            fixture.AddTokenAsset(date, isSent: true, 40);
            fixture.AddTokenAsset(date, isSent: true, 50);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.Feasibility.EnoughTokenPositionGroups);
            Assert.Equal(1, census.PooledSlack.Groups.ThreeToFive);
            Assert.Equal(0, census.ForcedPositions.Groups);

            // T - M + 1 = 4 possible positions for each of the two messages.
            Assert.Equal(4, census.PositionAmbiguity.PossibleTokenPositionCountPerGroup.Maximum);
            Assert.Equal(2, census.PositionAmbiguity.MessagesByPossibleTokenPositionCount.ThreeToFive);
        }

        [Fact]
        public void GroupWithNoCompatibleCandidateAsset_IsClassifiedApartFromMissingTokens()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            // Direction-contradicting evidence, so the asset is a candidate but never compatible.
            fixture.AddTokenAsset(date, isSent: false, 10);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.Feasibility.NoCompatibleCandidateAssetGroups);
            Assert.Equal(0, census.Feasibility.NoSupportedTokenPositionGroups);
            Assert.Equal(1, census.ImpossibleGroups.NoCompatibleCandidateAssetGroups);
            Assert.Equal(0, census.ImpossibleGroups.BaselineRelationsInImpossibleGroups);
        }

        [Fact]
        public void CompatibleAssetWithNoSupportedToken_IsItsOwnImpossibleClass()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddNamedAsset(date, isSent: true, "0001-1");

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.Feasibility.NoSupportedTokenPositionGroups);
            Assert.Equal(1, census.ImpossibleGroups.NoSupportedTokenPositionGroups);
            Assert.Equal(1, census.ImpossibleGroups.BaselineRelationsInImpossibleGroups);
        }

        // ---------------------------------------------------------------------------------------
        // The r .. T - M + r range, and the exactness of the union over it.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Two messages, three positions: asset A at tokens 10 and 20, asset B at token 30. Message 1's
        /// range is positions 1-2, which holds only A, so it is unique under the hypothesis. Message 2's
        /// range is positions 2-3, which holds A and B. Using the whole position set for both ranks —
        /// the obvious wrong implementation — would give two candidates each and no uniqueness.
        /// </remarks>
        [Fact]
        public void PerMessageRangeIsTheLowerAndUpperBound_NotTheWholePositionSet()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");

            fixture.AddTokenAsset(date, isSent: true, 10, 20);
            fixture.AddTokenAsset(date, isSent: true, 30);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.Feasibility.EnoughTokenPositionGroups);
            Assert.Equal(1, census.PooledNarrowing.UniqueCandidateUnderSequenceOrderHypothesis);
            Assert.Equal(1, census.PooledNarrowing.NoReduction);
            Assert.Equal(4, census.PooledNarrowing.BaselineCandidateRelations);
            Assert.Equal(3, census.PooledNarrowing.SequenceCompatibleCandidateRelations);
            Assert.Equal(1, census.PooledNarrowing.AbsoluteReduction);
        }

        /// <remarks>
        /// The mirror case: asset A only at the first position, asset B at the last two. Message 1 sees
        /// both; message 2 sees only B. The reduction total pins the window, because any off-by-one in
        /// either bound changes it.
        /// </remarks>
        [Fact]
        public void AssetOnlyAtTheFirstPosition_LeavesTheLaterMessageWithoutIt()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");

            fixture.AddTokenAsset(date, isSent: true, 10);
            fixture.AddTokenAsset(date, isSent: true, 20, 30);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.PooledNarrowing.NoReduction);
            Assert.Equal(1, census.PooledNarrowing.UniqueCandidateUnderSequenceOrderHypothesis);
            Assert.Equal(3, census.PooledNarrowing.SequenceCompatibleCandidateRelations);
        }

        /// <remarks>
        /// Five positions, three messages: rank 1 sees positions 1-3, rank 2 sees 2-4, rank 3 sees 3-5.
        /// With one distinct asset per position the union counts are 3, 3, 3 — nine relations from a
        /// baseline of fifteen — which no other window interpretation produces.
        /// </remarks>
        [Fact]
        public void SlidingWindowCoversExactlyThreeConsecutivePositionsPerRank()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");
            fixture.AddOutgoing(date, "09:02:00");

            for (var token = 10; token <= 50; token += 10)
            {
                fixture.AddTokenAsset(date, isSent: true, token);
            }

            var census = fixture.AnalyseAssignments();

            Assert.Equal(15, census.PooledNarrowing.BaselineCandidateRelations);
            Assert.Equal(9, census.PooledNarrowing.SequenceCompatibleCandidateRelations);
            Assert.Equal(3, census.PooledNarrowing.ReducedUnderSequenceOrderHypothesis);
        }

        /// <remarks>
        /// A baseline of one candidate is not a uniqueness result: the message had one candidate before
        /// the hypothesis was applied, so it counts as no reduction and is reported apart.
        /// </remarks>
        [Fact]
        public void MessageAlreadyUniqueWithoutTheHypothesis_IsNotCountedAsAUniquenessResult()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 10);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.PooledNarrowing.NoReduction);
            Assert.Equal(0, census.PooledNarrowing.UniqueCandidateUnderSequenceOrderHypothesis);
            Assert.Equal(1, census.PooledNarrowing.MessagesAlreadyUniqueWithoutHypothesis);
        }

        // ---------------------------------------------------------------------------------------
        // Occurrence model.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void OneAssetAtSeveralTokens_IsSeveralOccurrencesAndSeveralPositions()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 10, 20, 30);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.PooledPopulation.CompatibleAssetsPerGroup.One);
            Assert.Equal(1, census.PooledPopulation.TokenPositionsPerGroup.ThreeToFive);
            Assert.Equal(1, census.PooledPopulation.OccurrencesPerGroup.ThreeToFive);

            Assert.Equal(1, census.AssetMultiplicity.AssetGroupRelationsWithSeveralTokens);
            Assert.Equal(0, census.AssetMultiplicity.AssetGroupRelationsWithOneToken);
            Assert.Equal(3, census.AssetMultiplicity.MaximumTokenPositionsForOneAsset);
            Assert.Equal(1, census.AssetMultiplicity.GroupsWithARepeatedAsset);
        }

        /// <remarks>
        /// One payload recovered at one position from two acquisition stores is one occurrence. Letting
        /// acquisition duplication add positions would inflate the assignment space with copies of one
        /// piece of evidence.
        /// </remarks>
        [Fact]
        public void SameAssetAndTokenInTwoAcquisitionStores_CollapsesToOneOccurrence()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var second = fixture.AddSource();
            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.AddCopy(assetID, sha, date, isSent: true, SequenceTestFixture.Token(10));
            fixture.AddCopy(
                assetID,
                sha,
                date,
                isSent: true,
                SequenceTestFixture.Token(10),
                mediaSourceID: second);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.PooledPopulation.TokenPositionsPerGroup.One);
            Assert.Equal(1, census.PooledPopulation.OccurrencesPerGroup.One);
            Assert.Equal(1, census.AssetMultiplicity.AssetGroupRelationsWithOneToken);
            Assert.Equal(0, census.AssetMultiplicity.AssetGroupRelationsWithSeveralTokens);
        }

        /// <remarks>
        /// Two assets at one token is Stage B1's archive-wide collision shape. The position is
        /// represented with both assets and never broken by a tie rule.
        /// </remarks>
        [Fact]
        public void TwoAssetsAtOneToken_AreOnePositionOfWeightTwo()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            fixture.AddTokenAsset(date, isSent: true, 10);
            fixture.AddTokenAsset(date, isSent: true, 10);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.PooledPopulation.TokenPositionsPerGroup.One);
            Assert.Equal(1, census.PooledPopulation.OccurrencesPerGroup.Two);
            Assert.Equal(1, census.Collisions.TokenPositionsWithSeveralCompatibleAssets);
            Assert.Equal(1, census.Collisions.GroupsContainingSuchAPosition);
            Assert.Equal(1, census.Collisions.MessagesWhoseRangeIncludesSuchAPosition);

            // Forced onto one position, but that position holds two assets, so no uniqueness follows.
            Assert.Equal(1, census.ForcedPositions.MessagesWhereTokenHoldsSeveralAssets);
            Assert.Equal(0, census.ForcedPositions.MessagesWhereTokenHoldsOneAsset);
            Assert.Equal(0, census.PooledNarrowing.UniqueCandidateUnderSequenceOrderHypothesis);
        }

        // ---------------------------------------------------------------------------------------
        // Assignment-count arithmetic.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Unit weights everywhere, so the exact weighted count is C(5, 3) = 10 and the two figures
        /// agree. That agreement is what isolates the contribution of multi-asset positions elsewhere.
        /// </remarks>
        [Fact]
        public void WithOneAssetPerPosition_TheWeightedCountEqualsTheUnweightedChoiceCount()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");
            fixture.AddOutgoing(date, "09:02:00");

            for (var token = 10; token <= 50; token += 10)
            {
                fixture.AddTokenAsset(date, isSent: true, token);
            }

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.AssignmentCounts.GroupsWhereWeightedEqualsUnweighted);
            Assert.Equal(0, census.AssignmentCounts.GroupsWhereWeightedExceedsUnweighted);
            Assert.Equal(0, census.AssignmentCounts.TokenPositionsWithSeveralAssets);

            // C(5, 3) = 10 falls in the 2-10 band and has two decimal digits.
            Assert.Equal(1, census.AssignmentCounts.AssignmentCounts.TwoToTen);
            Assert.Equal(2, census.AssignmentCounts.MaximumDecimalDigitCount);
        }

        /// <remarks>
        /// Two positions, two messages, and the second position holding two assets. There is one
        /// increasing position choice, so <c>C(2, 2) = 1</c>, while the weighted count is
        /// <c>1 * 2 = 2</c>: the heavy position carries two asset options. The gap between the two
        /// figures is the entire contribution of multi-asset positions.
        /// </remarks>
        [Fact]
        public void AMultiAssetPosition_MakesTheWeightedCountExceedTheChoiceCount()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");

            fixture.AddTokenAsset(date, isSent: true, 10);
            fixture.AddTokenAsset(date, isSent: true, 20);
            fixture.AddTokenAsset(date, isSent: true, 20);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(0, census.AssignmentCounts.GroupsWhereWeightedEqualsUnweighted);
            Assert.Equal(1, census.AssignmentCounts.GroupsWhereWeightedExceedsUnweighted);
            Assert.Equal(1, census.AssignmentCounts.TokenPositionsWithSeveralAssets);
            Assert.Equal(1, census.AssignmentCounts.GroupsWithASeveralAssetPosition);

            // Two positions, two messages: one position choice, weights 1 and 2, so two assignments.
            Assert.Equal(1, census.AssignmentCounts.AssignmentCounts.TwoToTen);
        }

        [Fact]
        public void AnImpossibleGroupContributesNoAssignmentCount()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");
            fixture.AddTokenAsset(date, isSent: true, 10);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(0, census.AssignmentCounts.FeasibleGroupCount);
            Assert.Equal(0, census.AssignmentCounts.AssignmentCounts.Total);
            Assert.Equal(0, census.AssignmentCounts.MaximumDecimalDigitCount);
        }

        // ---------------------------------------------------------------------------------------
        // Groups, directions and the frozen reconciliation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void OutgoingAndIncomingAreSeparateGroupsOnOneDate()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddIncoming(date, "09:01:00");

            fixture.AddTokenAsset(date, isSent: true, 10);
            fixture.AddTokenAsset(date, isSent: false, 20);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.OutgoingPopulation.GroupCount);
            Assert.Equal(1, census.IncomingPopulation.GroupCount);
            Assert.Equal(2, census.PooledPopulation.GroupCount);
            Assert.Equal(2, census.PooledPopulation.MessageCount);

            // Each direction sees exactly its own compatible asset.
            Assert.Equal(1, census.OutgoingPopulation.BaselineCompatibleRelationCount);
            Assert.Equal(1, census.IncomingPopulation.BaselineCompatibleRelationCount);
        }

        /// <remarks>
        /// The strongest structural gate available: because every message in a group shares one
        /// compatible candidate set, the sum of <c>M * A</c> over groups must equal the frozen
        /// analysis's compatible relation total.
        /// </remarks>
        [Fact]
        public void SumOfMessagesTimesAssets_EqualsTheFrozenCompatibleRelationTotal()
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
            fixture.AddTokenAsset(second, isSent: true, 60);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(
                census.MatchingCensus.ExactCandidateRelationsCompatible,
                census.PooledPopulation.BaselineCompatibleRelationCount);

            Assert.Equal(
                census.PooledPopulation.BaselineCompatibleRelationCount,
                census.PooledPopulation.BaselineRelationsInFeasibleGroups
                    + census.PooledPopulation.BaselineRelationsInImpossibleGroups);

            Assert.Equal(4, census.PooledPopulation.MessageCount);
            Assert.Equal(3, census.PooledPopulation.GroupCount);
        }

        /// <remarks>
        /// The preserved first pass holds exactly one attachment whose only evidence is a copy dated a
        /// day either side. It is reported and never moved onto that date.
        /// </remarks>
        [Fact]
        public void AttachmentWithNoExactDateCandidate_IsExcludedAndReported()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.Day(1);

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date.AddDays(-1), isSent: true, 10);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(1, census.ExcludedAdjacentDateOnlyAttachmentCount);
            Assert.Equal(0, census.PooledPopulation.GroupCount);
            Assert.Equal(0, census.PooledPopulation.MessageCount);
        }

        // ---------------------------------------------------------------------------------------
        // Sensitivity decomposition.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// One asset's token sits on a direction-agreeing copy, the other's only on an
        /// unknown-direction copy. The availability effect removes the second asset and its position;
        /// the sequence-order effect is then measured against what remains; the combined figure is the
        /// two together and is labelled as such.
        /// </remarks>
        [Fact]
        public void SensitivitySeparatesAvailabilityFromSequenceOrder()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            fixture.AddTokenAsset(date, isSent: true, 10);

            // Compatible through an agreeing copy whose suffix is unsupported; its only token is on an
            // unknown-direction copy, so the sensitivity view drops it entirely.
            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);
            fixture.AddCopy(assetID, sha, date, isSent: true, "0020-copy");
            fixture.AddCopy(assetID, sha, date, isSent: null, SequenceTestFixture.Token(20));

            var census = fixture.AnalyseAssignments();

            // Primary keeps both assets and both positions.
            Assert.Equal(2, census.PooledPopulation.BaselineCompatibleRelationCount);
            Assert.Equal(1, census.PooledPopulation.TokenPositionsPerGroup.Two);

            // Effect 1: availability.
            Assert.Equal(2, census.Sensitivity.FrozenCandidateRelations);
            Assert.Equal(1, census.Sensitivity.AgreeingTokenEligibleCandidateRelations);
            Assert.Equal(1, census.Sensitivity.TokenPositionsRemovedByFiltering);
            Assert.Equal(1, census.Sensitivity.AssetsLosingEveryTokenPosition);

            // Effect 2: order, measured against the agreeing-eligible baseline of one.
            Assert.Equal(1, census.Sensitivity.Feasibility.EnoughTokenPositionGroups);
            Assert.Equal(1, census.Sensitivity.SequenceOrderEffect.BaselineCandidateRelations);
            Assert.Equal(
                1, census.Sensitivity.SequenceOrderEffect.SequenceCompatibleCandidateRelations);

            // Effect 3: combined, frozen baseline against the final sensitivity set.
            Assert.Equal(2, census.Sensitivity.CombinedFrozenBaselineRelations);
            Assert.Equal(1, census.Sensitivity.CombinedFinalCandidateRelations);
        }

        [Fact]
        public void SensitivityCanOnlyMakeFeasibilityWorse()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");

            fixture.AddTokenAsset(date, isSent: true, 10);

            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);
            fixture.AddCopy(assetID, sha, date, isSent: true, "0020-copy");
            fixture.AddCopy(assetID, sha, date, isSent: null, SequenceTestFixture.Token(20));

            var census = fixture.AnalyseAssignments();

            // Two positions carry the two messages in the primary view; filtering leaves one.
            Assert.Equal(1, census.Feasibility.EnoughTokenPositionGroups);
            Assert.Equal(0, census.Sensitivity.Feasibility.EnoughTokenPositionGroups);
            Assert.Equal(1, census.Sensitivity.Feasibility.TooFewTokenPositionGroups);
            Assert.Equal(0, census.Sensitivity.CombinedFinalCandidateRelations);
        }

        [Fact]
        public void TokensOnAgreeingCopies_SurviveBothViewsUnchanged()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date, "09:00:00");
            fixture.AddOutgoing(date, "09:01:00");

            fixture.AddTokenAsset(date, isSent: true, 10);
            fixture.AddTokenAsset(date, isSent: true, 20);

            var census = fixture.AnalyseAssignments();

            Assert.Equal(0, census.Sensitivity.TokenPositionsRemovedByFiltering);
            Assert.Equal(0, census.Sensitivity.AssetsLosingEveryTokenPosition);
            Assert.Equal(
                census.Sensitivity.FrozenCandidateRelations,
                census.Sensitivity.AgreeingTokenEligibleCandidateRelations);
            Assert.Equal(
                census.PooledNarrowing.SequenceCompatibleCandidateRelations,
                census.Sensitivity.SequenceOrderEffect.SequenceCompatibleCandidateRelations);
        }
    }
}
