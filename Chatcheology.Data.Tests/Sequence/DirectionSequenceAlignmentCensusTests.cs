using Chatcheology.Data.Media;
using Chatcheology.Data.Sequence;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests the Stage B2C-1 outcome census on shapes whose every figure can be worked out by hand.
    /// </summary>
    /// <remarks>
    /// The shapes are chosen so the reference is calculable without trusting the code that computes it.
    /// The workhorse is a message pattern of one outgoing then one incoming symbol against a token side of
    /// two outgoing then two incoming positions: that class holds exactly two arrangements — <c>OOII</c>
    /// and <c>IIOO</c> — one of which admits, so <c>q_r</c> is one half exactly. Reversing the message
    /// pattern to incoming-then-outgoing keeps <c>q_r</c> at one half and turns the observed outcome from
    /// an admission into a failure, which is what makes a difference of plus or minus one half readable
    /// straight off the fixture.
    /// </remarks>
    public class DirectionSequenceAlignmentCensusTests
    {
        private static readonly DateOnly First = DirectionSequenceGateFixture.FirstDate;

        /// <summary>Four thirds and two thirds, at the reading convention's twelve decimal places.</summary>
        private const double TwoThirds = 0.666666666666d;

        private const double OneThird = 0.333333333333d;

        // ---------------------------------------------------------------------------------------
        // C5 — the binary co-primary comparison.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AnAdmittingInformativePairIsOneAdmissionAgainstAHalf()
        {
            using var fixture = Admitting();

            var binary = fixture.AnalyseAlignment().Binary;

            Assert.Equal("BinaryInformative", binary.PopulationName);
            Assert.Equal(1, binary.PairCount);
            Assert.Equal(1, binary.ObservedAdmissions);
            Assert.Equal(0.5d, binary.SumOfConditionalAdmissionProbability);
            Assert.Equal(0.5d, binary.ObservedMinusExpected);
        }

        [Fact]
        public void AFailingInformativePairIsNoAdmissionAgainstAHalf()
        {
            using var fixture = Failing();

            var binary = fixture.AnalyseAlignment().Binary;

            Assert.Equal(1, binary.PairCount);
            Assert.Equal(0, binary.ObservedAdmissions);
            Assert.Equal(0.5d, binary.SumOfConditionalAdmissionProbability);
            Assert.Equal(-0.5d, binary.ObservedMinusExpected);
        }

        /// <remarks>
        /// The pre-registered range is the population's algebraic bounds and nothing else: no admissions
        /// at all, or every pair admitting.
        /// </remarks>
        [Fact]
        public void ThePreRegisteredRangeIsThePopulationsAlgebraicBounds()
        {
            using var fixture = Mixed();

            var binary = fixture.AnalyseAlignment().Binary;

            Assert.Equal(2, binary.PairCount);
            Assert.Equal(1.0d, binary.SumOfConditionalAdmissionProbability);
            Assert.Equal(-1.0d, binary.MinimumPossibleObservedMinusExpected);
            Assert.Equal(1.0d, binary.MaximumPossibleObservedMinusExpected);
            Assert.Equal(0.0d, binary.ObservedMinusExpected);
        }

        // ---------------------------------------------------------------------------------------
        // C4 — graded comparison and multiplicity.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheGradedComparisonUsesTheSameReadingConventionOnBothSides()
        {
            using var fixture = Admitting();

            var graded = fixture.AnalyseAlignment().Graded;

            Assert.Equal("GradedInformative", graded.PopulationName);
            Assert.Equal(1, graded.PairCount);

            // Four embeddings of six choices, against an expected four of twelve.
            Assert.Equal(TwoThirds, graded.SumOfObservedNormalisedEmbeddingShare);
            Assert.Equal(OneThird, graded.SumOfExpectedEmbeddingShare);
            Assert.Equal(TwoThirds - OneThird, graded.ObservedMinusExpected);
        }

        [Fact]
        public void AFailingPairContributesNoObservedShare()
        {
            using var fixture = Failing();

            var graded = fixture.AnalyseAlignment().Graded;

            Assert.Equal(0d, graded.SumOfObservedNormalisedEmbeddingShare);
            Assert.Equal(OneThird, graded.SumOfExpectedEmbeddingShare);
            Assert.Equal(-OneThird, graded.ObservedMinusExpected);
        }

        [Fact]
        public void TheMultiplicityCensusClassifiesEveryClassifiedPair()
        {
            using var fixture = Mixed();

            var multiplicity = fixture.AnalyseAlignment().Multiplicity;

            Assert.Equal("ClassifiedPairs", multiplicity.PopulationName);
            Assert.Equal(2, multiplicity.PairCount);
            Assert.Equal(2, multiplicity.ClassifiedTotal);

            // Four embeddings on the admitting pair, none on the failing one.
            Assert.Equal(1, multiplicity.SeveralExactMonotoneDirectionAlignments);
            Assert.Equal(0, multiplicity.OneExactMonotoneDirectionAlignment);
            Assert.Equal(1, multiplicity.NoExactMonotoneDirectionAlignment);
            Assert.Equal(1, multiplicity.PairsWithValidEmbedding);
        }

        /// <remarks>
        /// The reconciliation determinacy forces. A binary-determinate pair has <c>q_r = 1</c>, so every
        /// arrangement of its class admits and the observed order is one of them: it must admit before the
        /// run. The census separates those admissions from the ones that could have gone either way, and
        /// the count of pairs carrying no embedding must be exactly the informative pairs that did not
        /// admit.
        /// </remarks>
        [Fact]
        public void DeterminateAdmissionsAreForcedAndSeparatedFromInformativeOnes()
        {
            using var fixture = WithDeterminatePair();

            var multiplicity = fixture.AnalyseAlignment().Multiplicity;

            Assert.Equal(1, multiplicity.BinaryDeterminatePairCount);
            Assert.Equal(1, multiplicity.ForcedDeterminateAdmissionCount);
            Assert.Equal(1, multiplicity.BinaryInformativePairCount);
            Assert.Equal(1, multiplicity.InformativeAdmissionCount);

            Assert.Equal(
                multiplicity.ForcedDeterminateAdmissionCount
                    + multiplicity.InformativeAdmissionCount,
                multiplicity.PairsWithValidEmbedding);

            Assert.Equal(
                multiplicity.BinaryInformativePairCount - multiplicity.InformativeAdmissionCount,
                multiplicity.NoExactMonotoneDirectionAlignment);
        }

        /// <remarks>
        /// The strongest available check on the observed-share path, and it runs on the pairs the primary
        /// graded figure excludes. A graded-determinate pair has zero share variance across its reference
        /// class, so the observed arrangement's share must equal the expected share exactly — not within a
        /// tolerance, and as identical readings, because both come from the same conversion.
        /// </remarks>
        [Fact]
        public void EveryGradedDeterminatePairsObservedShareEqualsItsExpectedShare()
        {
            using var fixture = WithGradedDeterminatePair();

            var check = fixture.AnalyseAlignment().GradedDeterminateCheck;

            Assert.Equal(1, check.PairCount);
            Assert.True(check.EveryReadingIdentical);

            Assert.Equal(
                check.SumOfExpectedEmbeddingShare,
                check.SumOfObservedNormalisedEmbeddingShare);

            // One embedding of three choices, and an expectation of two over six.
            Assert.Equal(OneThird, check.SumOfObservedNormalisedEmbeddingShare);
        }

        // ---------------------------------------------------------------------------------------
        // C2 and C3.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void SupplyLossIsReportedOverEveryAssessedPair()
        {
            using var fixture = WithSupplyShortfall();

            var supply = fixture.AnalyseAlignment().SupplyLoss;

            Assert.Equal("SupplyAssessed", supply.PopulationName);

            // The informative pair supplies completely; the short date cannot.
            Assert.Equal(1, supply.ZeroSupplyLossPairCount);
            Assert.Equal(1, supply.PositiveSupplyLossPairCount);
            Assert.Equal(supply.PairCount, supply.ClassifiedTotal);

            Assert.Contains(
                supply.SupplyLossDistribution, row => row.Value == 1 && row.Count == 1);
        }

        /// <remarks>
        /// The order-testable population is supply-sufficient by construction, so its supply loss is
        /// identically zero and order loss is everything unaligned. That is expected in advance, and the
        /// census says so rather than leaving a column of zeros to be interpreted.
        /// </remarks>
        [Fact]
        public void TheOrderTestablePopulationCarriesNoSupplyLoss()
        {
            using var fixture = WithSupplyShortfall();

            var order = fixture.AnalyseAlignment().OrderLoss;

            Assert.Equal("ClassifiedPairs", order.PopulationName);
            Assert.Equal(0, order.SupplyLossPairCount);
            Assert.Equal(order.PairCount, order.ClassifiedTotal);
        }

        [Fact]
        public void OrderLossIsZeroWhereTheObservedOrderCarriesEverything()
        {
            using var fixture = Admitting();

            var order = fixture.AnalyseAlignment().OrderLoss;

            Assert.Equal(1, order.ZeroOrderLossPairCount);
            Assert.Equal(0, order.PositiveOrderLossPairCount);
            Assert.Equal(2, order.OrderedDirectionAlignment.Maximum);
        }

        [Fact]
        public void OrderLossIsPositiveWhereTheObservedOrderDoesNot()
        {
            using var fixture = Failing();

            var order = fixture.AnalyseAlignment().OrderLoss;

            Assert.Equal(0, order.ZeroOrderLossPairCount);
            Assert.Equal(1, order.PositiveOrderLossPairCount);

            // One of two symbols carried, so one lost to order and none to supply.
            Assert.Equal(1, order.OrderedDirectionAlignment.Maximum);
            Assert.Contains(order.OrderLossDistribution, row => row.Value == 1 && row.Count == 1);
        }

        // ---------------------------------------------------------------------------------------
        // Failure attribution.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AFailureOnCompleteCoverageIsAttributable()
        {
            using var fixture = Failing();

            var attribution = fixture.AnalyseAlignment().FailureAttribution;

            Assert.Equal(1, attribution.FailedEmbeddingCount);
            Assert.Equal(0, attribution.FailureNotAttributable);
            Assert.Equal(1, attribution.FailureWithCompleteTokenCoverage);
            Assert.Equal(attribution.FailedEmbeddingCount, attribution.AttributionTotal);
        }

        [Fact]
        public void AFailureBesideGrammarCoverageLossIsNotAttributable()
        {
            using var fixture = Failing();
            fixture.AddNamed(First, "0010-1", isSent: true);

            var attribution = fixture.AnalyseAlignment().FailureAttribution;

            Assert.Equal(1, attribution.FailedEmbeddingCount);
            Assert.Equal(1, attribution.FailureNotAttributable);
            Assert.Equal(1, attribution.FailuresWithGrammarCoverageLossOnly);
            Assert.Equal(0, attribution.FailureWithCompleteTokenCoverage);
        }

        [Fact]
        public void AFailureBesideDirectionCoverageLossIsNotAttributable()
        {
            using var fixture = Failing();
            fixture.AddToken(First, 9, isSent: null);

            var attribution = fixture.AnalyseAlignment().FailureAttribution;

            Assert.Equal(1, attribution.FailureNotAttributable);
            Assert.Equal(1, attribution.FailuresWithDirectionCoverageLossOnly);
        }

        [Fact]
        public void AFailureBesideBothLossesIsCountedOnce()
        {
            using var fixture = Failing();
            fixture.AddNamed(First, "0010-1", isSent: true);
            fixture.AddToken(First, 9, isSent: null);

            var attribution = fixture.AnalyseAlignment().FailureAttribution;

            Assert.Equal(1, attribution.FailureNotAttributable);
            Assert.Equal(1, attribution.FailuresWithBothCoverageLosses);
            Assert.Equal(attribution.FailureNotAttributable, attribution.LossCombinationTotal);
        }

        /// <remarks>
        /// The label changes interpretation and nothing else: the failure is still a failure, the pair is
        /// still in its census, and the admission count does not move.
        /// </remarks>
        [Fact]
        public void TheLabelDoesNotEraseTheFailureOrMoveAnAdmission()
        {
            using var fixture = Failing();
            fixture.AddNamed(First, "0010-1", isSent: true);

            var census = fixture.AnalyseAlignment();

            Assert.Equal(1, census.FailureAttribution.FailureNotAttributable);
            Assert.Equal(0, census.Binary.ObservedAdmissions);
            Assert.Equal(1, census.Multiplicity.NoExactMonotoneDirectionAlignment);
            Assert.Equal(1, census.Binary.PairCount);
        }

        // ---------------------------------------------------------------------------------------
        // C5c — reversal.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The forced cells are algebraic, not observed. A binary-determinate pair admits under both
        /// orders because both are members of a class every arrangement of which admits, and a
        /// graded-determinate pair ties because the class has zero share variance. Neither belongs in the
        /// contingency that describes what could have differed.
        /// </remarks>
        [Fact]
        public void ForcedAgreementIsSeparatedFromTheVariablePopulation()
        {
            using var fixture = WithGradedDeterminatePair();

            var reversal = fixture.AnalyseAlignment().Reversal;

            Assert.Equal("ClassifiedPairs", reversal.PopulationName);
            Assert.Equal(1, reversal.ForcedBothAdmitPairCount);
            Assert.Equal(1, reversal.ForcedGradedTiePairCount);
            Assert.Equal(0, reversal.VariableAdmissionPairCount);
            Assert.Equal(0, reversal.VariableAdmission.Total);
            Assert.Equal(0, reversal.VariableSharePairCount);
        }

        [Fact]
        public void TheVariableContingencyCoversOnlyInformativePairs()
        {
            using var fixture = Mixed();

            var reversal = fixture.AnalyseAlignment().Reversal;

            Assert.Equal(2, reversal.VariableAdmissionPairCount);
            Assert.Equal(2, reversal.VariableAdmission.Total);
            Assert.Equal(0, reversal.ForcedBothAdmitPairCount);

            // OI admits OOII and not IIOO; IO is the mirror. Reversing OOII gives IIOO, so exactly one
            // of each pair's two orders admits.
            Assert.Equal(1, reversal.VariableAdmission.ActualOnlyAdmits);
            Assert.Equal(1, reversal.VariableAdmission.ReversedOnlyAdmits);
            Assert.Equal(0, reversal.VariableAdmission.BothAdmit);
            Assert.Equal(0, reversal.VariableAdmission.NeitherAdmits);
        }

        [Fact]
        public void APalindromicPatternIsCountedAsSuch()
        {
            using var fixture = new DirectionSequenceGateFixture();

            // OIO against a token side long enough to carry it.
            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddIncoming(First, "09:01:00");
            fixture.AddOutgoing(First, "09:02:00");

            fixture.AddToken(First, 1, isSent: true);
            fixture.AddToken(First, 2, isSent: true);
            fixture.AddToken(First, 3, isSent: false);
            fixture.AddToken(First, 4, isSent: false);
            fixture.AddToken(First, 5, isSent: true);

            var reversal = fixture.AnalyseAlignment().Reversal;

            Assert.Equal(1, reversal.PalindromicPatternPairCount);
            Assert.Equal(reversal.PairCount, reversal.AlignmentComparison.Total);
        }

        // ---------------------------------------------------------------------------------------
        // C6 and C7.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void DescriptiveSplitsAccountForEveryPairOfTheirPopulation()
        {
            using var fixture = Mixed();

            var splits = fixture.AnalyseAlignment().DescriptiveSplits;

            Assert.Equal(2, splits.Count);

            Assert.All(
                splits,
                split =>
                {
                    Assert.Equal("BinaryInformative", split.PopulationName);
                    Assert.Equal(split.PairCount, split.AccountedPairCount);
                });
        }

        /// <remarks>
        /// A cell below the floor is withheld rather than printed, and the withholding is counted so the
        /// split still reconciles to its population.
        /// </remarks>
        [Fact]
        public void ASmallDescriptiveCellIsSuppressedAndCounted()
        {
            using var fixture = Mixed();

            var split = fixture.AnalyseAlignment().DescriptiveSplits[0];

            // Two pairs is below the floor of ten, so whatever band they fall in is withheld.
            Assert.True(split.SuppressedCellCount >= 1);
            Assert.Equal(2, split.SuppressedPairCount);
            Assert.Equal(2, split.AccountedPairCount);
            Assert.All(split.Cells, cell => Assert.True(cell.PairCount == 0));
        }

        [Fact]
        public void ScopeSensitivityIsReportedForTheNonPrimaryScopeOnly()
        {
            using var fixture = Mixed();

            var sensitivity = fixture.AnalyseAlignment().ScopeSensitivity;

            var scope = Assert.Single(sensitivity);

            Assert.Equal(ScopeLevel.SourceDate, scope.Scope);
            Assert.Contains("not independent support", scope.PreRegisteredExpectation);
            Assert.Equal("SourceDate BinaryInformative", scope.Binary.PopulationName);
        }

        /// <remarks>
        /// With one source the two scopes see the same positions, so the sensitivity result must equal the
        /// primary one. That is the pre-registered near-identity in its simplest form: a confirmation the
        /// scope reduction behaved as documented, not a second observation of the archive.
        /// </remarks>
        [Fact]
        public void OneSourceMakesTheSensitivityResultIdenticalToThePrimary()
        {
            using var fixture = Mixed();

            var census = fixture.AnalyseAlignment();
            var sensitivity = census.ScopeSensitivity.Single();

            Assert.Equal(census.Binary.PairCount, sensitivity.Binary.PairCount);
            Assert.Equal(census.Binary.ObservedAdmissions, sensitivity.Binary.ObservedAdmissions);

            Assert.Equal(
                census.Binary.ObservedMinusExpected, sensitivity.Binary.ObservedMinusExpected);
        }

        // ---------------------------------------------------------------------------------------
        // Fixtures.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// One informative pair whose observed order admits: pattern <c>OI</c> against <c>OOII</c>.
        /// </summary>
        private static DirectionSequenceGateFixture Admitting()
        {
            var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddIncoming(First, "09:01:00");

            AddOoii(fixture, First);

            return fixture;
        }

        /// <summary>
        /// One informative pair whose observed order does not: pattern <c>IO</c> against <c>OOII</c>.
        /// </summary>
        private static DirectionSequenceGateFixture Failing()
        {
            var fixture = new DirectionSequenceGateFixture();

            fixture.AddIncoming(First, "09:00:00");
            fixture.AddOutgoing(First, "09:01:00");

            AddOoii(fixture, First);

            return fixture;
        }

        /// <summary>One of each, on dates far enough apart not to be neighbours.</summary>
        private static DirectionSequenceGateFixture Mixed()
        {
            var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddIncoming(First, "09:01:00");
            AddOoii(fixture, First);

            var later = DirectionSequenceGateFixture.Day(10);
            fixture.AddIncoming(later, "09:00:00");
            fixture.AddOutgoing(later, "09:01:00");
            AddOoii(fixture, later);

            return fixture;
        }

        /// <summary>
        /// An informative pair and a binary-determinate one.
        /// </summary>
        /// <remarks>
        /// The determinate pair is <c>OI</c> against <c>OIOI</c>: that class holds <c>OIOI</c> and
        /// <c>IOIO</c> and both admit <c>OI</c>, so <c>q_r</c> is one. Its share still varies across the
        /// class, so it is graded-informative — the same combination the real archive's determinate pairs
        /// mostly have.
        /// </remarks>
        private static DirectionSequenceGateFixture WithDeterminatePair()
        {
            var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddIncoming(First, "09:01:00");
            AddOoii(fixture, First);

            var later = DirectionSequenceGateFixture.Day(10);
            fixture.AddOutgoing(later, "09:00:00");
            fixture.AddIncoming(later, "09:01:00");

            fixture.AddToken(later, 1, isSent: true);
            fixture.AddToken(later, 2, isSent: false);
            fixture.AddToken(later, 3, isSent: true);
            fixture.AddToken(later, 4, isSent: false);

            return fixture;
        }

        /// <summary>
        /// One graded-determinate pair: pattern <c>OO</c> against <c>OOI</c>.
        /// </summary>
        /// <remarks>
        /// That class holds <c>OOI</c> and <c>IOO</c>, each admitting <c>OO</c> in exactly one way, so
        /// <c>A * Q</c> equals <c>P * P</c> and the share has zero variance across the class. The observed
        /// share must therefore equal the expected share exactly: one embedding of three choices, against
        /// two of six.
        /// </remarks>
        private static DirectionSequenceGateFixture WithGradedDeterminatePair()
        {
            var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddOutgoing(First, "09:01:00");

            fixture.AddToken(First, 1, isSent: true);
            fixture.AddToken(First, 2, isSent: true);
            fixture.AddToken(First, 3, isSent: false);

            return fixture;
        }

        /// <summary>An informative pair, and a date whose token side is one symbol short.</summary>
        private static DirectionSequenceGateFixture WithSupplyShortfall()
        {
            var fixture = Admitting();

            var later = DirectionSequenceGateFixture.Day(10);

            fixture.AddOutgoing(later, "09:00:00");
            fixture.AddOutgoing(later, "09:01:00");
            fixture.AddToken(later, 1, isSent: true);

            return fixture;
        }

        /// <summary>Two outgoing then two incoming token positions on one date.</summary>
        private static void AddOoii(DirectionSequenceGateFixture fixture, DateOnly date)
        {
            fixture.AddToken(date, 1, isSent: true);
            fixture.AddToken(date, 2, isSent: true);
            fixture.AddToken(date, 3, isSent: false);
            fixture.AddToken(date, 4, isSent: false);
        }
    }
}
