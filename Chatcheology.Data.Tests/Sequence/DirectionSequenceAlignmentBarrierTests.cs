using Chatcheology.Data.Media;
using Chatcheology.Data.Sequence;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests the gate the whole Stage B2C-1 stage rests on: no outcome may be calculated until the
    /// accepted Stage B2C-0 state has been reproduced.
    /// </summary>
    /// <remarks>
    /// Every test here breaks one declared target and asserts the run stops. The declarations are built
    /// from the fixture's own census, so a passing run reconciles targets the test supplied and no archive
    /// figure is compiled into the production code — which is the property the freeze depends on.
    /// </remarks>
    public class DirectionSequenceAlignmentBarrierTests
    {
        private static readonly DateOnly First = DirectionSequenceGateFixture.FirstDate;

        // ---------------------------------------------------------------------------------------
        // The barrier passes only on an exact reproduction.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AMatchingFreezeLetsTheOutcomeStageProceed()
        {
            using var fixture = Populated();

            var census = fixture.AnalyseAlignment();

            Assert.True(census.Reconciliation.ReconciledTargetCount > 40);
            Assert.Equal(ScopeLevel.DeviceGroupDate, census.Reconciliation.PrimaryScope);
        }

        /// <remarks>
        /// The wrong-scope discriminator. The classified population and the reference readings are
        /// scope-invariant, so a run numbering by source instead of by device group would reconcile
        /// everything except the source scope's supply-assessed count — which is why both scopes are
        /// declared and both are checked.
        /// </remarks>
        [Fact]
        public void ASupplyAssessedCountFromTheOtherScopeIsRefused()
        {
            using var fixture = Populated();

            var frozen = Frozen(fixture);

            var sourceAssessed = frozen.Scopes
                .Single(scope => scope.Scope == ScopeLevel.SourceDate)
                .SupplyAssessedPairCount;

            var deviceAssessed = frozen.Scopes
                .Single(scope => scope.Scope == ScopeLevel.DeviceGroupDate)
                .SupplyAssessedPairCount;

            // The fixture is built so the two scopes disagree, which is what makes the check meaningful.
            Assert.NotEqual(sourceAssessed, deviceAssessed);

            var swapped = frozen with
            {
                Scopes =
                [
                    .. frozen.Scopes.Select(
                        scope => scope.Scope == ScopeLevel.DeviceGroupDate
                            ? scope with { SupplyAssessedPairCount = sourceAssessed }
                            : scope),
                ],
            };

            Refuses(fixture, swapped);
        }

        [Fact]
        public void APrimaryScopeOtherThanTheFrozenOneIsRefused()
        {
            using var fixture = Populated();

            Refuses(fixture, Frozen(fixture) with { PrimaryScope = ScopeLevel.SourceDate });
        }

        [Theory]
        [InlineData("ClassifiedPairCount")]
        [InlineData("BinaryInformativePairCount")]
        [InlineData("GradedInformativePairCount")]
        [InlineData("BinaryDeterminatePairCount")]
        [InlineData("GradedDeterminatePairCount")]
        [InlineData("BinaryDeterminateAndGradedInformative")]
        [InlineData("BinaryDeterminateAndGradedDeterminate")]
        [InlineData("BinaryInformativeAndGradedInformative")]
        [InlineData("SupplyInsufficientPairCountAfterCollapse")]
        [InlineData("SupplySufficientPairCountAfterCollapse")]
        [InlineData("DegeneratePairCount")]
        [InlineData("NoTokenPositionPairCount")]
        [InlineData("ClassifiedMessageObservationCount")]
        [InlineData("ExactlyOneQrBandPairCount")]
        [InlineData("ConsideredAttachmentCount")]
        [InlineData("DistinctAttachmentDateCount")]
        [InlineData("OutgoingAttachmentCount")]
        [InlineData("IncomingAttachmentCount")]
        public void AnyDriftedIntegerTargetIsRefused(string target)
        {
            using var fixture = Populated();

            var frozen = Frozen(fixture);

            // One more than the truth, whatever the truth is: the test does not need to know the figure,
            // only that the barrier compares it.
            var drifted = target switch
            {
                "ClassifiedPairCount" =>
                    frozen with { ClassifiedPairCount = frozen.ClassifiedPairCount + 1 },
                "BinaryInformativePairCount" =>
                    frozen with
                    {
                        BinaryInformativePairCount = frozen.BinaryInformativePairCount + 1,
                    },
                "GradedInformativePairCount" =>
                    frozen with
                    {
                        GradedInformativePairCount = frozen.GradedInformativePairCount + 1,
                    },
                "BinaryDeterminatePairCount" =>
                    frozen with
                    {
                        BinaryDeterminatePairCount = frozen.BinaryDeterminatePairCount + 1,
                    },
                "GradedDeterminatePairCount" =>
                    frozen with
                    {
                        GradedDeterminatePairCount = frozen.GradedDeterminatePairCount + 1,
                    },
                "BinaryDeterminateAndGradedInformative" =>
                    frozen with
                    {
                        BinaryDeterminateAndGradedInformative =
                            frozen.BinaryDeterminateAndGradedInformative + 1,
                    },
                "BinaryDeterminateAndGradedDeterminate" =>
                    frozen with
                    {
                        BinaryDeterminateAndGradedDeterminate =
                            frozen.BinaryDeterminateAndGradedDeterminate + 1,
                    },
                "BinaryInformativeAndGradedInformative" =>
                    frozen with
                    {
                        BinaryInformativeAndGradedInformative =
                            frozen.BinaryInformativeAndGradedInformative + 1,
                    },
                "SupplyInsufficientPairCountAfterCollapse" =>
                    frozen with
                    {
                        SupplyInsufficientPairCountAfterCollapse =
                            frozen.SupplyInsufficientPairCountAfterCollapse + 1,
                    },
                "SupplySufficientPairCountAfterCollapse" =>
                    frozen with
                    {
                        SupplySufficientPairCountAfterCollapse =
                            frozen.SupplySufficientPairCountAfterCollapse + 1,
                    },
                "DegeneratePairCount" =>
                    frozen with { DegeneratePairCount = frozen.DegeneratePairCount + 1 },
                "NoTokenPositionPairCount" =>
                    frozen with
                    {
                        NoTokenPositionPairCount = frozen.NoTokenPositionPairCount + 1,
                    },
                "ClassifiedMessageObservationCount" =>
                    frozen with
                    {
                        ClassifiedMessageObservationCount =
                            frozen.ClassifiedMessageObservationCount + 1,
                    },
                "ExactlyOneQrBandPairCount" =>
                    frozen with
                    {
                        ExactlyOneQrBandPairCount = frozen.ExactlyOneQrBandPairCount + 1,
                    },
                "ConsideredAttachmentCount" =>
                    frozen with
                    {
                        ConsideredAttachmentCount = frozen.ConsideredAttachmentCount + 1,
                    },
                "DistinctAttachmentDateCount" =>
                    frozen with
                    {
                        DistinctAttachmentDateCount = frozen.DistinctAttachmentDateCount + 1,
                    },
                "OutgoingAttachmentCount" =>
                    frozen with { OutgoingAttachmentCount = frozen.OutgoingAttachmentCount + 1 },
                _ => frozen with { IncomingAttachmentCount = frozen.IncomingAttachmentCount + 1 },
            };

            Refuses(fixture, drifted);
        }

        /// <remarks>
        /// The empty cross-tabulation cell is a required invariant, but it is not sufficient on its own:
        /// a graded classification that produced nothing determinate would satisfy it while redefining
        /// the graded population, which is why the non-empty cells are declared too. Declaring the empty
        /// cell as non-empty must be refused.
        /// </remarks>
        [Fact]
        public void ANonEmptyBinaryInformativeAndGradedDeterminateCellIsRefused()
        {
            using var fixture = Populated();

            Refuses(
                fixture,
                Frozen(fixture) with { BinaryInformativeAndGradedDeterminate = 1 });
        }

        [Theory]
        [InlineData("classified")]
        [InlineData("informative")]
        [InlineData("share")]
        [InlineData("shareInformative")]
        public void AnyDriftedReferenceReadingIsRefused(string reading)
        {
            using var fixture = Populated();

            var frozen = Frozen(fixture);

            var drifted = reading switch
            {
                "classified" =>
                    frozen with { SumOfConditionalAdmissionProbability = "999.999999" },
                "informative" =>
                    frozen with
                    {
                        SumOfConditionalAdmissionProbabilityOverInformative = "999.999999",
                    },
                "share" => frozen with { SumOfExpectedEmbeddingShare = "999.999999" },
                _ => frozen with { SumOfExpectedEmbeddingShareOverInformative = "999.999999" },
            };

            Refuses(fixture, drifted);
        }

        /// <remarks>
        /// A reading one part in a million out is still a different reading. The six-decimal rendering is
        /// what the freeze record printed, so that is what is compared.
        /// </remarks>
        [Fact]
        public void AReadingDifferingInTheSixthDecimalIsRefused()
        {
            using var fixture = Populated();

            var frozen = Frozen(fixture);
            var declared = frozen.SumOfConditionalAdmissionProbabilityOverInformative;

            var nudged = declared[..^1]
                + (declared[^1] == '0' ? '1' : (char)(declared[^1] - 1));

            Refuses(
                fixture,
                frozen with { SumOfConditionalAdmissionProbabilityOverInformative = nudged });
        }

        // ---------------------------------------------------------------------------------------
        // Provenance.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The gate may run without a Stage A declaration and say it did not reconcile. An alignment run
        /// may not: it compares an observed order against a reference, and a population that had drifted
        /// since Stage A would be compared against the wrong reference.
        /// </remarks>
        [Fact]
        public void AnAlignmentRunWithoutReconciledStageACoverageIsRefused()
        {
            using var fixture = Populated();

            var request = fixture.AlignmentRequest();

            var without = new DirectionSequenceAlignmentRequest
            {
                DatabasePath = request.DatabasePath,
                ConversationID = request.ConversationID,
                LocalParticipantID = request.LocalParticipantID,
                DeviceGroups = request.DeviceGroups,
                StageATokenCoverage = [],
                StageB1 = request.StageB1,
                FrozenGate = request.FrozenGate,
            };

            // An empty declaration does not name the workspace's sources, so the gate itself refuses it
            // before the barrier is reached — which is the same guarantee arriving one step earlier.
            Assert.Throws<InvalidOperationException>(
                () => new DirectionSequenceAlignmentService().Analyse(without));
        }

        [Fact]
        public void AStageB1DeclarationDenyingMonotoneTokenOrderIsRefused()
        {
            using var fixture = Populated();

            var request = fixture.AlignmentRequest();

            Assert.Throws<InvalidOperationException>(
                () => new DirectionSequenceAlignmentService().Analyse(
                    Rebuild(
                        request,
                        request.StageB1 with { SupportedTokenIsMonotoneWithinLocalDate = false })));
        }

        [Fact]
        public void AStageB1DeclarationWithADriftedPositionCountIsRefused()
        {
            using var fixture = Populated();

            var request = fixture.AlignmentRequest();

            Assert.Throws<InvalidOperationException>(
                () => new DirectionSequenceAlignmentService().Analyse(
                    Rebuild(
                        request,
                        request.StageB1 with
                        {
                            DistinctLogicalPositionCount =
                                request.StageB1.DistinctLogicalPositionCount + 1,
                        })));
        }

        /// <remarks>
        /// The collision finding is carried forward rather than recomputed — the gate has no reason to
        /// track which assets share a key — so it cannot be reconciled against a recount. What can still
        /// be refused is a declaration that does not describe the universe beside it.
        /// </remarks>
        [Theory]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void AStageB1CollisionFindingThatCannotDescribeItsUniverseIsRefused(int collisions)
        {
            using var fixture = Populated();

            var request = fixture.AlignmentRequest();

            Assert.Throws<InvalidOperationException>(
                () => new DirectionSequenceAlignmentService().Analyse(
                    Rebuild(
                        request,
                        request.StageB1 with
                        {
                            PositionsWithMoreThanOneAssetCount = collisions,
                        })));
        }

        /// <remarks>
        /// Cross-source overlap is not Stage B1 provenance. Stage B1 established the <c>(date, token)</c>
        /// key universe and its asset cardinality; how those keys distribute across acquisition stores is
        /// something the gate works out for its own cross-source and scope reporting. An earlier draft
        /// required the shared and single-source counts as declared provenance, which asked the caller to
        /// pin a quantity no preserved evidence fixes.
        /// <para>
        /// So a workspace whose sources overlap heavily must run with no overlap figure declared at all —
        /// and the gate must still see that overlap in its own census.
        /// </para>
        /// </remarks>
        [Fact]
        public void HeavyCrossSourceOverlapNeedsNoStageB1Declaration()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var second = fixture.AddSource();
            var third = fixture.AddSource("Third synthetic source");

            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddIncoming(First, "09:01:00");

            // Every position observed by all three sources, agreeing about direction.
            foreach (var (token, isSent) in new[] { (1, true), (2, true), (3, false), (4, false) })
            {
                fixture.AddToken(First, token, isSent);
                fixture.AddToken(First, token, isSent, mediaSourceID: second);
                fixture.AddToken(First, token, isSent, mediaSourceID: third);
            }

            var census = fixture.AnalyseAlignment();

            Assert.Equal(1, census.Binary.PairCount);

            var overlap = census.Reconciliation.Census.CrossSourceOverlap;

            // The gate saw the overlap; the declaration never mentioned it.
            Assert.Equal(4, overlap.DistinctLogicalPositionCount);
            Assert.Equal(4, overlap.SharedLogicalPositionCount);
            Assert.Equal(0, overlap.SingleSourceLogicalPositionCount);
        }

        /// <remarks>
        /// And the same workspace with no overlap at all runs identically, which is the other half of the
        /// point: the declaration is indifferent to a quantity it no longer carries.
        /// </remarks>
        [Fact]
        public void NoCrossSourceOverlapNeedsNoStageB1DeclarationEither()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddIncoming(First, "09:01:00");

            fixture.AddToken(First, 1, isSent: true);
            fixture.AddToken(First, 2, isSent: true);
            fixture.AddToken(First, 3, isSent: false);
            fixture.AddToken(First, 4, isSent: false);

            var census = fixture.AnalyseAlignment();
            var overlap = census.Reconciliation.Census.CrossSourceOverlap;

            Assert.Equal(1, census.Binary.PairCount);
            Assert.Equal(0, overlap.SharedLogicalPositionCount);
            Assert.Equal(4, overlap.SingleSourceLogicalPositionCount);
        }

        [Fact]
        public void AStageB1DeclarationNamingAnotherGroupingIsRefused()
        {
            using var fixture = Populated();

            var request = fixture.AlignmentRequest();

            Assert.Throws<InvalidOperationException>(
                () => new DirectionSequenceAlignmentService().Analyse(
                    Rebuild(
                        request,
                        request.StageB1 with { DeviceGroups = fixture.OneGroupPerSource() })));
        }

        [Fact]
        public void AStageB1DeclarationMisnamingTheDirectionBlindSourcesIsRefused()
        {
            using var fixture = Populated();

            var request = fixture.AlignmentRequest();

            Assert.Throws<InvalidOperationException>(
                () => new DirectionSequenceAlignmentService().Analyse(
                    Rebuild(
                        request,
                        request.StageB1 with { DirectionBlindMediaSourceIDs = [fixture.SourceID] })));
        }

        // ---------------------------------------------------------------------------------------
        // No outcome before the barrier.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The structural proof. The outcome calculator cannot be constructed without a reconciliation,
        /// the reconciliation has no public constructor, and the barrier is the only code that makes one —
        /// so a refused run cannot have computed an outcome, whatever order anything was called in.
        /// </remarks>
        [Fact]
        public void TheReconciliationCannotBeConstructedOutsideTheBarrier()
        {
            Assert.Empty(typeof(DirectionSequenceFrozenGateReconciliation).GetConstructors());
        }

        /// <remarks>
        /// And the calculator that consumes it is not reachable from outside the assembly at all, so the
        /// only public way to an outcome is the service, which runs the barrier first.
        /// </remarks>
        [Fact]
        public void TheOutcomeCalculatorIsNotPubliclyReachable()
        {
            var calculator = typeof(DirectionSequenceAlignmentService).Assembly
                .GetType("Chatcheology.Data.Sequence.DirectionSequenceOutcomeCalculator");

            Assert.NotNull(calculator);
            Assert.False(calculator.IsPublic);
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static DirectionSequenceFrozenGateDeclaration Frozen(
            DirectionSequenceGateFixture fixture) =>
            fixture.AlignmentRequest().FrozenGate;

        private static void Refuses(
            DirectionSequenceGateFixture fixture, DirectionSequenceFrozenGateDeclaration frozen)
        {
            var failure = Assert.Throws<InvalidOperationException>(
                () => fixture.AnalyseAlignment(frozenGate: frozen));

            Assert.Contains("No alignment outcome", failure.Message, StringComparison.Ordinal);
        }

        private static DirectionSequenceAlignmentRequest Rebuild(
            DirectionSequenceAlignmentRequest request,
            DirectionSequenceStageB1Declaration stageB1) =>
            new()
            {
                DatabasePath = request.DatabasePath,
                ConversationID = request.ConversationID,
                LocalParticipantID = request.LocalParticipantID,
                DeviceGroups = request.DeviceGroups,
                StageATokenCoverage = request.StageATokenCoverage,
                StageB1 = stageB1,
                FrozenGate = request.FrozenGate,
            };

        /// <summary>
        /// A workspace with informative and determinate pairs, two sources whose scopes disagree about
        /// the pair universe, an unsupported name, an undated copy and neighbouring dates.
        /// </summary>
        private static DirectionSequenceGateFixture Populated()
        {
            var fixture = new DirectionSequenceGateFixture();
            var second = fixture.AddSource();

            // An informative pair: pattern OI against OOII, whose class holds OOII and IIOO and only one
            // of them admits.
            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddIncoming(First, "09:01:00");
            fixture.AddToken(First, 1, isSent: true);
            fixture.AddToken(First, 2, isSent: true);
            fixture.AddToken(First, 3, isSent: false);
            fixture.AddToken(First, 4, isSent: false);

            // A determinate pair on the next calendar date, so the first date has a neighbour.
            var next = DirectionSequenceGateFixture.Day(1);
            fixture.AddOutgoing(next, "09:00:00");
            fixture.AddIncoming(next, "09:01:00");
            fixture.AddToken(next, 1, isSent: true);
            fixture.AddToken(next, 2, isSent: false);
            fixture.AddToken(next, 3, isSent: true);
            fixture.AddToken(next, 4, isSent: false);

            // A date whose token side cannot supply its message side.
            var short_ = DirectionSequenceGateFixture.Day(3);
            fixture.AddOutgoing(short_, "09:00:00");
            fixture.AddOutgoing(short_, "09:01:00");
            fixture.AddToken(short_, 1, isSent: true);

            // A date with no token position at all.
            fixture.AddOutgoing(DirectionSequenceGateFixture.Day(5), "09:00:00");

            // Grammar and direction coverage loss, and a second source so the two scopes differ.
            fixture.AddNamed(First, "0010-1", isSent: true);
            fixture.AddToken(First, 9, isSent: null, mediaSourceID: second);
            fixture.AddUndated(isSent: true, mediaSourceID: second);

            return fixture;
        }
    }
}
