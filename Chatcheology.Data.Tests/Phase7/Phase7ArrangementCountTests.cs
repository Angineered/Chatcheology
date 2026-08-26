using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// Pins the arrangement-count classification against brute-force enumeration of the thing it is
    /// defined in terms of.
    /// </summary>
    /// <remarks>
    /// This class exists because an earlier draft of Stage 7.1 asserted that "exactly one admissible
    /// arrangement contains rank <c>r</c> at position <c>p</c>" is equivalent to <c>T = M</c>, which is
    /// <strong>false</strong>. The counterexample is <c>M = 2, T = 4, r = 0, p = 2</c>: one arrangement,
    /// <c>{2, 3}</c>, with <c>T &gt; M</c>. The exhaustive test below is what makes that error
    /// impossible to reintroduce.
    /// <para>
    /// The enumeration is written independently of both production formulations — it materialises every
    /// increasing selection and counts the ones that place the rank at the position — so agreement is
    /// evidence rather than restatement. It is bounded to <c>T &lt;= 9</c> and lives only in the test
    /// assembly; production never enumerates an arrangement and never grows as
    /// <c>C(T, M)</c>.
    /// </para>
    /// <para>
    /// Written as one <c>[Fact]</c> looping every shape rather than a <c>[Theory]</c> over every
    /// <c>(T, M, r, p)</c>, which would emit of the order of 1,700 individual cases and distort both
    /// the suite's runtime reporting and the count of new tests this stage added.
    /// </para>
    /// </remarks>
    public class Phase7ArrangementCountTests
    {
        private const int MaximumPositionCount = 9;

        [Fact]
        public void ArrangementCount_MatchesBruteForceEnumeration_ForEveryShapeUpToNine()
        {
            var shapes = 0;

            for (var positionCount = 1; positionCount <= MaximumPositionCount; positionCount++)
            {
                for (var memberCount = 1; memberCount <= positionCount; memberCount++)
                {
                    for (var rank = 0; rank < memberCount; rank++)
                    {
                        for (var position = 0; position < positionCount; position++)
                        {
                            shapes++;

                            var enumerated =
                                CountByEnumeration(rank, position, memberCount, positionCount);

                            var expected = enumerated switch
                            {
                                0 => Phase7ArrangementClass.None,
                                1 => Phase7ArrangementClass.ExactlyOne,
                                _ => Phase7ArrangementClass.Several,
                            };

                            Assert.Equal(
                                expected,
                                Phase7ArrangementCount.Classify(
                                    rank, position, memberCount, positionCount));

                            Assert.Equal(
                                enumerated > 0,
                                Phase7ArrangementCount.IsAdmissible(
                                    rank, position, memberCount, positionCount));
                        }
                    }
                }
            }

            // Guards the loop itself: a bounds mistake that silently exercised nothing would
            // otherwise pass.
            Assert.Equal(1155, shapes);
        }

        /// <remarks>
        /// The definitional form — the binomial product itself — compared against the O(1) predicate
        /// production uses, so the cheap form is proved to agree with the count rather than assumed to.
        /// </remarks>
        [Fact]
        public void ArrangementCount_SaturatingExactCount_AgreesWithThePredicate()
        {
            for (var positionCount = 1; positionCount <= MaximumPositionCount; positionCount++)
            {
                for (var memberCount = 1; memberCount <= positionCount; memberCount++)
                {
                    for (var rank = 0; rank < memberCount; rank++)
                    {
                        for (var position = 0; position < positionCount; position++)
                        {
                            var saturating = Phase7ArrangementCount.SaturatingCount(
                                rank, position, memberCount, positionCount);

                            var expected = saturating switch
                            {
                                0 => Phase7ArrangementClass.None,
                                1 => Phase7ArrangementClass.ExactlyOne,
                                _ => Phase7ArrangementClass.Several,
                            };

                            Assert.Equal(
                                expected,
                                Phase7ArrangementCount.Classify(
                                    rank, position, memberCount, positionCount));
                        }
                    }
                }
            }
        }

        /// <remarks>
        /// Named for the finding it encodes. The retracted claim would have made this
        /// <c>CompatibleWithAmbiguity</c>, because <c>T &gt; M</c>.
        /// </remarks>
        [Fact]
        public void ArrangementCount_TheReviewCounterexample_IsExactlyOne()
        {
            Assert.Equal(
                Phase7ArrangementClass.ExactlyOne,
                Phase7ArrangementCount.Classify(rank: 0, position: 2, memberCount: 2, positionCount: 4));

            var arrangements = Enumerate(memberCount: 2, positionCount: 4)
                .Where(arrangement => arrangement[0] == 2)
                .ToList();

            var only = Assert.Single(arrangements);

            Assert.Equal([2, 3], only);
        }

        [Fact]
        public void ArrangementCount_ForcedGroup_IsExactlyOneAtEveryRankOwnPosition()
        {
            for (var memberCount = 1; memberCount <= 5; memberCount++)
            {
                for (var rank = 0; rank < memberCount; rank++)
                {
                    Assert.Equal(
                        Phase7ArrangementClass.ExactlyOne,
                        Phase7ArrangementCount.Classify(rank, rank, memberCount, memberCount));

                    for (var position = 0; position < memberCount; position++)
                    {
                        if (position == rank)
                        {
                            continue;
                        }

                        Assert.Equal(
                            Phase7ArrangementClass.None,
                            Phase7ArrangementCount.Classify(
                                rank, position, memberCount, memberCount));
                    }
                }
            }
        }

        /// <remarks>
        /// The two edge shapes, and the direction is easy to get backwards: uniqueness comes from the
        /// <em>first</em> rank at its <em>highest</em> admissible position and the <em>last</em> rank at
        /// its <em>lowest</em>. The opposite edges are the most ambiguous positions, not the least.
        /// </remarks>
        [Fact]
        public void ArrangementCount_EdgeAndInteriorShapes_ClassifyAsStated()
        {
            var unique = Phase7ArrangementClass.ExactlyOne;
            var several = Phase7ArrangementClass.Several;

            (int Rank, int Position, int MemberCount, int PositionCount,
             Phase7ArrangementClass Expected)[] cases =
            [
                // The first rank at its highest admissible position: unique.
                (0, 2, 2, 4, unique),

                // The first rank at its lowest: the most ambiguous position it has.
                (0, 0, 2, 4, several),

                // The last rank at its lowest admissible position: unique.
                (1, 1, 2, 4, unique),

                // The last rank at its highest: again the most ambiguous.
                (1, 3, 2, 4, several),

                (0, 1, 2, 4, several),
                (1, 2, 2, 4, several),
                (1, 2, 3, 5, several),
            ];

            foreach (var (rank, position, memberCount, positionCount, expected) in cases)
            {
                Assert.Equal(
                    expected,
                    Phase7ArrangementCount.Classify(rank, position, memberCount, positionCount));
            }
        }

        [Theory]
        [InlineData(0, 3, 2, 4)]
        [InlineData(1, 0, 2, 4)]
        [InlineData(2, 1, 3, 4)]
        public void ArrangementCount_OutsideTheWindow_IsNone(
            int rank, int position, int memberCount, int positionCount)
        {
            Assert.Equal(
                Phase7ArrangementClass.None,
                Phase7ArrangementCount.Classify(rank, position, memberCount, positionCount));

            Assert.False(
                Phase7ArrangementCount.IsAdmissible(rank, position, memberCount, positionCount));
        }

        /// <remarks>
        /// A single-member group makes every admissible position uniquely constrained, which is exactly
        /// why uniqueness earns no ranking bonus: it is routinely forced by shape rather than earned.
        /// </remarks>
        [Fact]
        public void ArrangementCount_SingleMemberGroup_IsExactlyOneAtEveryPosition()
        {
            for (var positionCount = 1; positionCount <= 6; positionCount++)
            {
                for (var position = 0; position < positionCount; position++)
                {
                    Assert.Equal(
                        Phase7ArrangementClass.ExactlyOne,
                        Phase7ArrangementCount.Classify(
                            rank: 0, position, memberCount: 1, positionCount));
                }
            }
        }

        [Fact]
        public void ArrangementCount_ImpossibleShapes_AreNoneRatherThanThrowing()
        {
            // Fewer positions than members: no arrangement exists for anyone.
            Assert.Equal(
                Phase7ArrangementClass.None,
                Phase7ArrangementCount.Classify(
                    rank: 0, position: 0, memberCount: 3, positionCount: 2));

            // No positions at all.
            Assert.Equal(
                Phase7ArrangementClass.None,
                Phase7ArrangementCount.Classify(
                    rank: 0, position: 0, memberCount: 1, positionCount: 0));

            // A rank outside the group.
            Assert.Equal(
                Phase7ArrangementClass.None,
                Phase7ArrangementCount.Classify(
                    rank: 3, position: 0, memberCount: 2, positionCount: 4));
        }

        // ---------------------------------------------------------------------------------------
        // The independent enumeration.
        // ---------------------------------------------------------------------------------------

        private static int CountByEnumeration(
            int rank, int position, int memberCount, int positionCount) =>
            Enumerate(memberCount, positionCount)
                .Count(arrangement => arrangement[rank] == position);

        /// <summary>
        /// Every strictly increasing selection of <paramref name="memberCount"/> positions from
        /// <paramref name="positionCount"/>, materialised.
        /// </summary>
        /// <remarks>
        /// Recursive rather than combinatorial on purpose: a formulation deliberately unlike either
        /// production form, so agreeing with it means something.
        /// </remarks>
        private static List<int[]> Enumerate(int memberCount, int positionCount)
        {
            var arrangements = new List<int[]>();
            var current = new int[memberCount];

            void Extend(int index, int from)
            {
                if (index == memberCount)
                {
                    arrangements.Add([.. current]);

                    return;
                }

                for (var position = from; position < positionCount; position++)
                {
                    current[index] = position;

                    Extend(index + 1, position + 1);
                }
            }

            Extend(0, 0);

            return arrangements;
        }
    }
}
