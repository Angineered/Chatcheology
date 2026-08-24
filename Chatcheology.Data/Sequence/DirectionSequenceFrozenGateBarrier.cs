using System.Globalization;
using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Refuses to let an alignment run read any outcome until it has reproduced the accepted Stage
    /// B2C-0 gate state from the same workspace and the same pinned inputs.
    /// </summary>
    /// <remarks>
    /// Everything here aborts. There is no flagged, partial or best-effort reconciliation: the frozen
    /// scope, the frozen populations and the frozen references either come back identical or the run
    /// stops before an outcome exists, because a drifted population would produce an alignment figure
    /// that no longer describes the population the freeze review accepted.
    /// <para>
    /// The checks are grouped as the freeze record groups them, and every one of them counts itself, so
    /// the reconciliation can report how much it actually verified.
    /// </para>
    /// </remarks>
    public static class DirectionSequenceFrozenGateBarrier
    {
        /// <summary>How many decimal places the frozen reference readings are compared at.</summary>
        private const string ReadingFormat = "F6";

        /// <summary>
        /// Reconciles one census against a frozen declaration, or throws.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Any frozen target, identity, provenance gate or disagreement gate failed. No reconciliation
        /// is returned, and no outcome may be computed.
        /// </exception>
        public static DirectionSequenceFrozenGateReconciliation Reconcile(
            DirectionSequenceGateCensus census,
            DirectionSequenceFrozenGateDeclaration declaration,
            DirectionSequenceStageB1Declaration stageB1,
            IReadOnlyList<DeviceGroupAssignment> deviceGroups)
        {
            ArgumentNullException.ThrowIfNull(census);
            ArgumentNullException.ThrowIfNull(declaration);
            ArgumentNullException.ThrowIfNull(stageB1);
            ArgumentNullException.ThrowIfNull(deviceGroups);

            var verified = 0;

            RequireProvenance(census, stageB1, deviceGroups, ref verified);
            RequireMessagePopulation(census, declaration, ref verified);
            RequireScopeTargets(census, declaration, ref verified);
            RequireDisagreementGate(census, ref verified);

            var primary = PrimaryScopeOf(census, declaration);

            RequireSupply(primary, declaration, ref verified);
            RequirePairUniverse(primary, declaration, ref verified);
            RequireDeterminacy(primary, declaration, ref verified);
            RequireReferenceReadings(primary, declaration, ref verified);

            return new DirectionSequenceFrozenGateReconciliation(
                census, declaration, stageB1, verified);
        }

        // -------------------------------------------------------------------------------------------
        // Provenance.
        // -------------------------------------------------------------------------------------------

        private static void RequireProvenance(
            DirectionSequenceGateCensus census,
            DirectionSequenceStageB1Declaration stageB1,
            IReadOnlyList<DeviceGroupAssignment> deviceGroups,
            ref int verified)
        {
            if (!census.StageATokenCoverageReconciled)
            {
                throw Refuse(
                    "the Stage A token-coverage declaration was not supplied, so the gate recounted " +
                    "supported tokens without reconciling them against the preserved figures. An " +
                    "alignment run may not treat an unreconciled recount as provenance");
            }

            verified++;

            if (!stageB1.SupportedTokenIsMonotoneWithinLocalDate)
            {
                throw Refuse(
                    "the Stage B1 declaration does not assert that the supported token orders media " +
                    "monotonically within a local date. That finding is the ordering authority this " +
                    "stage's whole question rests on, and it cannot be re-established here");
            }

            verified++;

            RequireFigure(
                "Stage B1 supported-token digit width",
                stageB1.SupportedTokenDigitWidth,
                4,
                ref verified);

            // The one Stage B1 cardinality figure the gate independently recounts. Cross-source overlap
            // is deliberately not reconciled here: how the key universe distributes across acquisition
            // stores is something this gate works out for its own cross-source and scope reporting, not
            // something Stage B1 established, and requiring it as provenance asked the caller to pin a
            // quantity no preserved evidence fixes.
            RequireFigure(
                "Stage B1 distinct logical positions",
                stageB1.DistinctLogicalPositionCount,
                census.CrossSourceOverlap.DistinctLogicalPositionCount,
                ref verified);

            RequireCollisionFindingIsCoherent(stageB1, ref verified);

            RequireGrouping(stageB1.DeviceGroups, deviceGroups, ref verified);

            var blind = census.Sources
                .Where(source => !source.RecordsAnyDirection)
                .Select(source => source.MediaSourceID)
                .Order()
                .ToList();

            if (!blind.SequenceEqual(stageB1.DirectionBlindMediaSourceIDs.Order()))
            {
                throw Refuse(
                    $"the Stage B1 declaration names {stageB1.DirectionBlindMediaSourceIDs.Count} " +
                    $"direction-blind source(s) and this census finds {blind.Count}. Which sources can " +
                    $"emit a direction symbol decides what every scope's token side is made of");
            }

            verified++;
        }

        /// <summary>
        /// Refuses a Stage B1 collision finding that cannot describe the position universe beside it.
        /// </summary>
        /// <remarks>
        /// This figure is carried forward rather than recomputed — the gate has no reason to track which
        /// assets share a <c>(date, token)</c> key — so it cannot be reconciled against a recount. What
        /// can still be refused is a declaration that is not internally coherent: a count of keys naming
        /// several payloads cannot be negative, and cannot exceed the number of keys it was counted over.
        /// A transcription error large enough to matter fails one of those.
        /// </remarks>
        private static void RequireCollisionFindingIsCoherent(
            DirectionSequenceStageB1Declaration stageB1, ref int verified)
        {
            if (stageB1.PositionsWithMoreThanOneAssetCount < 0)
            {
                throw Refuse(
                    $"the Stage B1 declaration reports {stageB1.PositionsWithMoreThanOneAssetCount} " +
                    $"logical position(s) mapping to more than one asset, which is not a count");
            }

            if (stageB1.PositionsWithMoreThanOneAssetCount > stageB1.DistinctLogicalPositionCount)
            {
                throw Refuse(
                    $"the Stage B1 declaration reports {stageB1.PositionsWithMoreThanOneAssetCount} " +
                    $"logical position(s) mapping to more than one asset out of " +
                    $"{stageB1.DistinctLogicalPositionCount} position(s), so the two figures do not " +
                    $"describe the same universe");
            }

            verified++;
        }

        /// <remarks>
        /// Compared as a set of pairs, not as a list, because the order a caller writes assignments in
        /// is not part of the grouping.
        /// </remarks>
        private static void RequireGrouping(
            IReadOnlyList<DeviceGroupAssignment> declared,
            IReadOnlyList<DeviceGroupAssignment> applied,
            ref int verified)
        {
            var declaredPairs = declared
                .Select(assignment => (assignment.MediaSourceID, assignment.DeviceGroupID))
                .OrderBy(pair => pair.MediaSourceID)
                .ToList();

            var appliedPairs = applied
                .Select(assignment => (assignment.MediaSourceID, assignment.DeviceGroupID))
                .OrderBy(pair => pair.MediaSourceID)
                .ToList();

            if (!declaredPairs.SequenceEqual(appliedPairs))
            {
                throw Refuse(
                    "the device grouping this run applies is not the grouping the Stage B1 declaration " +
                    "preserves. A numbering scope Stage B1 did not establish would measure a different " +
                    "numbering authority under the same name");
            }

            verified++;
        }

        // -------------------------------------------------------------------------------------------
        // Message side.
        // -------------------------------------------------------------------------------------------

        private static void RequireMessagePopulation(
            DirectionSequenceGateCensus census,
            DirectionSequenceFrozenGateDeclaration declaration,
            ref int verified)
        {
            var messages = census.MessagePopulation;

            RequireFigure(
                "considered unresolved attachments",
                declaration.ConsideredAttachmentCount,
                messages.ConsideredAttachmentCount,
                ref verified);

            RequireFigure(
                "distinct attachment dates",
                declaration.DistinctAttachmentDateCount,
                messages.DistinctAttachmentDateCount,
                ref verified);

            RequireFigure(
                "outgoing attachments",
                declaration.OutgoingAttachmentCount,
                messages.OutgoingAttachmentCount,
                ref verified);

            RequireFigure(
                "incoming attachments",
                declaration.IncomingAttachmentCount,
                messages.IncomingAttachmentCount,
                ref verified);

            RequireFigure(
                "multi-attachment messages",
                declaration.MultiAttachmentMessageCount,
                messages.MultiAttachmentMessageCount,
                ref verified);

            RequireFigure(
                "maximum attachments on one message",
                declaration.MaximumAttachmentsOnOneMessage,
                messages.MaximumAttachmentsOnOneMessage,
                ref verified);

            // Zero by construction — a senderless message aborts the gate — and asserted rather than
            // assumed, because the supply identity m = outgoing + incoming depends on it.
            RequireFigure(
                "unknown-direction attachments",
                0,
                messages.UnknownDirectionAttachmentCount,
                ref verified);

            RequireFigure(
                "outgoing plus incoming attachments",
                messages.OutgoingAttachmentCount + messages.IncomingAttachmentCount,
                messages.ConsideredAttachmentCount,
                ref verified);
        }

        // -------------------------------------------------------------------------------------------
        // Scopes.
        // -------------------------------------------------------------------------------------------

        private static void RequireScopeTargets(
            DirectionSequenceGateCensus census,
            DirectionSequenceFrozenGateDeclaration declaration,
            ref int verified)
        {
            if (declaration.Scopes.Count != census.Scopes.Count)
            {
                throw Refuse(
                    $"the freeze declares {declaration.Scopes.Count} scope target(s) and this census " +
                    $"measured {census.Scopes.Count} scope(s). The wrong-scope discriminator only works " +
                    $"if every measured scope is declared");
            }

            verified++;

            foreach (var target in declaration.Scopes)
            {
                var measured = census.Scopes.SingleOrDefault(scope => scope.Scope == target.Scope)
                    ?? throw Refuse(
                        $"the freeze declares targets for scope {target.Scope}, which this census did " +
                        $"not measure");

                verified++;

                var population = measured.PairPopulation;

                RequireFigure(
                    $"{target.Scope} pairs with message symbols",
                    target.PairsWithMessageSymbols,
                    population.PairsWithMessageSymbols,
                    ref verified);

                RequireFigure(
                    $"{target.Scope} pairs excluded by direction conflict",
                    target.ExcludedByDirectionConflictPairCount,
                    population.ExcludedByDirectionConflictPairCount,
                    ref verified);

                RequireFigure(
                    $"{target.Scope} supply-assessed pairs",
                    target.SupplyAssessedPairCount,
                    measured.Supply.AfterCollapse.Population,
                    ref verified);

                // Definitional, and asserted so a change to either side cannot pass unnoticed: supply
                // is assessed over exactly the pairs carrying message symbols that conflict did not
                // exclude.
                RequireFigure(
                    $"{target.Scope} supply-assessed pairs by identity",
                    target.PairsWithMessageSymbols - target.ExcludedByDirectionConflictPairCount,
                    target.SupplyAssessedPairCount,
                    ref verified);
            }
        }

        private static void RequireDisagreementGate(
            DirectionSequenceGateCensus census, ref int verified)
        {
            var overlap = census.CrossSourceOverlap;

            // The frozen stage-level rule, in exact integers: conflicting / shared <= 0.1%.
            if ((long)overlap.ConflictingPositionCount * 1000 > overlap.SharedLogicalPositionCount)
            {
                throw Refuse(
                    $"cross-source direction disagreement is " +
                    $"{overlap.ConflictingPositionCount} of {overlap.SharedLogicalPositionCount} " +
                    $"shared supported positions, which exceeds the frozen one-tenth-of-one-percent " +
                    $"stage-level tolerance. The folder-direction evidence is not stable enough to " +
                    $"carry an order question");
            }

            verified++;
        }

        private static DirectionSequenceScopeCensus PrimaryScopeOf(
            DirectionSequenceGateCensus census,
            DirectionSequenceFrozenGateDeclaration declaration)
        {
            if (declaration.PrimaryScope != ScopeLevel.DeviceGroupDate)
            {
                throw Refuse(
                    $"the freeze names {declaration.PrimaryScope} as the primary scope. Stage B2C-0 " +
                    $"froze the Stage B1 device-group scope, and a primary aggregate at any other scope " +
                    $"would not be the population the freeze review accepted");
            }

            return census.Scopes.SingleOrDefault(
                    scope => scope.Scope == declaration.PrimaryScope)
                ?? throw Refuse(
                    $"this census did not measure the frozen primary scope " +
                    $"{declaration.PrimaryScope}");
        }

        // -------------------------------------------------------------------------------------------
        // Primary scope.
        // -------------------------------------------------------------------------------------------

        private static void RequireSupply(
            DirectionSequenceScopeCensus primary,
            DirectionSequenceFrozenGateDeclaration declaration,
            ref int verified)
        {
            var supply = primary.Supply.AfterCollapse;

            RequireFigure(
                "after-collapse supply-insufficient pairs",
                declaration.SupplyInsufficientPairCountAfterCollapse,
                supply.SupplyInsufficientPairCount,
                ref verified);

            RequireFigure(
                "after-collapse supply-sufficient pairs",
                declaration.SupplySufficientPairCountAfterCollapse,
                supply.SupplySufficientPairCount,
                ref verified);

            RequireFigure(
                "pairs becoming sufficient after collapse",
                declaration.PairsBecomingSufficientAfterCollapse,
                primary.Supply.PairsBecomingSufficientAfterCollapse,
                ref verified);

            RequireFigure(
                "after-collapse supply split",
                supply.SupplyInsufficientPairCount + supply.SupplySufficientPairCount,
                supply.Population,
                ref verified);
        }

        private static void RequirePairUniverse(
            DirectionSequenceScopeCensus primary,
            DirectionSequenceFrozenGateDeclaration declaration,
            ref int verified)
        {
            var population = primary.PairPopulation;

            RequireFigure(
                "degenerate pairs",
                declaration.DegeneratePairCount,
                population.Degenerate.DegeneratePairCount,
                ref verified);

            RequireFigure(
                "pairs with no token position",
                declaration.NoTokenPositionPairCount,
                population.Degenerate.NoTokenPositionPairCount,
                ref verified);

            RequireFigure(
                "classified pairs",
                declaration.ClassifiedPairCount,
                population.ClassifiedPairCount,
                ref verified);

            RequireFigure(
                "classified message observations",
                declaration.ClassifiedMessageObservationCount,
                population.MessageObservationsClassified,
                ref verified);

            // A derived relation, not a frozen target. The two figures named "supply insufficient"
            // describe different sets: the supply census counts every assessed pair that could not
            // supply the message side, and the pair population counts only those that emitted token
            // positions and still could not. A pair emitting nothing cannot supply anything, so the
            // difference is exactly the no-token-position pairs.
            RequireFigure(
                "pair-population supply-insufficient pairs by derivation",
                primary.Supply.AfterCollapse.SupplyInsufficientPairCount
                    - population.Degenerate.NoTokenPositionPairCount,
                population.SupplyInsufficientPairCount,
                ref verified);
        }

        private static void RequireDeterminacy(
            DirectionSequenceScopeCensus primary,
            DirectionSequenceFrozenGateDeclaration declaration,
            ref int verified)
        {
            var determinacy = primary.Determinacy;

            RequireFigure(
                "binary-determinate pairs",
                declaration.BinaryDeterminatePairCount,
                determinacy.BinaryDeterminatePairCount,
                ref verified);

            RequireFigure(
                "binary-informative pairs",
                declaration.BinaryInformativePairCount,
                determinacy.BinaryInformativePairCount,
                ref verified);

            RequireFigure(
                "graded-determinate pairs",
                declaration.GradedDeterminatePairCount,
                determinacy.GradedDeterminatePairCount,
                ref verified);

            RequireFigure(
                "graded-informative pairs",
                declaration.GradedInformativePairCount,
                determinacy.GradedInformativePairCount,
                ref verified);

            RequireFigure(
                "binary-informative and graded-informative pairs",
                declaration.BinaryInformativeAndGradedInformative,
                determinacy.BinaryInformativeAndGradedInformative,
                ref verified);

            RequireFigure(
                "binary-determinate and graded-informative pairs",
                declaration.BinaryDeterminateAndGradedInformative,
                determinacy.BinaryDeterminateAndGradedInformative,
                ref verified);

            RequireFigure(
                "binary-informative and graded-determinate pairs",
                declaration.BinaryInformativeAndGradedDeterminate,
                determinacy.BinaryInformativeAndGradedDeterminate,
                ref verified);

            RequireFigure(
                "binary-determinate and graded-determinate pairs",
                declaration.BinaryDeterminateAndGradedDeterminate,
                determinacy.BinaryDeterminateAndGradedDeterminate,
                ref verified);

            // The one implication that holds between the two classifications. The empty cell is
            // necessary but not sufficient on its own, which is why the non-empty cells above are
            // declared too.
            if (determinacy.BinaryInformativeAndGradedDeterminate != 0)
            {
                throw Refuse(
                    $"{determinacy.BinaryInformativeAndGradedDeterminate} pair(s) are " +
                    $"binary-informative and graded-determinate. Graded determinacy implies binary " +
                    $"determinacy, so that cell must be empty and one of the two classifications is " +
                    $"wrong");
            }

            verified++;

            RequireFigure(
                "binary classification split",
                determinacy.BinaryDeterminatePairCount + determinacy.BinaryInformativePairCount,
                determinacy.Population,
                ref verified);

            RequireFigure(
                "graded classification split",
                determinacy.GradedDeterminatePairCount + determinacy.GradedInformativePairCount,
                determinacy.Population,
                ref verified);

            RequireFigure(
                "determinacy cross-tabulation total",
                determinacy.CrossTabulationTotal,
                determinacy.Population,
                ref verified);

            RequireFigure(
                "classified pairs against determinacy population",
                primary.PairPopulation.ClassifiedPairCount,
                determinacy.Population,
                ref verified);

            RequireBandFigure(
                primary,
                DirectionSequenceQrBand.ExactlyOne,
                declaration.ExactlyOneQrBandPairCount,
                ref verified);

            RequireBandFigure(
                primary,
                DirectionSequenceQrBand.ExactlyZero,
                declaration.ExactlyZeroQrBandPairCount,
                ref verified);
        }

        private static void RequireBandFigure(
            DirectionSequenceScopeCensus primary,
            DirectionSequenceQrBand band,
            int declared,
            ref int verified)
        {
            var row = primary.Reference.BandRows.SingleOrDefault(candidate => candidate.Band == band)
                ?? throw Refuse($"this census reports no {band} q_r band row");

            RequireFigure($"{band} q_r band pairs", declared, row.PairCount, ref verified);
        }

        // -------------------------------------------------------------------------------------------
        // Reference readings.
        // -------------------------------------------------------------------------------------------

        /// <remarks>
        /// Compared as the six-decimal rendering the freeze record prints, because that rendering is what
        /// was frozen. The underlying sums are the gate's own accumulation of exact ratios truncated at
        /// one part in a million million, and reproducing them means calling that same path rather than
        /// re-deriving the total a different way.
        /// </remarks>
        private static void RequireReferenceReadings(
            DirectionSequenceScopeCensus primary,
            DirectionSequenceFrozenGateDeclaration declaration,
            ref int verified)
        {
            var reference = primary.Reference;

            RequireReading(
                "sum of q_r over classified pairs",
                declaration.SumOfConditionalAdmissionProbability,
                reference.SumOfConditionalAdmissionProbability,
                ref verified);

            RequireReading(
                "sum of q_r over binary-informative pairs",
                declaration.SumOfConditionalAdmissionProbabilityOverInformative,
                reference.SumOfConditionalAdmissionProbabilityOverInformative,
                ref verified);

            RequireReading(
                "sum of E_r[share] over classified pairs",
                declaration.SumOfExpectedEmbeddingShare,
                reference.SumOfExpectedEmbeddingShare,
                ref verified);

            RequireReading(
                "sum of E_r[share] over graded-informative pairs",
                declaration.SumOfExpectedEmbeddingShareOverInformative,
                reference.SumOfExpectedEmbeddingShareOverInformative,
                ref verified);
        }

        // -------------------------------------------------------------------------------------------
        // Comparison primitives.
        // -------------------------------------------------------------------------------------------

        private static void RequireFigure(
            string figure, int declared, int observed, ref int verified)
        {
            if (declared != observed)
            {
                throw Refuse(
                    $"the freeze declares {figure} as {declared} and this run reconciles {observed}. " +
                    $"The frozen population and the population an outcome would be computed over are " +
                    $"not the same");
            }

            verified++;
        }

        internal static string Render(double reading) =>
            reading.ToString(ReadingFormat, CultureInfo.InvariantCulture);

        private static void RequireReading(
            string figure, string declared, double observed, ref int verified)
        {
            var rendered = Render(observed);

            if (!string.Equals(declared, rendered, StringComparison.Ordinal))
            {
                throw Refuse(
                    $"the freeze declares {figure} as {declared} and this run reads {rendered}. The " +
                    $"reference an outcome would be compared against has moved");
            }

            verified++;
        }

        private static InvalidOperationException Refuse(string reason) =>
            new(
                $"The Stage B2C-1 frozen-gate barrier refused this run: {reason}. No alignment outcome " +
                $"has been calculated, read or exposed, and the workspace is unchanged.");
    }
}
