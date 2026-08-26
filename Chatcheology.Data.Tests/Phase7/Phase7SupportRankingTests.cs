using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The partial order itself, exercised at the generic profile level as well as through the public
    /// read model.
    /// </summary>
    /// <remarks>
    /// The public types cannot express a direction/order conflict — <see cref="Phase7DirectionSupport"/>
    /// has no <c>LessSupportive</c> member — which is correct for evidence but would leave the generic
    /// Pareto algorithm partly untested. So the conflicting-dimension and no-common-dimension branches
    /// are reached through the internal profile, and the public mapping onto it is checked exhaustively.
    /// </remarks>
    public class Phase7SupportRankingTests
    {
        // ---------------------------------------------------------------------------------------
        // The generic comparator.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Compare_ConflictingDimensions_IsIncomparable()
        {
            // Each wins one dimension. This state is unreachable through the public read model and is
            // exactly why the comparator is written over the internal profile.
            var left = new Phase7SupportProfile(
                Phase7SupportLevel.Supportive, Phase7SupportLevel.LessSupportive);

            var right = new Phase7SupportProfile(
                Phase7SupportLevel.LessSupportive, Phase7SupportLevel.Supportive);

            Assert.Equal(
                Phase7SupportComparison.Incomparable, Phase7SupportRanking.Compare(left, right));

            Assert.Equal(
                Phase7SupportComparison.Incomparable, Phase7SupportRanking.Compare(right, left));
        }

        [Fact]
        public void Compare_NoCommonAvailableDimension_IsIncomparable()
        {
            var left = new Phase7SupportProfile(
                Phase7SupportLevel.Supportive, Phase7SupportLevel.Unavailable);

            var right = new Phase7SupportProfile(
                Phase7SupportLevel.Unavailable, Phase7SupportLevel.Supportive);

            Assert.Equal(
                Phase7SupportComparison.Incomparable, Phase7SupportRanking.Compare(left, right));

            Assert.Equal(
                Phase7SupportComparison.Incomparable, Phase7SupportRanking.Compare(right, left));
        }

        /// <remarks>
        /// The property that keeps "unavailable is not negative" honest: a candidate whose order
        /// dimension is unavailable is <em>not</em> worse than one whose order dimension is available
        /// and unfavourable. It is incomparable with it.
        /// </remarks>
        [Fact]
        public void Compare_UnavailableDimension_NeverDemotes()
        {
            var unavailable = new Phase7SupportProfile(
                Phase7SupportLevel.Supportive, Phase7SupportLevel.Unavailable);

            var unfavourable = new Phase7SupportProfile(
                Phase7SupportLevel.Supportive, Phase7SupportLevel.LessSupportive);

            Assert.Equal(
                Phase7SupportComparison.Incomparable,
                Phase7SupportRanking.Compare(unavailable, unfavourable));

            Assert.Equal(
                Phase7SupportComparison.Incomparable,
                Phase7SupportRanking.Compare(unfavourable, unavailable));
        }

        [Fact]
        public void Compare_IsAntisymmetricAndIrreflexive()
        {
            var levels = Enum.GetValues<Phase7SupportLevel>();

            foreach (var leftDirection in levels)
            {
                foreach (var leftOrder in levels)
                {
                    var left = new Phase7SupportProfile(leftDirection, leftOrder);

                    Assert.Equal(
                        Phase7SupportComparison.Incomparable,
                        Phase7SupportRanking.Compare(left, left));

                    foreach (var rightDirection in levels)
                    {
                        foreach (var rightOrder in levels)
                        {
                            var right = new Phase7SupportProfile(rightDirection, rightOrder);

                            var forward = Phase7SupportRanking.Compare(left, right);
                            var backward = Phase7SupportRanking.Compare(right, left);

                            Assert.Equal(
                                forward switch
                                {
                                    Phase7SupportComparison.LeftPreferred =>
                                        Phase7SupportComparison.RightPreferred,
                                    Phase7SupportComparison.RightPreferred =>
                                        Phase7SupportComparison.LeftPreferred,
                                    _ => Phase7SupportComparison.Incomparable,
                                },
                                backward);
                        }
                    }
                }
            }
        }

        [Fact]
        public void PublicSupport_MapsOntoTheInternalProfileFaithfully()
        {
            var mapped = 0;

            foreach (var direction in Enum.GetValues<Phase7DirectionSupport>())
            {
                foreach (var order in Enum.GetValues<Phase7OrderSupport>())
                {
                    mapped++;

                    var profile = Phase7SupportProfile.Of(
                        new Phase7CandidateSupport
                        {
                            MediaAssetID = 1,
                            Direction = direction,
                            OrderSupport = order,
                        });

                    Assert.Equal(
                        direction == Phase7DirectionSupport.Supportive
                            ? Phase7SupportLevel.Supportive
                            : Phase7SupportLevel.Unavailable,
                        profile[Phase7SupportDimension.Direction]);

                    // The public read model can never produce a direction reading of LessSupportive,
                    // because the member does not exist.
                    Assert.NotEqual(
                        Phase7SupportLevel.LessSupportive,
                        profile[Phase7SupportDimension.Direction]);

                    Assert.Equal(
                        order switch
                        {
                            Phase7OrderSupport.Supportive => Phase7SupportLevel.Supportive,
                            Phase7OrderSupport.LessSupportive => Phase7SupportLevel.LessSupportive,
                            _ => Phase7SupportLevel.Unavailable,
                        },
                        profile[Phase7SupportDimension.OrderSupport]);
                }
            }

            Assert.Equal(6, mapped);
        }

        // ---------------------------------------------------------------------------------------
        // Tiers.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Ranking_AmbiguousAndUniquelyConstrained_HaveEqualStrength()
        {
            // Both annotations reduce to Supportive, so two candidates differing only in which one
            // their occurrence carries are incomparable and share a tier.
            var ambiguous = Support(2, Phase7OrderSupport.Supportive);
            var unique = Support(1, Phase7OrderSupport.Supportive);

            Assert.Equal(
                Phase7SupportComparison.Incomparable,
                Phase7SupportRanking.Compare(ambiguous, unique));

            var ranked = Phase7SupportRanking.BuildTiers([ambiguous, unique]);

            Assert.Equal(2, Assert.Single(ranked.Tiers).Count);
        }

        [Fact]
        public void Tiers_WithinATier_AreOrderedByMediaAssetIDWithNoEvidentialMeaning()
        {
            var candidates = new[]
            {
                Support(30, Phase7OrderSupport.Supportive),
                Support(10, Phase7OrderSupport.Supportive),
                Support(20, Phase7OrderSupport.Supportive),
            };

            var forward = Phase7SupportRanking.BuildTiers(candidates);
            var reversed = Phase7SupportRanking.BuildTiers(candidates.Reverse());

            Assert.Equal(
                [10L, 20L, 30L],
                Assert.Single(forward.Tiers).Select(candidate => candidate.MediaAssetID));

            Assert.Equal(
                [10L, 20L, 30L],
                Assert.Single(reversed.Tiers).Select(candidate => candidate.MediaAssetID));
        }

        [Fact]
        public void BuildTiers_IsStableUnderInputPermutation()
        {
            var candidates = new[]
            {
                Support(1, Phase7OrderSupport.Supportive),
                Support(2, Phase7OrderSupport.LessSupportive),
                Support(3, Phase7OrderSupport.Supportive),
                Support(4, Phase7OrderSupport.Unavailable),
            };

            static string Describe(Phase7RankedCandidateSet ranked) =>
                string.Join(
                    "|",
                    ranked.Tiers.Select(
                        tier => string.Join(
                            ",", tier.Select(candidate => candidate.MediaAssetID))));

            var expected = Describe(Phase7SupportRanking.BuildTiers(candidates));

            foreach (var permutation in Permutations(candidates))
            {
                Assert.Equal(expected, Describe(Phase7SupportRanking.BuildTiers(permutation)));
            }
        }

        /// <remarks>
        /// Three mutually incomparable candidates form one tier, not three. A tiered partial order is
        /// not a sorted list with the ties broken arbitrarily.
        /// </remarks>
        [Fact]
        public void Tiers_AreNotATotalOrder()
        {
            var candidates = new[]
            {
                Support(1, Phase7OrderSupport.Supportive),
                Support(2, Phase7OrderSupport.Supportive),
                Support(3, Phase7OrderSupport.Unavailable),
            };

            var ranked = Phase7SupportRanking.BuildTiers(candidates);

            Assert.Equal(3, Assert.Single(ranked.Tiers).Count);
            Assert.Equal(3, ranked.CandidateCount);
        }

        [Fact]
        public void Tiers_APreferredCandidate_LeavesTheWeakerOneInALaterTier()
        {
            var strong = Support(1, Phase7OrderSupport.Supportive);
            var weak = Support(2, Phase7OrderSupport.LessSupportive);

            var ranked = Phase7SupportRanking.BuildTiers([weak, strong]);

            Assert.Equal(2, ranked.Tiers.Count);
            Assert.Equal(1L, Assert.Single(ranked.Tiers[0]).MediaAssetID);
            Assert.Equal(2L, Assert.Single(ranked.Tiers[1]).MediaAssetID);
        }

        [Fact]
        public void BuildTiers_WithNoCandidates_IsAnEmptySet()
        {
            var ranked = Phase7SupportRanking.BuildTiers([]);

            Assert.Empty(ranked.Tiers);
            Assert.Equal(0, ranked.CandidateCount);
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static Phase7CandidateSupport Support(long mediaAssetID, Phase7OrderSupport order) =>
            new()
            {
                MediaAssetID = mediaAssetID,
                Direction = Phase7DirectionSupport.Supportive,
                OrderSupport = order,
            };

        private static IEnumerable<Phase7CandidateSupport[]> Permutations(
            Phase7CandidateSupport[] candidates)
        {
            if (candidates.Length <= 1)
            {
                yield return candidates;

                yield break;
            }

            for (var index = 0; index < candidates.Length; index++)
            {
                var head = candidates[index];

                var rest = candidates
                    .Where((_, position) => position != index)
                    .ToArray();

                foreach (var permutation in Permutations(rest))
                {
                    yield return [head, .. permutation];
                }
            }
        }
    }
}
