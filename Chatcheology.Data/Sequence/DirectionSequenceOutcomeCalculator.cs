using System.Diagnostics;
using System.Numerics;
using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Computes what the observed token order actually carried, over populations that were frozen before
    /// it was looked at.
    /// </summary>
    /// <remarks>
    /// Constructing one requires a <see cref="DirectionSequenceFrozenGateReconciliation"/>, which only
    /// the frozen-gate barrier can produce and which it produces only when every frozen target
    /// reconciles. So no outcome here can be reached before the freeze has been reproduced — that is a
    /// property of what can be constructed, not a rule about call order.
    /// <para>
    /// Nothing is read from the workspace. Every figure comes from the immutable snapshots the accepted
    /// gate handed over, so this stage cannot reinterpret the grammar, the collapse, the ordering or the
    /// exclusions even by accident: it does not have access to the tables they were derived from.
    /// </para>
    /// <para>
    /// Populations are selected from each pair's gate row, which was written before any outcome existed.
    /// An outcome cannot therefore filter a population, and a frozen count that has drifted stops the run
    /// rather than being reported smaller.
    /// </para>
    /// </remarks>
    internal sealed class DirectionSequenceOutcomeCalculator
    {
        /// <summary>The frozen population names, used as reported labels.</summary>
        private const string SupplyAssessed = "SupplyAssessed";

        private const string ClassifiedPairs = "ClassifiedPairs";

        private const string BinaryInformative = "BinaryInformative";

        private const string GradedInformative = "GradedInformative";

        /// <summary>
        /// The fewest pairs a descriptive cell may hold and still be printed.
        /// </summary>
        /// <remarks>
        /// Below this a cell is a private direction pattern in table form: a message length beside a
        /// composition beside a run count pins a short pattern down exactly, and the archive's median
        /// pattern is two symbols long.
        /// </remarks>
        private const int MinimumReportCellSize = 10;

        private readonly DirectionSequenceFrozenGateReconciliation _reconciliation;
        private readonly DirectionSequenceReferenceCache _cache;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();

        private readonly Dictionary<
            (ScopeLevel Scope, long ScopeKey, DateOnly Date), DirectionSequencePairSnapshot> _index;

        private readonly List<DirectionSequencePairSnapshot> _snapshots;

        internal DirectionSequenceOutcomeCalculator(
            DirectionSequenceFrozenGateReconciliation reconciliation,
            IReadOnlyList<DirectionSequencePairSnapshot> snapshots,
            DirectionSequenceReferenceCache? cache = null)
        {
            ArgumentNullException.ThrowIfNull(reconciliation);
            ArgumentNullException.ThrowIfNull(snapshots);

            _reconciliation = reconciliation;
            _cache = cache ?? new DirectionSequenceReferenceCache();
            _snapshots = [.. snapshots];

            _index = new Dictionary<
                (ScopeLevel, long, DateOnly), DirectionSequencePairSnapshot>(_snapshots.Count);

            foreach (var snapshot in _snapshots)
            {
                var key = (snapshot.Scope, snapshot.ScopeKey, snapshot.Date);

                if (!_index.TryAdd(key, snapshot))
                {
                    throw Fail(
                        "the gate offered two snapshots for one scope key and date, so a neighbouring " +
                        "date cannot be looked up unambiguously");
                }
            }
        }

        /// <summary>
        /// Runs every Stage B2C-1 computation and returns the census, or throws.
        /// </summary>
        /// <remarks>
        /// The census is constructed at the end and only then. A run that fails an internal
        /// reconciliation, or that is cancelled, returns nothing at all rather than a partial census that
        /// looks like a smaller result.
        /// </remarks>
        internal DirectionSequenceAlignmentCensus Compute(
            IProgress<DirectionSequenceAlignmentProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var primary = _reconciliation.PrimaryScope;

            var assessed = PopulationOf(primary, IsSupplyAssessed, SupplyAssessed);
            var classified = PopulationOf(primary, IsClassified, ClassifiedPairs);

            RequireCount(
                SupplyAssessed,
                assessed.Count,
                _reconciliation.Declaration.Scopes
                    .Single(scope => scope.Scope == primary)
                    .SupplyAssessedPairCount);

            RequireCount(
                ClassifiedPairs,
                classified.Count,
                _reconciliation.Declaration.ClassifiedPairCount);

            var supplyLoss = MeasureSupplyLoss(assessed, cancellationToken);

            var outcomes = MeasureOutcomes(
                classified, ClassifiedPairs, progress, cancellationToken);

            var orderLoss = MeasureOrderLoss(outcomes);
            var multiplicity = MeasureMultiplicity(outcomes);
            var attribution = MeasureFailureAttribution(outcomes, ClassifiedPairs);

            var gradedCheck = CheckGradedDeterminatePairs(outcomes);

            var binary = MeasureBinary(
                outcomes,
                BinaryInformative,
                _reconciliation.Declaration.BinaryInformativePairCount,
                _reconciliation.PrimaryScopeCensus.Reference
                    .SumOfConditionalAdmissionProbabilityOverInformative);

            var graded = MeasureGraded(
                outcomes,
                GradedInformative,
                _reconciliation.Declaration.GradedInformativePairCount,
                _reconciliation.PrimaryScopeCensus.Reference
                    .SumOfExpectedEmbeddingShareOverInformative);

            var reversal = MeasureReversal(outcomes, cancellationToken);

            var binaryControls = new List<DirectionSequenceBinaryAdjacentControl>();
            var gradedControls = new List<DirectionSequenceGradedAdjacentControl>();

            foreach (var offset in AdjacentOffsets)
            {
                binaryControls.Add(
                    MeasureBinaryAdjacentControl(outcomes, offset, progress, cancellationToken));

                gradedControls.Add(
                    MeasureGradedAdjacentControl(outcomes, offset, progress, cancellationToken));
            }

            var splits = MeasureDescriptiveSplits(outcomes);
            var sensitivity = MeasureScopeSensitivity(progress, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            return new DirectionSequenceAlignmentCensus
            {
                ConversationID = _reconciliation.Census.ConversationID,
                LocalParticipantID = _reconciliation.Census.LocalParticipantID,
                Reconciliation = _reconciliation,
                SupplyLoss = supplyLoss,
                OrderLoss = orderLoss,
                Multiplicity = multiplicity,
                FailureAttribution = attribution,
                Graded = graded,
                GradedDeterminateCheck = gradedCheck,
                Binary = binary,
                BinaryAdjacentControls = binaryControls,
                GradedAdjacentControls = gradedControls,
                Reversal = reversal,
                DescriptiveSplits = splits,
                ScopeSensitivity = sensitivity,
            };
        }

        private static readonly DirectionSequenceAdjacentOffset[] AdjacentOffsets =
            [DirectionSequenceAdjacentOffset.PreviousDay, DirectionSequenceAdjacentOffset.NextDay];

        // -------------------------------------------------------------------------------------------
        // Populations, selected from the gate's own row.
        // -------------------------------------------------------------------------------------------

        private static bool IsSupplyAssessed(DirectionSequencePairSnapshot snapshot) =>
            snapshot.Row is { State: not DirectionSequencePairState.ExcludedByDirectionConflict };

        private static bool IsClassified(DirectionSequencePairSnapshot snapshot) =>
            snapshot.Row is { State: DirectionSequencePairState.Classified };

        private List<DirectionSequencePairSnapshot> PopulationOf(
            ScopeLevel scope,
            Func<DirectionSequencePairSnapshot, bool> predicate,
            string name)
        {
            var population = _snapshots
                .Where(snapshot => snapshot.Scope == scope && predicate(snapshot))
                .ToList();

            if (population.Count == 0)
            {
                throw Fail($"the {name} population at scope {scope} is empty");
            }

            return population;
        }

        private static void RequireCount(string population, int observed, int frozen)
        {
            if (observed != frozen)
            {
                throw Fail(
                    $"the {population} population holds {observed} pair(s) and the freeze declares " +
                    $"{frozen}. A primary result computed over a population that has drifted is not the " +
                    $"result the freeze review authorised");
            }
        }

        // -------------------------------------------------------------------------------------------
        // C2 — supply and loss.
        // -------------------------------------------------------------------------------------------

        private DirectionSequenceSupplyLossCensus MeasureSupplyLoss(
            List<DirectionSequencePairSnapshot> assessed, CancellationToken cancellationToken)
        {
            var outgoing = new Dictionary<int, int>();
            var incoming = new Dictionary<int, int>();
            var losses = new Dictionary<int, int>();

            var zero = 0;
            var positive = 0;
            var messageObservations = 0;
            var tokenPositions = 0;
            var observationsInPositive = 0;

            foreach (var snapshot in assessed)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var row = snapshot.Row!;

                RequireMessageDirectionIdentity(row);

                var ceiling = DirectionSequenceAlignment.DirectionCountCeiling(
                    row.MessageOutgoingCount,
                    row.MessageIncomingCount,
                    row.TokenOutgoingCount,
                    row.TokenIncomingCount);

                var supplyLoss = row.MessageSymbolCount - ceiling;

                // The two forms of supply loss must agree. They do so only because every message symbol
                // carries a direction, which is why the identity above is asserted per pair.
                if (supplyLoss != row.OutgoingShortfall + row.IncomingShortfall)
                {
                    throw Fail(
                        $"a pair's supply loss is {supplyLoss} by the direction-count ceiling and " +
                        $"{row.OutgoingShortfall + row.IncomingShortfall} by its shortfalls. The two " +
                        $"forms of the same quantity disagree");
                }

                DirectionSequenceGateService.Increment(outgoing, row.OutgoingShortfall);
                DirectionSequenceGateService.Increment(incoming, row.IncomingShortfall);
                DirectionSequenceGateService.Increment(losses, supplyLoss);

                messageObservations += row.MessageSymbolCount;
                tokenPositions += row.TokenPositionCount;

                if (supplyLoss == 0)
                {
                    zero++;
                }
                else
                {
                    positive++;
                    observationsInPositive += row.MessageSymbolCount;
                }
            }

            return new DirectionSequenceSupplyLossCensus
            {
                PopulationName = SupplyAssessed,
                PairCount = assessed.Count,
                MessageObservationCount = messageObservations,
                TokenPositionCount = tokenPositions,
                ZeroSupplyLossPairCount = zero,
                PositiveSupplyLossPairCount = positive,
                MessageObservationsInPositiveSupplyLossPairs = observationsInPositive,
                OutgoingShortfallDistribution = DirectionSequenceGateService.Distribution(outgoing),
                IncomingShortfallDistribution = DirectionSequenceGateService.Distribution(incoming),
                SupplyLossDistribution = DirectionSequenceGateService.Distribution(losses),
            };
        }

        private static void RequireMessageDirectionIdentity(DirectionSequencePairRow row)
        {
            if (row.MessageOutgoingCount + row.MessageIncomingCount != row.MessageSymbolCount)
            {
                throw Fail(
                    $"a pair holds {row.MessageSymbolCount} message symbol(s) of which " +
                    $"{row.MessageOutgoingCount} are outgoing and {row.MessageIncomingCount} incoming. " +
                    $"A symbol carrying neither direction would break the supply decomposition");
            }
        }

        // -------------------------------------------------------------------------------------------
        // Own-date outcomes.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// The observed-order outcome of one pair against one token sequence.
        /// </summary>
        private sealed class PairOutcome
        {
            internal required DirectionSequencePairSnapshot Snapshot { get; init; }

            internal required DirectionSequencePairRow Row { get; init; }

            internal required int Alignment { get; init; }

            internal required BigInteger EmbeddingCount { get; init; }

            internal required double ObservedShareReading { get; init; }

            internal bool Admits => EmbeddingCount > BigInteger.Zero;

            internal int MessageLength => Row.MessageSymbolCount;

            internal int DirectionCountCeiling =>
                DirectionSequenceAlignment.DirectionCountCeiling(
                    Row.MessageOutgoingCount,
                    Row.MessageIncomingCount,
                    Row.TokenOutgoingCount,
                    Row.TokenIncomingCount);

            internal int SupplyLoss => MessageLength - DirectionCountCeiling;

            internal int OrderLoss => DirectionCountCeiling - Alignment;

            internal int TotalUnaligned => MessageLength - Alignment;
        }

        private List<PairOutcome> MeasureOutcomes(
            List<DirectionSequencePairSnapshot> population,
            string stage,
            IProgress<DirectionSequenceAlignmentProgress>? progress,
            CancellationToken cancellationToken)
        {
            var outcomes = new List<PairOutcome>(population.Count);

            foreach (var snapshot in population)
            {
                cancellationToken.ThrowIfCancellationRequested();

                outcomes.Add(OutcomeOf(snapshot, snapshot.EmittedTokens));

                Report(progress, stage, outcomes.Count, population.Count);
            }

            return outcomes;
        }

        private static PairOutcome OutcomeOf(
            DirectionSequencePairSnapshot snapshot, bool[] tokens)
        {
            var row = snapshot.Row!;

            var alignment = DirectionSequenceAlignment.LongestCommonSubsequenceLength(
                snapshot.Pattern, tokens);

            var embeddings = DirectionSequenceAlignment.MonotoneEmbeddingCount(
                snapshot.Pattern, tokens);

            // The two are computed independently, so their agreement is a check rather than a
            // restatement: a pattern embeds at all exactly when the ordered alignment carries all of it.
            if (embeddings > BigInteger.Zero != (alignment == snapshot.Pattern.Length))
            {
                throw Fail(
                    $"a pair's embedding count and ordered alignment disagree about whether its whole " +
                    $"pattern is carried: {embeddings} embedding(s) against an alignment of " +
                    $"{alignment} for {snapshot.Pattern.Length} symbol(s)");
            }

            var choices = DirectionSequenceReference.Binomial(tokens.Length, snapshot.Pattern.Length);

            if (embeddings > choices)
            {
                throw Fail(
                    $"a pair admits {embeddings} embedding(s) of {choices} possible choices of " +
                    $"positions, which would make its share exceed one");
            }

            return new PairOutcome
            {
                Snapshot = snapshot,
                Row = row,
                Alignment = alignment,
                EmbeddingCount = embeddings,

                // The same conversion the accepted gate uses for its expected readings, so an observed
                // total and an expected total are commensurable and their difference carries no
                // conversion asymmetry.
                ObservedShareReading =
                    DirectionSequenceGateService.DecimalReading(embeddings, choices),
            };
        }

        // -------------------------------------------------------------------------------------------
        // C3 — order loss.
        // -------------------------------------------------------------------------------------------

        private static DirectionSequenceOrderLossCensus MeasureOrderLoss(List<PairOutcome> outcomes)
        {
            var losses = new Dictionary<int, int>();
            var alignments = new List<int>();
            var lengths = new List<int>();

            var zero = 0;
            var positive = 0;
            var supplyLossPairs = 0;
            var messageObservations = 0;
            var tokenPositions = 0;

            foreach (var outcome in outcomes)
            {
                if (outcome.Alignment < 0 || outcome.Alignment > outcome.DirectionCountCeiling)
                {
                    throw Fail(
                        $"a pair's ordered alignment of {outcome.Alignment} is outside its " +
                        $"direction-count ceiling of {outcome.DirectionCountCeiling}");
                }

                if (outcome.TotalUnaligned != outcome.SupplyLoss + outcome.OrderLoss)
                {
                    throw Fail(
                        $"a pair leaves {outcome.TotalUnaligned} symbol(s) unaligned, which is not its " +
                        $"supply loss of {outcome.SupplyLoss} plus its order loss of " +
                        $"{outcome.OrderLoss}");
                }

                if (outcome.SupplyLoss != 0)
                {
                    supplyLossPairs++;
                }

                DirectionSequenceGateService.Increment(losses, outcome.OrderLoss);
                alignments.Add(outcome.Alignment);
                lengths.Add(outcome.MessageLength);

                messageObservations += outcome.MessageLength;
                tokenPositions += outcome.Row.TokenPositionCount;

                if (outcome.OrderLoss == 0)
                {
                    zero++;
                }
                else
                {
                    positive++;
                }
            }

            // The classified population is supply-sufficient by construction, so a positive supply loss
            // here means the population is not the one it claims to be.
            if (supplyLossPairs != 0)
            {
                throw Fail(
                    $"{supplyLossPairs} pair(s) of the order-testable population are short of supply. " +
                    $"That population is supply-sufficient by construction, so it has drifted");
            }

            return new DirectionSequenceOrderLossCensus
            {
                PopulationName = ClassifiedPairs,
                PairCount = outcomes.Count,
                MessageObservationCount = messageObservations,
                TokenPositionCount = tokenPositions,
                SupplyLossPairCount = supplyLossPairs,
                ZeroOrderLossPairCount = zero,
                PositiveOrderLossPairCount = positive,
                OrderLossDistribution = DirectionSequenceGateService.Distribution(losses),
                OrderedDirectionAlignment = DirectionSequenceGateService.Summarise(alignments),
                MessageLength = DirectionSequenceGateService.Summarise(lengths),
            };
        }

        // -------------------------------------------------------------------------------------------
        // C4 — multiplicity, and the reconciliation determinacy forces.
        // -------------------------------------------------------------------------------------------

        private DirectionSequenceMultiplicityCensus MeasureMultiplicity(List<PairOutcome> outcomes)
        {
            var none = 0;
            var one = 0;
            var several = 0;

            var determinateAdmissions = 0;
            var determinatePairs = 0;
            var informativeAdmissions = 0;
            var informativePairs = 0;

            foreach (var outcome in outcomes)
            {
                if (outcome.EmbeddingCount.IsZero)
                {
                    none++;
                }
                else if (outcome.EmbeddingCount.IsOne)
                {
                    one++;
                }
                else
                {
                    several++;
                }

                if (outcome.Row.BinaryClass == DirectionSequenceDeterminacyClass.Determinate)
                {
                    determinatePairs++;

                    if (outcome.Admits)
                    {
                        determinateAdmissions++;
                    }
                }
                else
                {
                    informativePairs++;

                    if (outcome.Admits)
                    {
                        informativeAdmissions++;
                    }
                }
            }

            // Every binary-determinate pair of the frozen population has q_r = 1: every arrangement of
            // its own reference class admits its pattern, and the observed order is one of those
            // arrangements. So all of them must admit, before the run and whatever it finds.
            if (determinateAdmissions != determinatePairs)
            {
                throw Fail(
                    $"{determinateAdmissions} of {determinatePairs} binary-determinate pair(s) admit " +
                    $"their pattern. Determinacy means every arrangement of the class admits it and the " +
                    $"observed order is one of them, so all of them must");
            }

            var admissions = determinateAdmissions + informativeAdmissions;

            if (admissions != outcomes.Count - none)
            {
                throw Fail(
                    $"{admissions} pair(s) admit their pattern and {outcomes.Count - none} have a " +
                    $"non-zero embedding count. The multiplicity census and the admission count " +
                    $"disagree");
            }

            if (none != informativePairs - informativeAdmissions)
            {
                throw Fail(
                    $"{none} pair(s) carry no embedding, which is not the " +
                    $"{informativePairs - informativeAdmissions} binary-informative pair(s) that did " +
                    $"not admit. Only an informative pair can fail to admit");
            }

            RequireCount(
                "binary-determinate pairs",
                determinatePairs,
                _reconciliation.Declaration.BinaryDeterminatePairCount);

            RequireCount(
                BinaryInformative,
                informativePairs,
                _reconciliation.Declaration.BinaryInformativePairCount);

            return new DirectionSequenceMultiplicityCensus
            {
                PopulationName = ClassifiedPairs,
                PairCount = outcomes.Count,
                NoExactMonotoneDirectionAlignment = none,
                OneExactMonotoneDirectionAlignment = one,
                SeveralExactMonotoneDirectionAlignments = several,
                PairsWithValidEmbedding = admissions,
                ForcedDeterminateAdmissionCount = determinateAdmissions,
                BinaryDeterminatePairCount = determinatePairs,
                InformativeAdmissionCount = informativeAdmissions,
                BinaryInformativePairCount = informativePairs,
            };
        }

        // -------------------------------------------------------------------------------------------
        // Failure attribution.
        // -------------------------------------------------------------------------------------------

        private static DirectionSequenceFailureAttributionCensus MeasureFailureAttribution(
            List<PairOutcome> outcomes, string population)
        {
            var failed = 0;
            var notAttributable = 0;
            var complete = 0;
            var grammarOnly = 0;
            var directionOnly = 0;
            var both = 0;
            var pairsWithCompleteCoverage = 0;

            foreach (var outcome in outcomes)
            {
                var grammarIncomplete = outcome.Snapshot.UnsupportedTokenObservationCount > 0;
                var directionIncomplete = outcome.Snapshot.UnlabelledPositionCount > 0;

                if (!grammarIncomplete && !directionIncomplete)
                {
                    pairsWithCompleteCoverage++;
                }

                if (outcome.Admits)
                {
                    continue;
                }

                failed++;

                if (grammarIncomplete && directionIncomplete)
                {
                    both++;
                    notAttributable++;
                }
                else if (grammarIncomplete)
                {
                    grammarOnly++;
                    notAttributable++;
                }
                else if (directionIncomplete)
                {
                    directionOnly++;
                    notAttributable++;
                }
                else
                {
                    complete++;
                }
            }

            return new DirectionSequenceFailureAttributionCensus
            {
                PopulationName = population,
                PairCount = outcomes.Count,
                FailedEmbeddingCount = failed,
                FailureNotAttributable = notAttributable,
                FailureWithCompleteTokenCoverage = complete,
                FailuresWithGrammarCoverageLossOnly = grammarOnly,
                FailuresWithDirectionCoverageLossOnly = directionOnly,
                FailuresWithBothCoverageLosses = both,
                PairsWithCompleteTokenCoverage = pairsWithCompleteCoverage,
            };
        }

        // -------------------------------------------------------------------------------------------
        // The graded-determinate validation.
        // -------------------------------------------------------------------------------------------

        /// <remarks>
        /// A graded-determinate pair has zero share variance across its reference class, so every
        /// arrangement of the class — the observed one included — carries the identical share. Both
        /// readings come from the same conversion of the same exact rational, so this is an equality and
        /// not a tolerance, and it validates the whole observed-share path on real data without touching
        /// the population that carries the primary graded figure.
        /// </remarks>
        private DirectionSequenceGradedDeterminateCheck CheckGradedDeterminatePairs(
            List<PairOutcome> outcomes)
        {
            var pairs = 0;
            var identical = 0;
            var observed = 0d;
            var expected = 0d;

            foreach (var outcome in outcomes)
            {
                if (outcome.Row.GradedClass != DirectionSequenceDeterminacyClass.Determinate)
                {
                    continue;
                }

                pairs++;
                observed += outcome.ObservedShareReading;
                expected += outcome.Row.ExpectedEmbeddingShare;

                if (outcome.ObservedShareReading == outcome.Row.ExpectedEmbeddingShare)
                {
                    identical++;

                    continue;
                }

                throw Fail(
                    $"a graded-determinate pair's observed share reads " +
                    $"{outcome.ObservedShareReading} and its expected share reads " +
                    $"{outcome.Row.ExpectedEmbeddingShare}. Graded determinacy is zero share variance " +
                    $"across the class, so the observed arrangement must carry exactly the expected " +
                    $"share");
            }

            RequireCount(
                "graded-determinate pairs",
                pairs,
                _reconciliation.Declaration.GradedDeterminatePairCount);

            return new DirectionSequenceGradedDeterminateCheck
            {
                PairCount = pairs,
                ReadingsIdenticalPairCount = identical,
                SumOfObservedNormalisedEmbeddingShare = observed,
                SumOfExpectedEmbeddingShare = expected,
            };
        }

        // -------------------------------------------------------------------------------------------
        // C5 and C4 — the two co-primary comparisons.
        // -------------------------------------------------------------------------------------------

        /// <remarks>
        /// <paramref name="frozenCount"/> is null for a sensitivity population, which follows the frozen
        /// <em>rules</em> and has its own size rather than a frozen one. A primary population always has
        /// a frozen count, and drift in it stops the run.
        /// </remarks>
        private static DirectionSequenceBinaryFigures MeasureBinary(
            List<PairOutcome> outcomes, string population, int? frozenCount, double frozenExpectation)
        {
            var selected = outcomes
                .Where(
                    outcome => outcome.Row.BinaryClass
                        == DirectionSequenceDeterminacyClass.Informative)
                .ToList();

            if (frozenCount is { } frozen)
            {
                RequireCount(population, selected.Count, frozen);
            }

            var admissions = selected.Count(outcome => outcome.Admits);

            var expectation = 0d;

            foreach (var outcome in selected)
            {
                expectation += outcome.Row.ConditionalAdmissionProbability;
            }

            // Re-summed from the pair rows and required to render identically to the figure the barrier
            // reconciled. The same values in the same order give the same total, so a difference here
            // means the population summed is not the population frozen.
            RequireReadingAgreement(
                $"{population} conditional expectation", expectation, frozenExpectation);

            return new DirectionSequenceBinaryFigures
            {
                PopulationName = population,
                PairCount = selected.Count,
                ObservedAdmissions = admissions,
                SumOfConditionalAdmissionProbability = frozenExpectation,
                ObservedMinusExpected = admissions - frozenExpectation,
            };
        }

        /// <remarks>Null <paramref name="frozenCount"/> as in <see cref="MeasureBinary"/>.</remarks>
        private static DirectionSequenceGradedFigures MeasureGraded(
            List<PairOutcome> outcomes, string population, int? frozenCount, double frozenExpectation)
        {
            var selected = outcomes
                .Where(
                    outcome => outcome.Row.GradedClass
                        == DirectionSequenceDeterminacyClass.Informative)
                .ToList();

            if (frozenCount is { } frozen)
            {
                RequireCount(population, selected.Count, frozen);
            }

            var observed = 0d;
            var expectation = 0d;

            foreach (var outcome in selected)
            {
                observed += outcome.ObservedShareReading;
                expectation += outcome.Row.ExpectedEmbeddingShare;
            }

            RequireReadingAgreement(
                $"{population} expected share", expectation, frozenExpectation);

            return new DirectionSequenceGradedFigures
            {
                PopulationName = population,
                PairCount = selected.Count,
                SumOfObservedNormalisedEmbeddingShare = observed,
                SumOfExpectedEmbeddingShare = frozenExpectation,
                ObservedMinusExpected = observed - frozenExpectation,
            };
        }

        private static void RequireReadingAgreement(string figure, double resummed, double frozen)
        {
            var left = DirectionSequenceFrozenGateBarrier.Render(resummed);
            var right = DirectionSequenceFrozenGateBarrier.Render(frozen);

            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                throw Fail(
                    $"the {figure} re-summed from the pair rows reads {left} and the reconciled gate " +
                    $"figure reads {right}. The population an outcome was computed over is not the " +
                    $"population the reference was summed over");
            }
        }

        // -------------------------------------------------------------------------------------------
        // C5c — reversal.
        // -------------------------------------------------------------------------------------------

        private DirectionSequenceReversalCensus MeasureReversal(
            List<PairOutcome> outcomes, CancellationToken cancellationToken)
        {
            var palindromes = 0;
            var forcedBothAdmit = 0;
            var forcedGradedTie = 0;

            var actualOnly = 0;
            var bothAdmit = 0;
            var neither = 0;
            var reversedOnly = 0;

            var alignmentActual = 0;
            var alignmentTie = 0;
            var alignmentReversed = 0;

            var shareActual = 0;
            var shareTie = 0;
            var shareReversed = 0;
            var sharePairs = 0;
            var admissionPairs = 0;

            foreach (var outcome in outcomes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (DirectionSequenceAlignment.IsPalindrome(outcome.Snapshot.Pattern))
                {
                    palindromes++;
                }

                var reversed = DirectionSequenceAlignment.Reversed(outcome.Snapshot.EmittedTokens);

                var reversedAlignment = DirectionSequenceAlignment.LongestCommonSubsequenceLength(
                    outcome.Snapshot.Pattern, reversed);

                var reversedEmbeddings = DirectionSequenceAlignment.MonotoneEmbeddingCount(
                    outcome.Snapshot.Pattern, reversed);

                if (outcome.Alignment > reversedAlignment)
                {
                    alignmentActual++;
                }
                else if (outcome.Alignment == reversedAlignment)
                {
                    alignmentTie++;
                }
                else
                {
                    alignmentReversed++;
                }

                var reversedAdmits = reversedEmbeddings > BigInteger.Zero;

                if (outcome.Row.BinaryClass == DirectionSequenceDeterminacyClass.Determinate)
                {
                    // Both orders belong to the same reference class, and every arrangement of a
                    // determinate class with q_r = 1 admits. Counted as forced rather than observed.
                    if (!outcome.Admits || !reversedAdmits)
                    {
                        throw Fail(
                            "a binary-determinate pair does not admit its pattern under both the " +
                            "observed order and its reverse. Reversal preserves the composition and the " +
                            "run count, so both orders are members of the same class and both must " +
                            "admit");
                    }

                    forcedBothAdmit++;
                }
                else
                {
                    admissionPairs++;

                    if (outcome.Admits && reversedAdmits)
                    {
                        bothAdmit++;
                    }
                    else if (outcome.Admits)
                    {
                        actualOnly++;
                    }
                    else if (reversedAdmits)
                    {
                        reversedOnly++;
                    }
                    else
                    {
                        neither++;
                    }
                }

                if (outcome.Row.GradedClass == DirectionSequenceDeterminacyClass.Determinate)
                {
                    forcedGradedTie++;

                    continue;
                }

                sharePairs++;

                var choices = DirectionSequenceReference.Binomial(
                    outcome.Snapshot.EmittedTokens.Length, outcome.MessageLength);

                var reversedShare =
                    DirectionSequenceGateService.DecimalReading(reversedEmbeddings, choices);

                if (outcome.ObservedShareReading > reversedShare)
                {
                    shareActual++;
                }
                else if (outcome.ObservedShareReading == reversedShare)
                {
                    shareTie++;
                }
                else
                {
                    shareReversed++;
                }
            }

            RequireCount(
                "reversal forced both-admit pairs",
                forcedBothAdmit,
                _reconciliation.Declaration.BinaryDeterminatePairCount);

            RequireCount(
                "reversal forced graded ties",
                forcedGradedTie,
                _reconciliation.Declaration.GradedDeterminatePairCount);

            return new DirectionSequenceReversalCensus
            {
                PopulationName = ClassifiedPairs,
                PairCount = outcomes.Count,
                PalindromicPatternPairCount = palindromes,
                ForcedBothAdmitPairCount = forcedBothAdmit,
                ForcedGradedTiePairCount = forcedGradedTie,
                VariableAdmissionPairCount = admissionPairs,
                VariableAdmission = new DirectionSequenceAdmissionContingency
                {
                    ActualOnlyAdmits = actualOnly,
                    BothAdmit = bothAdmit,
                    NeitherAdmits = neither,
                    ReversedOnlyAdmits = reversedOnly,
                },
                AlignmentComparison = new DirectionSequenceOrderComparisonCounts
                {
                    ActualOrderBetter = alignmentActual,
                    Tie = alignmentTie,
                    ReversedOrderBetter = alignmentReversed,
                },
                VariableSharePairCount = sharePairs,
                VariableShareComparison = new DirectionSequenceOrderComparisonCounts
                {
                    ActualOrderBetter = shareActual,
                    Tie = shareTie,
                    ReversedOrderBetter = shareReversed,
                },
            };
        }

        // -------------------------------------------------------------------------------------------
        // C5b — the adjacent-date controls.
        // -------------------------------------------------------------------------------------------

        /// <summary>Why a pair is absent from an intersection.</summary>
        private enum ShiftDrop
        {
            None,
            NeighbourDateAbsent,
            NeighbourTokenPopulationEmpty,
            NeighbourDateConflict,
            SupplyInsufficient,
            Degenerate,
            Determinate,
        }

        /// <summary>One pair's message pattern measured against a neighbouring date's token order.</summary>
        private sealed class ShiftedComparison
        {
            internal required double ConditionalAdmissionProbability { get; init; }

            internal required double ExpectedEmbeddingShare { get; init; }

            internal required double ObservedShareReading { get; init; }

            internal required bool Admits { get; init; }
        }

        /// <remarks>
        /// The shifted comparison carries its own everything: the neighbour's composition, its run count,
        /// its supply state, its degeneracy, its determinacy and its own <c>q_r</c> and
        /// <c>E_r[share]</c>, all computed from the neighbour's tokens against this pair's pattern.
        /// Reusing the correct date's conditioning would compare an observed order against a reference
        /// describing a different token side, which is the one mistake this control cannot survive.
        /// </remarks>
        private ShiftedComparison? Shift(
            PairOutcome own, DirectionSequenceAdjacentOffset offset, bool graded, out ShiftDrop drop)
        {
            drop = ShiftDrop.None;

            var days = (int)offset;

            // The immediately previous or next calendar date, never the nearest populated one. A date
            // outside the calendar's range is simply not there.
            if ((days < 0 && own.Snapshot.Date == DateOnly.MinValue)
                || (days > 0 && own.Snapshot.Date == DateOnly.MaxValue))
            {
                drop = ShiftDrop.NeighbourDateAbsent;

                return null;
            }

            var neighbourDate = own.Snapshot.Date.AddDays(days);

            if (!_index.TryGetValue(
                (own.Snapshot.Scope, own.Snapshot.ScopeKey, neighbourDate), out var neighbour))
            {
                drop = ShiftDrop.NeighbourDateAbsent;

                return null;
            }

            // Exclusion is owned by the date it belongs to. The own-date pair's eligibility says nothing
            // about the neighbour's, and the neighbour's conflict excludes the comparison rather than
            // the pair.
            if (neighbour.ConflictingPositionCount > 0)
            {
                drop = ShiftDrop.NeighbourDateConflict;

                return null;
            }

            var tokens = neighbour.EmittedTokens;

            if (tokens.Length == 0)
            {
                drop = ShiftDrop.NeighbourTokenPopulationEmpty;

                return null;
            }

            var outgoing = DirectionSequenceReference.CountOutgoing(tokens);
            var incoming = tokens.Length - outgoing;
            var runs = DirectionSequenceReference.RunCount(tokens);

            if (own.Row.MessageOutgoingCount > outgoing || own.Row.MessageIncomingCount > incoming)
            {
                drop = ShiftDrop.SupplyInsufficient;

                return null;
            }

            // Degeneracy that does not need a reference at all is decided first, so a pair order can
            // never depend on the expensive quantities being computed for a pair that was not testable.
            if (outgoing == 0 || incoming == 0 || own.MessageLength == 1)
            {
                drop = ShiftDrop.Degenerate;

                return null;
            }

            // The binary rule mentions the class size and the admitting count and nothing else, so a
            // binary comparison never pays for the squared-embedding count, which is the most expensive
            // quantity in the stage.
            var admission = _cache.ForAdmission(own.Snapshot.Pattern, outgoing, incoming, runs);

            if (admission.ArrangementCount.IsZero)
            {
                throw Fail(
                    $"a reference class of {outgoing} outgoing and {incoming} incoming positions in " +
                    $"{runs} run(s) holds no arrangement, yet a neighbouring date's token sequence has " +
                    $"exactly those properties");
            }

            if (admission.ArrangementCount.IsOne)
            {
                drop = ShiftDrop.Degenerate;

                return null;
            }

            var choices = DirectionSequenceReference.Binomial(tokens.Length, own.MessageLength);

            var expectedShare = 0d;

            if (graded)
            {
                var values = _cache.ForShare(own.Snapshot.Pattern, outgoing, incoming, runs);

                if (values.ArrangementCount * values.SquaredEmbeddingCount
                    < values.EmbeddingPairCount * values.EmbeddingPairCount)
                {
                    throw Fail(
                        "a shifted reference violates A * Q >= P * P, which holds for every class by " +
                        "Cauchy-Schwarz, so one of its three counts is wrong");
                }

                if (!values.BinaryDeterminate && values.GradedDeterminate)
                {
                    throw Fail(
                        "a shifted comparison is binary-informative and graded-determinate. Graded " +
                        "determinacy implies binary determinacy, so one of the two classifications is " +
                        "wrong");
                }

                if (values.GradedDeterminate)
                {
                    drop = ShiftDrop.Determinate;

                    return null;
                }

                expectedShare = DirectionSequenceGateService.DecimalReading(
                    values.EmbeddingPairCount, values.ArrangementCount * choices);
            }
            else if (admission.Determinate)
            {
                drop = ShiftDrop.Determinate;

                return null;
            }

            var embeddings = DirectionSequenceAlignment.MonotoneEmbeddingCount(
                own.Snapshot.Pattern, tokens);

            return new ShiftedComparison
            {
                ConditionalAdmissionProbability = DirectionSequenceGateService.DecimalReading(
                    admission.AdmittingArrangementCount, admission.ArrangementCount),
                ExpectedEmbeddingShare = expectedShare,
                ObservedShareReading =
                    DirectionSequenceGateService.DecimalReading(embeddings, choices),
                Admits = embeddings > BigInteger.Zero,
            };
        }

        private DirectionSequenceBinaryAdjacentControl MeasureBinaryAdjacentControl(
            List<PairOutcome> outcomes,
            DirectionSequenceAdjacentOffset offset,
            IProgress<DirectionSequenceAlignmentProgress>? progress,
            CancellationToken cancellationToken)
        {
            var eligible = outcomes
                .Where(
                    outcome => outcome.Row.BinaryClass
                        == DirectionSequenceDeterminacyClass.Informative)
                .ToList();

            var drops = new Dictionary<ShiftDrop, int>();
            var paired = new List<(PairOutcome Own, ShiftedComparison Shifted)>();

            foreach (var outcome in eligible)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var shifted = Shift(outcome, offset, graded: false, out var drop);

                if (shifted is null)
                {
                    Count(drops, drop);
                }
                else
                {
                    paired.Add((outcome, shifted));
                }

                Report(progress, $"binary control {offset}", paired.Count, eligible.Count);
            }

            var correctAdmissions = 0;
            var correctExpectation = 0d;
            var shiftedAdmissions = 0;
            var shiftedExpectation = 0d;

            foreach (var (own, shifted) in paired)
            {
                if (own.Admits)
                {
                    correctAdmissions++;
                }

                correctExpectation += own.Row.ConditionalAdmissionProbability;

                if (shifted.Admits)
                {
                    shiftedAdmissions++;
                }

                shiftedExpectation += shifted.ConditionalAdmissionProbability;
            }

            var accounting = AccountingOf(eligible.Count, paired.Count, drops);

            return new DirectionSequenceBinaryAdjacentControl
            {
                Offset = offset,
                Accounting = accounting,
                CorrectDate = new DirectionSequenceBinaryFigures
                {
                    PopulationName = $"{BinaryInformative} paired intersection {offset}",
                    PairCount = paired.Count,
                    ObservedAdmissions = correctAdmissions,
                    SumOfConditionalAdmissionProbability = correctExpectation,
                    ObservedMinusExpected = correctAdmissions - correctExpectation,
                },
                ShiftedDate = new DirectionSequenceBinaryFigures
                {
                    PopulationName = $"{BinaryInformative} shifted intersection {offset}",
                    PairCount = paired.Count,
                    ObservedAdmissions = shiftedAdmissions,
                    SumOfConditionalAdmissionProbability = shiftedExpectation,
                    ObservedMinusExpected = shiftedAdmissions - shiftedExpectation,
                },
                Composition = CompositionOf(
                    eligible.Select(outcome => outcome.Row),
                    paired.Select(pair => pair.Own.Row)),
            };
        }

        private DirectionSequenceGradedAdjacentControl MeasureGradedAdjacentControl(
            List<PairOutcome> outcomes,
            DirectionSequenceAdjacentOffset offset,
            IProgress<DirectionSequenceAlignmentProgress>? progress,
            CancellationToken cancellationToken)
        {
            var eligible = outcomes
                .Where(
                    outcome => outcome.Row.GradedClass
                        == DirectionSequenceDeterminacyClass.Informative)
                .ToList();

            var drops = new Dictionary<ShiftDrop, int>();
            var paired = new List<(PairOutcome Own, ShiftedComparison Shifted)>();

            foreach (var outcome in eligible)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var shifted = Shift(outcome, offset, graded: true, out var drop);

                if (shifted is null)
                {
                    Count(drops, drop);
                }
                else
                {
                    paired.Add((outcome, shifted));
                }

                Report(progress, $"graded control {offset}", paired.Count, eligible.Count);
            }

            var correctObserved = 0d;
            var correctExpectation = 0d;
            var shiftedObserved = 0d;
            var shiftedExpectation = 0d;

            foreach (var (own, shifted) in paired)
            {
                correctObserved += own.ObservedShareReading;
                correctExpectation += own.Row.ExpectedEmbeddingShare;
                shiftedObserved += shifted.ObservedShareReading;
                shiftedExpectation += shifted.ExpectedEmbeddingShare;
            }

            return new DirectionSequenceGradedAdjacentControl
            {
                Offset = offset,
                Accounting = AccountingOf(eligible.Count, paired.Count, drops),
                CorrectDate = new DirectionSequenceGradedFigures
                {
                    PopulationName = $"{GradedInformative} paired intersection {offset}",
                    PairCount = paired.Count,
                    SumOfObservedNormalisedEmbeddingShare = correctObserved,
                    SumOfExpectedEmbeddingShare = correctExpectation,
                    ObservedMinusExpected = correctObserved - correctExpectation,
                },
                ShiftedDate = new DirectionSequenceGradedFigures
                {
                    PopulationName = $"{GradedInformative} shifted intersection {offset}",
                    PairCount = paired.Count,
                    SumOfObservedNormalisedEmbeddingShare = shiftedObserved,
                    SumOfExpectedEmbeddingShare = shiftedExpectation,
                    ObservedMinusExpected = shiftedObserved - shiftedExpectation,
                },
                Composition = CompositionOf(
                    eligible.Select(outcome => outcome.Row),
                    paired.Select(pair => pair.Own.Row)),
            };
        }

        /// <summary>The drop counts keyed by reason, with every eligible pair accounted for.</summary>
        private static DirectionSequenceIntersectionAccounting AccountingOf(
            int eligible, int intersection, Dictionary<ShiftDrop, int> drops)
        {
            var accounting = new DirectionSequenceIntersectionAccounting
            {
                OwnDateEligiblePairCount = eligible,
                IntersectionPairCount = intersection,
                DroppedNeighbourDateAbsent = drops.GetValueOrDefault(ShiftDrop.NeighbourDateAbsent),
                DroppedNeighbourTokenPopulationEmpty =
                    drops.GetValueOrDefault(ShiftDrop.NeighbourTokenPopulationEmpty),
                DroppedNeighbourDateConflict =
                    drops.GetValueOrDefault(ShiftDrop.NeighbourDateConflict),
                DroppedShiftedSupplyInsufficient =
                    drops.GetValueOrDefault(ShiftDrop.SupplyInsufficient),
                DroppedShiftedDegenerate = drops.GetValueOrDefault(ShiftDrop.Degenerate),
                DroppedShiftedDeterminate = drops.GetValueOrDefault(ShiftDrop.Determinate),
            };

            if (accounting.AccountedTotal != eligible)
            {
                throw Fail(
                    $"an adjacent-date intersection accounts for {accounting.AccountedTotal} of " +
                    $"{eligible} eligible pair(s). Every pair not in the intersection must be explained " +
                    $"by a reason");
            }

            return accounting;
        }

        private static void Count(Dictionary<ShiftDrop, int> drops, ShiftDrop drop) =>
            drops[drop] = drops.GetValueOrDefault(drop) + 1;

        // -------------------------------------------------------------------------------------------
        // Predeclared coarse composition.
        // -------------------------------------------------------------------------------------------

        private static readonly string[] CoarseLengthBands = ["1-2", "3-5", "6+"];

        private static readonly string[] CoarseQrBands = ["0<q_r<=0.75", "0.75<q_r<1", "q_r=1"];

        private static string CoarseLengthBandOf(int length) =>
            length <= 2 ? CoarseLengthBands[0] : length <= 5 ? CoarseLengthBands[1] : CoarseLengthBands[2];

        /// <remarks>
        /// Exact unions of the frozen descriptive bands, so no edge here was invented for this table and
        /// the endpoint at three quarters falls on the side the frozen band puts it. The band itself is
        /// decided by integer comparison on the exact reference counts, never on a decimal.
        /// </remarks>
        private static string CoarseQrBandOf(DirectionSequencePairRow row) =>
            DirectionSequenceGateService.BandOf(
                row.AdmittingArrangementCount, row.ArrangementCount) switch
            {
                DirectionSequenceQrBand.ExactlyOne => CoarseQrBands[2],
                DirectionSequenceQrBand.AboveThreeQuartersToNinetyFiveHundredths
                    or DirectionSequenceQrBand.AboveNinetyFiveHundredthsBelowOne =>
                    CoarseQrBands[1],
                _ => CoarseQrBands[0],
            };

        private static DirectionSequenceIntersectionComposition CompositionOf(
            IEnumerable<DirectionSequencePairRow> population,
            IEnumerable<DirectionSequencePairRow> intersection)
        {
            var suppressed = 0;

            var full = CoarseCellsOf(population, ref suppressed);
            var kept = CoarseCellsOf(intersection, ref suppressed);

            return new DirectionSequenceIntersectionComposition
            {
                FullPopulation = full,
                Intersection = kept,
                SuppressedCellCount = suppressed,
            };
        }

        private static List<DirectionSequenceCoarseCell> CoarseCellsOf(
            IEnumerable<DirectionSequencePairRow> rows, ref int suppressed)
        {
            var lengths = new Dictionary<string, int>();
            var bands = new Dictionary<string, int>();

            foreach (var row in rows)
            {
                var length = CoarseLengthBandOf(row.MessageSymbolCount);
                var band = CoarseQrBandOf(row);

                lengths[length] = lengths.GetValueOrDefault(length) + 1;
                bands[band] = bands.GetValueOrDefault(band) + 1;
            }

            var cells = new List<DirectionSequenceCoarseCell>();

            foreach (var band in CoarseLengthBands)
            {
                cells.Add(CellOf("MessageLength", band, lengths.GetValueOrDefault(band), ref suppressed));
            }

            foreach (var band in CoarseQrBands)
            {
                cells.Add(CellOf("q_r", band, bands.GetValueOrDefault(band), ref suppressed));
            }

            return cells;
        }

        private static DirectionSequenceCoarseCell CellOf(
            string dimension, string band, int count, ref int suppressed)
        {
            // An empty cell is not a disclosure — there is nothing in it to reconstruct — so only a
            // non-empty cell below the floor is withheld.
            var withhold = count > 0 && count < MinimumReportCellSize;

            if (withhold)
            {
                suppressed++;
            }

            return new DirectionSequenceCoarseCell
            {
                Dimension = dimension,
                Band = band,
                PairCount = withhold ? 0 : count,
                Suppressed = withhold,
            };
        }

        // -------------------------------------------------------------------------------------------
        // C6 — descriptive splits.
        // -------------------------------------------------------------------------------------------

        private static readonly string[] LengthBands =
            ["1", "2", "3-5", "6-10", "11-25", "26+"];

        private static string LengthBandOf(int length) => length switch
        {
            <= 1 => LengthBands[0],
            2 => LengthBands[1],
            <= 5 => LengthBands[2],
            <= 10 => LengthBands[3],
            <= 25 => LengthBands[4],
            _ => LengthBands[5],
        };

        private List<DirectionSequenceDescriptiveSplit> MeasureDescriptiveSplits(
            List<PairOutcome> outcomes)
        {
            var informative = outcomes
                .Where(
                    outcome => outcome.Row.BinaryClass
                        == DirectionSequenceDeterminacyClass.Informative)
                .ToList();

            return
            [
                SplitOf(
                    informative,
                    "q_r band",
                    Enum.GetValues<DirectionSequenceQrBand>().Select(band => band.ToString()).ToArray(),
                    outcome => DirectionSequenceGateService.BandOf(
                        outcome.Row.AdmittingArrangementCount,
                        outcome.Row.ArrangementCount).ToString()),

                SplitOf(
                    informative,
                    "message length band",
                    LengthBands,
                    outcome => LengthBandOf(outcome.MessageLength)),
            ];
        }

        private static DirectionSequenceDescriptiveSplit SplitOf(
            List<PairOutcome> population,
            string dimension,
            IReadOnlyList<string> bands,
            Func<PairOutcome, string> bandOf)
        {
            var grouped = new Dictionary<string, List<PairOutcome>>();

            foreach (var outcome in population)
            {
                var band = bandOf(outcome);

                if (!grouped.TryGetValue(band, out var members))
                {
                    members = [];
                    grouped[band] = members;
                }

                members.Add(outcome);
            }

            var cells = new List<DirectionSequenceDescriptiveCell>();
            var suppressedCells = 0;
            var suppressedPairs = 0;

            foreach (var band in bands)
            {
                var members = grouped.GetValueOrDefault(band) ?? [];
                var withhold = members.Count > 0 && members.Count < MinimumReportCellSize;

                if (withhold)
                {
                    suppressedCells++;
                    suppressedPairs += members.Count;
                }

                var admissions = members.Count(outcome => outcome.Admits);
                var expectation = 0d;
                var observations = 0;

                foreach (var outcome in members)
                {
                    expectation += outcome.Row.ConditionalAdmissionProbability;
                    observations += outcome.MessageLength;
                }

                cells.Add(
                    new DirectionSequenceDescriptiveCell
                    {
                        Band = band,
                        PairCount = withhold ? 0 : members.Count,
                        MessageObservationCount = withhold ? 0 : observations,
                        ObservedAdmissions = withhold ? 0 : admissions,
                        SumOfConditionalAdmissionProbability = withhold ? 0d : expectation,
                        ObservedMinusExpected = withhold ? 0d : admissions - expectation,
                        Suppressed = withhold,
                    });
            }

            var split = new DirectionSequenceDescriptiveSplit
            {
                Dimension = dimension,
                PopulationName = BinaryInformative,
                PairCount = population.Count,
                Cells = cells,
                SuppressedCellCount = suppressedCells,
                SuppressedPairCount = suppressedPairs,
            };

            if (split.AccountedPairCount != population.Count)
            {
                throw Fail(
                    $"a descriptive split by {dimension} accounts for {split.AccountedPairCount} of " +
                    $"{population.Count} pair(s). A band set that does not cover its population would " +
                    $"drop pairs silently");
            }

            return split;
        }

        // -------------------------------------------------------------------------------------------
        // C7 — scope sensitivity.
        // -------------------------------------------------------------------------------------------

        /// <remarks>
        /// Sensitivity, not replication. The non-primary scope is measured with the same frozen
        /// <em>rules</em> and its own population, never against the primary scope's frozen counts, so no
        /// figure here can move a primary total.
        /// </remarks>
        private List<DirectionSequenceScopeSensitivityCensus> MeasureScopeSensitivity(
            IProgress<DirectionSequenceAlignmentProgress>? progress,
            CancellationToken cancellationToken)
        {
            var sensitivity = new List<DirectionSequenceScopeSensitivityCensus>();

            foreach (var scope in _reconciliation.Census.Scopes)
            {
                if (scope.Scope == _reconciliation.PrimaryScope)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var classified = PopulationOf(
                    scope.Scope, IsClassified, $"{scope.Scope} {ClassifiedPairs}");

                var outcomes = MeasureOutcomes(
                    classified, $"sensitivity {scope.Scope}", progress, cancellationToken);

                sensitivity.Add(
                    new DirectionSequenceScopeSensitivityCensus
                    {
                        Scope = scope.Scope,
                        ClassifiedPairCount = outcomes.Count,
                        Binary = MeasureBinary(
                            outcomes,
                            $"{scope.Scope} {BinaryInformative}",
                            frozenCount: null,
                            scope.Reference.SumOfConditionalAdmissionProbabilityOverInformative),
                        Graded = MeasureGraded(
                            outcomes,
                            $"{scope.Scope} {GradedInformative}",
                            frozenCount: null,
                            scope.Reference.SumOfExpectedEmbeddingShareOverInformative),
                        SharedLogicalPositionCount =
                            _reconciliation.Census.CrossSourceOverlap.SharedLogicalPositionCount,
                        SingleSourceLogicalPositionCount = _reconciliation.Census.CrossSourceOverlap
                            .SingleSourceLogicalPositionCount,
                        PreRegisteredExpectation =
                            "Expected to resemble the primary device-group result, because a device "
                            + "group whose other source records no folder direction reduces to its "
                            + "direction-capable source and a single-source group is that source. "
                            + "Near-identity confirms the frozen scope reduction behaved as documented "
                            + "and is not independent support; a material divergence is an integrity "
                            + "signal to investigate.",
                    });
            }

            return sensitivity;
        }

        private void Report(
            IProgress<DirectionSequenceAlignmentProgress>? progress,
            string stage,
            int processed,
            int total) => progress?.Report(
                new DirectionSequenceAlignmentProgress
                {
                    Stage = stage,
                    ProcessedPairCount = processed,
                    TotalPairCount = total,
                    Elapsed = _elapsed.Elapsed,
                    WorkingSetBytes = Environment.WorkingSet,
                });

        private static InvalidOperationException Fail(string reason) =>
            new(
                $"The Stage B2C-1 alignment run stopped: {reason}. No result is returned, nothing " +
                $"partial is retained, and the workspace is unchanged.");
    }
}
