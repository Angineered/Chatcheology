using Chatcheology.Data.Matching;
using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The reduction from stored facts to the two support dimensions: occurrence-coherent, with no
    /// cherry-picking, and with <c>ContradictoryOnly</c> unavailable rather than negative.
    /// </summary>
    public class Phase7SupportReductionTests
    {
        // ---------------------------------------------------------------------------------------
        // Direction.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Named for what the frozen rule can actually deliver. Direction is available exactly for
        /// <c>Compatible</c>, and — because a dimension enters the comparison only when it is available
        /// for both candidates, and when it is both are <c>Supportive</c> — it can never be the
        /// dimension on which one candidate is strictly better.
        /// </remarks>
        [Fact]
        public void Ranking_DirectionCompatibility_IsAvailableOnlyForCompatibleAndNeverDiscriminates()
        {
            Assert.Equal(
                Phase7DirectionSupport.Supportive,
                Phase7SupportReader.ReduceDirection(DirectionCompatibility.Compatible));

            foreach (var unavailable in new[]
            {
                DirectionCompatibility.Mixed,
                DirectionCompatibility.Unknown,
                DirectionCompatibility.ContradictoryOnly,
            })
            {
                Assert.Equal(
                    Phase7DirectionSupport.Unavailable,
                    Phase7SupportReader.ReduceDirection(unavailable));
            }

            // Two candidates whose only available dimension is Direction are incomparable, never
            // ordered.
            var left = Support(1, Phase7DirectionSupport.Supportive, Phase7OrderSupport.Unavailable);
            var right = Support(2, Phase7DirectionSupport.Supportive, Phase7OrderSupport.Unavailable);

            Assert.Equal(
                Phase7SupportComparison.Incomparable, Phase7SupportRanking.Compare(left, right));

            Assert.Equal(
                Phase7SupportComparison.Incomparable, Phase7SupportRanking.Compare(right, left));
        }

        [Fact]
        public void Reduction_DirectionDimension_OnlyEverEmitsUnavailableOrSupportive()
        {
            foreach (var compatibility in Enum.GetValues<DirectionCompatibility>())
            {
                var reduced = Phase7SupportReader.ReduceDirection(compatibility);

                Assert.True(
                    reduced is Phase7DirectionSupport.Unavailable
                        or Phase7DirectionSupport.Supportive);
            }

            Assert.Equal(
                ["Unavailable", "Supportive"], Enum.GetNames<Phase7DirectionSupport>());
        }

        /// <remarks>
        /// A <c>ContradictoryOnly</c> candidate is persisted like any other, stays a candidate, and is
        /// incomparable rather than demoted. The closure attributes those relations to folder structure
        /// rather than to evidence and forbids acting on one as an exclusion.
        /// </remarks>
        [Fact]
        public void Ranking_ContradictoryOnlyCandidate_IsNeverRemovedAndNeverRankedLast()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            var contradictory = fixture.AddTokenAsset(date, isSent: false, 1);
            var compatible = fixture.AddTokenAsset(date, isSent: true, 2);

            var result = fixture.Run();

            Assert.Equal(2, result.CandidateRowCount);

            var support = fixture.Support(result.RunID, attachment);

            Assert.Equal(2, support.Count);

            var contradictorySupport =
                Assert.Single(support, entry => entry.MediaAssetID == contradictory);

            var compatibleSupport =
                Assert.Single(support, entry => entry.MediaAssetID == compatible);

            Assert.Equal(Phase7DirectionSupport.Unavailable, contradictorySupport.Direction);
            Assert.Equal(Phase7OrderSupport.Unavailable, contradictorySupport.OrderSupport);

            // No available dimension in common, so incomparable — not last.
            Assert.Equal(
                Phase7SupportComparison.Incomparable,
                Phase7SupportRanking.Compare(compatibleSupport, contradictorySupport));

            var ranked = fixture.Rank(result.RunID, attachment);

            Assert.Equal(2, Assert.Single(ranked.Tiers).Count);
        }

        [Fact]
        public void Ranking_DirectionNeverProducesAStrictPreference()
        {
            foreach (var leftOrder in Enum.GetValues<Phase7OrderSupport>())
            {
                foreach (var rightOrder in Enum.GetValues<Phase7OrderSupport>())
                {
                    var left = Support(1, Phase7DirectionSupport.Supportive, leftOrder);
                    var right = Support(2, Phase7DirectionSupport.Unavailable, rightOrder);

                    var comparison = Phase7SupportRanking.Compare(left, right);

                    // Whatever the answer, it cannot have come from Direction: the right candidate's
                    // Direction is unavailable, so the dimension is not in the comparison at all.
                    if (comparison == Phase7SupportComparison.LeftPreferred)
                    {
                        Assert.NotEqual(rightOrder, leftOrder);
                        Assert.NotEqual(Phase7OrderSupport.Unavailable, leftOrder);
                        Assert.NotEqual(Phase7OrderSupport.Unavailable, rightOrder);
                    }
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Order support: unanimous or unavailable.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Reduction_NoOccurrence_IsUnavailable() =>
            Assert.Equal(Phase7OrderSupport.Unavailable, Phase7SupportReader.ReduceOrder([]));

        [Fact]
        public void Reduction_AmbiguousAndUniquelyConstrained_ReduceToTheSameValue()
        {
            Assert.Equal(
                Phase7OrderSupport.Supportive,
                Phase7SupportReader.ReduceOrder(
                    [OrderConsistencyAnnotation.CompatibleWithAmbiguity]));

            Assert.Equal(
                Phase7OrderSupport.Supportive,
                Phase7SupportReader.ReduceOrder(
                    [OrderConsistencyAnnotation.UniquelyConstrained]));

            // Unanimous across the two, because they are the same reduced value.
            Assert.Equal(
                Phase7OrderSupport.Supportive,
                Phase7SupportReader.ReduceOrder(
                    [
                        OrderConsistencyAnnotation.CompatibleWithAmbiguity,
                        OrderConsistencyAnnotation.UniquelyConstrained,
                    ]));
        }

        [Fact]
        public void Reduction_NoCompatibleArrangement_IsLessSupportive() =>
            Assert.Equal(
                Phase7OrderSupport.LessSupportive,
                Phase7SupportReader.ReduceOrder(
                    [OrderConsistencyAnnotation.NoCompatibleArrangement]));

        /// <remarks>
        /// Unanimity is over the surviving values only: one supportive occurrence beside three
        /// unassessable ones is supportive, because the unassessable ones were dropped rather than
        /// counted.
        /// </remarks>
        [Fact]
        public void Ranking_UnavailableOccurrences_AreDroppedNotCounted()
        {
            Assert.Equal(
                Phase7OrderSupport.Supportive,
                Phase7SupportReader.ReduceOrder(
                    [
                        OrderConsistencyAnnotation.NotAssessable,
                        OrderConsistencyAnnotation.UniquelyConstrained,
                        OrderConsistencyAnnotation.NotAssessable,
                        OrderConsistencyAnnotation.NotAssessable,
                    ]));

            Assert.Equal(
                Phase7OrderSupport.LessSupportive,
                Phase7SupportReader.ReduceOrder(
                    [
                        OrderConsistencyAnnotation.NotAssessable,
                        OrderConsistencyAnnotation.NoCompatibleArrangement,
                    ]));

            Assert.Equal(
                Phase7OrderSupport.Unavailable,
                Phase7SupportReader.ReduceOrder(
                    [
                        OrderConsistencyAnnotation.NotAssessable,
                        OrderConsistencyAnnotation.NotAssessable,
                    ]));
        }

        [Fact]
        public void Ranking_MixedSupportiveAndLessSupportiveOccurrences_MakeOrderSupportUnavailable()
        {
            Assert.Equal(
                Phase7OrderSupport.Unavailable,
                Phase7SupportReader.ReduceOrder(
                    [
                        OrderConsistencyAnnotation.UniquelyConstrained,
                        OrderConsistencyAnnotation.NoCompatibleArrangement,
                    ]));

            // Order of arrival makes no difference: the rule is unanimity, not first-wins.
            Assert.Equal(
                Phase7OrderSupport.Unavailable,
                Phase7SupportReader.ReduceOrder(
                    [
                        OrderConsistencyAnnotation.NoCompatibleArrangement,
                        OrderConsistencyAnnotation.CompatibleWithAmbiguity,
                    ]));
        }

        /// <remarks>
        /// End to end, and this is the shape that matters: one candidate whose two physical copies land
        /// on positions that read differently for the same attachment. The disagreement stays fully
        /// visible in the stored rows and is never resolved in the favourable direction.
        /// </remarks>
        [Fact]
        public void Run_CandidateWhoseOccurrencesDisagree_HasUnavailableOrderSupport()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, disagreeing) = BuildDisagreementFixture(fixture, date);

            var result = fixture.Run();

            // Rank 1's window is positions 1..3, and this candidate sits at 0 and at 3.
            var occurrences = fixture.Occurrences(result.RunID, attachments[1])
                .Where(row => row.StartsWith(
                    disagreeing.ToString() + ",", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(2, occurrences.Count);

            Assert.Contains(
                occurrences, row => row.EndsWith("NoCompatibleArrangement", StringComparison.Ordinal));

            Assert.Contains(
                occurrences, row => row.EndsWith("CompatibleWithAmbiguity", StringComparison.Ordinal));

            var support = Assert.Single(
                fixture.Support(result.RunID, attachments[1]),
                entry => entry.MediaAssetID == disagreeing);

            Assert.Equal(Phase7OrderSupport.Unavailable, support.OrderSupport);
        }

        [Fact]
        public void Ranking_DisagreeingCandidate_IsIncomparableWithAUnanimousSupportiveCandidate()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, disagreeing) = BuildDisagreementFixture(fixture, date);

            var result = fixture.Run();

            var support = fixture.Support(result.RunID, attachments[1]);

            var disagreeingSupport =
                Assert.Single(support, entry => entry.MediaAssetID == disagreeing);

            var unanimous = support.First(
                entry => entry.MediaAssetID != disagreeing
                    && entry.OrderSupport == Phase7OrderSupport.Supportive);

            Assert.Equal(
                Phase7SupportComparison.Incomparable,
                Phase7SupportRanking.Compare(disagreeingSupport, unanimous));

            Assert.Equal(
                Phase7SupportComparison.Incomparable,
                Phase7SupportRanking.Compare(unanimous, disagreeingSupport));
        }

        /// <remarks>
        /// A strict preference, and the only kind Phase 7 can express: an order-inconsistent candidate
        /// against an order-compatible one, direction equal. It is a preference, not an exclusion — the
        /// weaker candidate is still present, in a later tier.
        /// </remarks>
        [Fact]
        public void Ranking_IncompatibleOrder_IsLessSupportiveThanCompatibleOrder()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            var result = fixture.Run();

            var support = fixture.Support(result.RunID, attachments[1]);

            var outside = Assert.Single(support, entry => entry.MediaAssetID == assets[0]);
            var inside = Assert.Single(support, entry => entry.MediaAssetID == assets[1]);

            Assert.Equal(Phase7OrderSupport.LessSupportive, outside.OrderSupport);
            Assert.Equal(Phase7OrderSupport.Supportive, inside.OrderSupport);

            Assert.Equal(
                Phase7SupportComparison.LeftPreferred,
                Phase7SupportRanking.Compare(inside, outside));

            var ranked = fixture.Rank(result.RunID, attachments[1]);

            Assert.Equal(2, ranked.Tiers.Count);
            Assert.Equal(3, ranked.Tiers[0].Count);
            Assert.Equal(assets[0], Assert.Single(ranked.Tiers[1]).MediaAssetID);
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Two outgoing attachments and four token positions, where one candidate asset carries copies
        /// at positions 0 and 3 — which read differently for rank 1.
        /// </summary>
        internal static (long[] Attachments, long Disagreeing) BuildDisagreementFixture(
            Phase7TestFixture fixture, DateOnly date)
        {
            var attachments = new[]
            {
                fixture.AddOutgoing(date, time: "09:00:00"),
                fixture.AddOutgoing(date, time: "09:01:00"),
            };

            var disagreeing = fixture.AddTokenAsset(date, isSent: true, 0, 3);

            fixture.AddTokenAsset(date, isSent: true, 1);
            fixture.AddTokenAsset(date, isSent: true, 2);

            return (attachments, disagreeing);
        }

        private static Phase7CandidateSupport Support(
            long mediaAssetID, Phase7DirectionSupport direction, Phase7OrderSupport order) =>
            new()
            {
                MediaAssetID = mediaAssetID,
                Direction = direction,
                OrderSupport = order,
            };
    }
}
