using Chatcheology.Data.Sequence;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests the co-primary adjacent-date controls: a message pattern measured against the token order of
    /// the calendar date beside it.
    /// </summary>
    /// <remarks>
    /// The fixture is built so both figures are readable by hand. The own date carries the pattern
    /// <c>OI</c> against a token side of <c>OOII</c>, which admits; each neighbour carries <c>IIOO</c>,
    /// which does not. Both token sides belong to the same reference class — two outgoing, two incoming,
    /// two runs — so the conditional expectation is one half on both sides of the comparison and only the
    /// observed outcome differs. That is the cleanest possible statement of what this control asks.
    /// </remarks>
    public class DirectionSequenceAlignmentAdjacentTests
    {
        private static readonly DateOnly Own = DirectionSequenceGateFixture.Day(1);

        // ---------------------------------------------------------------------------------------
        // The paired comparison.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void BothNeighboursAreReportedSeparately()
        {
            using var fixture = WithBothNeighbours();

            var census = fixture.AnalyseAlignment();

            Assert.Equal(2, census.BinaryAdjacentControls.Count);
            Assert.Equal(2, census.GradedAdjacentControls.Count);

            Assert.Equal(
                [DirectionSequenceAdjacentOffset.PreviousDay, DirectionSequenceAdjacentOffset.NextDay],
                census.BinaryAdjacentControls.Select(control => control.Offset));

            Assert.Equal(
                [DirectionSequenceAdjacentOffset.PreviousDay, DirectionSequenceAdjacentOffset.NextDay],
                census.GradedAdjacentControls.Select(control => control.Offset));
        }

        /// <remarks>
        /// The correct-date and shifted figures are computed over the same pairs, so their populations
        /// must be identical — and the difference between them is the whole point of the control.
        /// </remarks>
        [Fact]
        public void TheCorrectDateAndShiftedFiguresShareOnePopulation()
        {
            using var fixture = WithBothNeighbours();

            foreach (var control in fixture.AnalyseAlignment().BinaryAdjacentControls)
            {
                Assert.Equal(1, control.Accounting.IntersectionPairCount);
                Assert.Equal(1, control.CorrectDate.PairCount);
                Assert.Equal(1, control.ShiftedDate.PairCount);

                // The own date admits; the neighbour, being the same class in the other order, does not.
                Assert.Equal(1, control.CorrectDate.ObservedAdmissions);
                Assert.Equal(0, control.ShiftedDate.ObservedAdmissions);

                Assert.Equal(0.5d, control.CorrectDate.SumOfConditionalAdmissionProbability);
                Assert.Equal(0.5d, control.ShiftedDate.SumOfConditionalAdmissionProbability);

                Assert.Equal(0.5d, control.CorrectDate.ObservedMinusExpected);
                Assert.Equal(-0.5d, control.ShiftedDate.ObservedMinusExpected);
            }
        }

        /// <remarks>
        /// The correct-date figure here is restricted to the intersection and is a different population
        /// from the full-population primary result, which is why it is labelled distinctly rather than
        /// being reported as the same number.
        /// </remarks>
        [Fact]
        public void TheRestrictedCorrectDateFigureIsLabelledDistinctlyFromThePrimary()
        {
            using var fixture = WithBothNeighbours();

            var census = fixture.AnalyseAlignment();
            var control = census.BinaryAdjacentControls[0];

            Assert.Equal("BinaryInformative", census.Binary.PopulationName);
            Assert.Contains("paired intersection", control.CorrectDate.PopulationName);
            Assert.Contains("shifted intersection", control.ShiftedDate.PopulationName);
        }

        [Fact]
        public void TheGradedControlUsesItsOwnIntersection()
        {
            using var fixture = WithBothNeighbours();

            foreach (var control in fixture.AnalyseAlignment().GradedAdjacentControls)
            {
                Assert.Equal(1, control.Accounting.IntersectionPairCount);

                // Four embeddings of six choices on the own date, none on the neighbour.
                Assert.Equal(
                    0.666666666666d, control.CorrectDate.SumOfObservedNormalisedEmbeddingShare);

                Assert.Equal(0d, control.ShiftedDate.SumOfObservedNormalisedEmbeddingShare);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Literal calendar adjacency.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The immediately previous calendar date, never the nearest date that happens to hold tokens.
        /// Skipping a gap would turn a wrong-date control into a comparison with whatever activity was
        /// closest, which is a different question and a weaker one.
        /// </remarks>
        [Fact]
        public void AGapIsNotSkippedToReachAPopulatedDate()
        {
            using var fixture = new DirectionSequenceGateFixture();

            var own = DirectionSequenceGateFixture.Day(2);

            fixture.AddOutgoing(own, "09:00:00");
            fixture.AddIncoming(own, "09:01:00");
            AddOoii(fixture, own);

            // Two days earlier, with the day between deliberately empty.
            AddIioo(fixture, DirectionSequenceGateFixture.Day(0));

            var control = fixture.AnalyseAlignment().BinaryAdjacentControls
                .Single(
                    candidate => candidate.Offset
                        == DirectionSequenceAdjacentOffset.PreviousDay);

            Assert.Equal(0, control.Accounting.IntersectionPairCount);
            Assert.Equal(1, control.Accounting.DroppedNeighbourDateAbsent);
        }

        [Fact]
        public void ANeighbourWithNoTokenPopulationIsDropped()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(Own, "09:00:00");
            fixture.AddIncoming(Own, "09:01:00");
            AddOoii(fixture, Own);

            // The previous date carries a message but no token position at all.
            fixture.AddOutgoing(DirectionSequenceGateFixture.Day(0), "09:00:00");

            var control = Previous(fixture);

            Assert.Equal(0, control.Accounting.IntersectionPairCount);
            Assert.Equal(1, control.Accounting.DroppedNeighbourTokenPopulationEmpty);
        }

        // ---------------------------------------------------------------------------------------
        // The shifted comparison carries its own everything.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The check that the shifted reference is recomputed rather than inherited. The own date is
        /// informative with <c>q_r</c> one half; the neighbour's token side is an alternating order whose
        /// class admits the pattern from every arrangement, so the shifted comparison is determinate and
        /// contributes exactly zero to a difference. A run that had reused the correct date's
        /// classification would have kept the pair.
        /// </remarks>
        [Fact]
        public void AShiftedComparisonIsClassifiedOnItsOwnTokenSide()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(Own, "09:00:00");
            fixture.AddIncoming(Own, "09:01:00");
            AddOoii(fixture, Own);

            // OIOI on the previous date: the class holds OIOI and IOIO and both admit OI.
            var previous = DirectionSequenceGateFixture.Day(0);
            fixture.AddToken(previous, 1, isSent: true);
            fixture.AddToken(previous, 2, isSent: false);
            fixture.AddToken(previous, 3, isSent: true);
            fixture.AddToken(previous, 4, isSent: false);

            var control = Previous(fixture);

            Assert.Equal(1, control.Accounting.OwnDateEligiblePairCount);
            Assert.Equal(0, control.Accounting.IntersectionPairCount);
            Assert.Equal(1, control.Accounting.DroppedShiftedDeterminate);
        }

        /// <remarks>
        /// Supply is reassessed against the neighbour's composition, not the own date's. The own date
        /// supplies its pattern completely; the neighbour cannot supply the incoming symbol at all.
        /// </remarks>
        [Fact]
        public void AShiftedComparisonShortOfSupplyIsDropped()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(Own, "09:00:00");
            fixture.AddIncoming(Own, "09:01:00");
            AddOoii(fixture, Own);

            // Outgoing only on the previous date, so the incoming message symbol cannot be supplied.
            var previous = DirectionSequenceGateFixture.Day(0);
            fixture.AddToken(previous, 1, isSent: true);
            fixture.AddToken(previous, 2, isSent: true);

            var control = Previous(fixture);

            Assert.Equal(0, control.Accounting.IntersectionPairCount);
            Assert.Equal(1, control.Accounting.DroppedShiftedSupplyInsufficient);
        }

        // ---------------------------------------------------------------------------------------
        // Exclusion ownership.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// A conflict on the neighbouring date excludes the comparison, not the pair: the own-date result
        /// still stands in the primary population, and only this control loses it.
        /// <para>
        /// The conflict is internal to one source, which is the case the freeze record keeps distinct: it
        /// does not enter the shared-position disagreement rate, because no second source observed that
        /// position, but it still excludes its own pair by the per-pair integrity rule.
        /// </para>
        /// </remarks>
        [Fact]
        public void AConflictOnTheNeighbourDateDropsOnlyTheComparison()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(Own, "09:00:00");
            fixture.AddIncoming(Own, "09:01:00");
            AddOoii(fixture, Own);

            var previous = DirectionSequenceGateFixture.Day(0);
            AddIioo(fixture, previous);

            AddSameSourceConflict(fixture, previous);

            var census = fixture.AnalyseAlignment();
            var control = census.BinaryAdjacentControls
                .Single(
                    candidate => candidate.Offset
                        == DirectionSequenceAdjacentOffset.PreviousDay);

            Assert.Equal(0, control.Accounting.IntersectionPairCount);
            Assert.Equal(1, control.Accounting.DroppedNeighbourDateConflict);

            // The own date is untouched: it is still in the primary population and still admits.
            Assert.Equal(1, census.Binary.PairCount);
            Assert.Equal(1, census.Binary.ObservedAdmissions);
        }

        /// <remarks>
        /// A conflict on the own date excludes the pair from the frozen population itself, so it reaches
        /// neither the eligible count nor the intersection. The two exclusions are owned by different
        /// dates and act at different points.
        /// </remarks>
        [Fact]
        public void AConflictOnTheOwnDateRemovesThePairEntirely()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(Own, "09:00:00");
            fixture.AddIncoming(Own, "09:01:00");
            AddOoii(fixture, Own);

            AddSameSourceConflict(fixture, Own);

            // Another informative pair elsewhere, so the run still has a population to report.
            var other = DirectionSequenceGateFixture.Day(20);
            fixture.AddOutgoing(other, "09:00:00");
            fixture.AddIncoming(other, "09:01:00");
            AddOoii(fixture, other);

            AddIioo(fixture, DirectionSequenceGateFixture.Day(0));

            var control = Previous(fixture);

            Assert.Equal(1, control.Accounting.OwnDateEligiblePairCount);
            Assert.Equal(0, control.Accounting.IntersectionPairCount);
            Assert.Equal(1, control.Accounting.DroppedNeighbourDateAbsent);
        }

        // ---------------------------------------------------------------------------------------
        // Accounting and composition.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void EveryEligiblePairIsAccountedForByTheIntersectionOrAReason()
        {
            using var fixture = WithBothNeighbours();

            var census = fixture.AnalyseAlignment();

            foreach (var control in census.BinaryAdjacentControls)
            {
                Assert.Equal(
                    control.Accounting.OwnDateEligiblePairCount,
                    control.Accounting.AccountedTotal);
            }

            foreach (var control in census.GradedAdjacentControls)
            {
                Assert.Equal(
                    control.Accounting.OwnDateEligiblePairCount,
                    control.Accounting.AccountedTotal);
            }
        }

        /// <remarks>
        /// The composition table uses only the predeclared coarse bands, which are exact unions of the
        /// frozen descriptive bands, and every cell obeys the suppression floor. A one-pair intersection
        /// is withheld rather than printed.
        /// </remarks>
        [Fact]
        public void TheCompositionUsesPredeclaredCoarseBandsAndSuppressesSmallCells()
        {
            using var fixture = WithBothNeighbours();

            var composition = Previous(fixture).Composition;

            Assert.Equal(
                ["MessageLength", "MessageLength", "MessageLength", "q_r", "q_r", "q_r"],
                composition.FullPopulation.Select(cell => cell.Dimension));

            Assert.Equal(
                ["1-2", "3-5", "6+", "0<q_r<=0.75", "0.75<q_r<1", "q_r=1"],
                composition.FullPopulation.Select(cell => cell.Band));

            // One pair is below the floor of ten on both sides, so both are withheld and counted.
            Assert.Equal(4, composition.SuppressedCellCount);
            Assert.All(composition.FullPopulation, cell => Assert.Equal(0, cell.PairCount));
            Assert.All(composition.Intersection, cell => Assert.Equal(0, cell.PairCount));
        }

        // ---------------------------------------------------------------------------------------
        // Fixtures.
        // ---------------------------------------------------------------------------------------

        private static DirectionSequenceBinaryAdjacentControl Previous(
            DirectionSequenceGateFixture fixture) =>
            fixture.AnalyseAlignment().BinaryAdjacentControls
                .Single(
                    control => control.Offset == DirectionSequenceAdjacentOffset.PreviousDay);

        /// <summary>
        /// One informative pair with a token-only neighbour on each side, both in the same reference class
        /// as the own date but in the order that does not admit.
        /// </summary>
        private static DirectionSequenceGateFixture WithBothNeighbours()
        {
            var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(Own, "09:00:00");
            fixture.AddIncoming(Own, "09:01:00");
            AddOoii(fixture, Own);

            AddIioo(fixture, DirectionSequenceGateFixture.Day(0));
            AddIioo(fixture, DirectionSequenceGateFixture.Day(2));

            return fixture;
        }

        /// <summary>Two outgoing then two incoming positions.</summary>
        private static void AddOoii(DirectionSequenceGateFixture fixture, DateOnly date)
        {
            fixture.AddToken(date, 1, isSent: true);
            fixture.AddToken(date, 2, isSent: true);
            fixture.AddToken(date, 3, isSent: false);
            fixture.AddToken(date, 4, isSent: false);
        }

        /// <summary>Two incoming then two outgoing positions: the same class, the other order.</summary>
        private static void AddIioo(DirectionSequenceGateFixture fixture, DateOnly date)
        {
            fixture.AddToken(date, 1, isSent: false);
            fixture.AddToken(date, 2, isSent: false);
            fixture.AddToken(date, 3, isSent: true);
            fixture.AddToken(date, 4, isSent: true);
        }

        /// <summary>
        /// One logical position whose two copies, from the same source, disagree about direction.
        /// </summary>
        /// <remarks>
        /// Within one source, so it excludes its pair without entering the cross-source shared-position
        /// disagreement rate — the same distinction the freeze record draws for the archive's own
        /// single-source conflict.
        /// </remarks>
        private static void AddSameSourceConflict(
            DirectionSequenceGateFixture fixture, DateOnly date)
        {
            fixture.AddToken(date, 7, isSent: true);
            fixture.AddToken(date, 7, isSent: false);
        }
    }
}
