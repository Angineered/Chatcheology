using Chatcheology.Data.Matching;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The second read pass: turns each attachment's frozen analysis, the group shapes and the
    /// occurrence index into the rows the run will publish.
    /// </summary>
    /// <remarks>
    /// No SQL, no connection and no per-candidate query. Every occurrence fact comes from the
    /// in-memory occurrence index, which is keyed <c>(MediaAssetID, FileDate)</c> exactly so that this
    /// pass is a lookup rather than a scan.
    /// <para>
    /// Every row this pass produces is checked as it is produced: each candidate's occurrence count
    /// against the frozen <c>SupportingPhysicalCopyCount</c>, each candidate against
    /// <c>HasExactMessageDateCopy</c>, each attachment against what the first pass recorded, and each
    /// summary flag against the persisted annotations. A failure here aborts before anything is
    /// written.
    /// </para>
    /// </remarks>
    internal sealed class Phase7EvidenceBuilder
    {
        private readonly Phase7PassOne _passOne;
        private readonly Phase7OccurrenceIndex _occurrenceIndex;

        private readonly List<Phase7SnapshotRow> _snapshotRows = [];
        private readonly List<Phase7CandidateRow> _candidateRows = [];
        private readonly List<Phase7OccurrenceRow> _occurrenceRows = [];
        private readonly List<Phase7AdjacentContextRow> _adjacentRows = [];

        private int _position;

        internal Phase7EvidenceBuilder(
            Phase7PassOne passOne, Phase7OccurrenceIndex occurrenceIndex)
        {
            ArgumentNullException.ThrowIfNull(passOne);
            ArgumentNullException.ThrowIfNull(occurrenceIndex);

            _passOne = passOne;
            _occurrenceIndex = occurrenceIndex;
        }

        /// <summary>Receives one attachment's analysis, as the frozen analysis streams.</summary>
        internal void Add(AttachmentMatchAnalysis analysis)
        {
            ArgumentNullException.ThrowIfNull(analysis);

            var summary = RequireAgreementWithPassOne(analysis);

            var shape = summary.Key is { } key ? _passOne.Shapes[key] : null;

            AppendCandidates(analysis, summary, shape);

            var adjacentRowCount = AppendAdjacentContext(analysis);

            _snapshotRows.Add(
                new Phase7SnapshotRow(
                    analysis.AttachmentID,
                    analysis.ExactDateCandidateCount,
                    adjacentRowCount,
                    HasAnnotation(analysis, DirectionCompatibility.Compatible),
                    HasAnnotation(analysis, DirectionCompatibility.ContradictoryOnly),
                    IsWithinDirectionImpossibleGroup: shape is not null && !shape.IsFeasible,
                    IsForcedPositionGroupShape: shape is not null && shape.IsForcedPositionShape,
                    RemainingCompatiblePositionCountUnderHypothesis:
                        shape?.RemainingCompatiblePositionCount));
        }

        /// <summary>Finishes the payload, once every attachment has been seen.</summary>
        /// <exception cref="InvalidOperationException">
        /// The second pass saw a different number of attachments from the first. Nothing has been
        /// published and the workspace is unchanged.
        /// </exception>
        internal Phase7EvidencePayload Build()
        {
            if (_position != _passOne.Summaries.Count)
            {
                throw new InvalidOperationException(
                    Phase7RunReconciliation.DescribeTwoPassDisagreement(
                        $"the total number of in-scope attachments — the first pass emitted " +
                        $"{_passOne.Summaries.Count} and the second emitted {_position}",
                        attachmentID: null));
            }

            return new Phase7EvidencePayload
            {
                SnapshotRows = _snapshotRows,
                CandidateRows = _candidateRows,
                OccurrenceRows = _occurrenceRows,
                AdjacentContextRows = _adjacentRows,
                InScopeAttachmentCount = _snapshotRows.Count,
                TwoPassAgreementVerified = true,
            };
        }

        /// <summary>
        /// Proves this attachment is the one the first pass saw here, with the same group, rank and
        /// counts.
        /// </summary>
        /// <remarks>
        /// The analysis is read-only, ordered in SQL, reads no clock and depends on no ambient state,
        /// so the two passes must agree. Asserting it rather than assuming it is what turns a
        /// mid-run change or a non-deterministic computation into a failed run instead of a published
        /// hybrid.
        /// </remarks>
        private AttachmentPassSummary RequireAgreementWithPassOne(AttachmentMatchAnalysis analysis)
        {
            if (_position >= _passOne.Summaries.Count)
            {
                throw new InvalidOperationException(
                    Phase7RunReconciliation.DescribeTwoPassDisagreement(
                        $"the total number of in-scope attachments — the first pass emitted " +
                        $"{_passOne.Summaries.Count} and the second emitted at least one more",
                        analysis.AttachmentID));
            }

            var summary = _passOne.Summaries[_position];
            _position++;

            if (summary.AttachmentID != analysis.AttachmentID)
            {
                throw new InvalidOperationException(
                    Phase7RunReconciliation.DescribeTwoPassDisagreement(
                        $"which attachment arrives in position {_position} — the first pass saw " +
                        $"{summary.AttachmentID} and the second saw {analysis.AttachmentID}",
                        analysis.AttachmentID));
            }

            var key = analysis.ExactDateCandidates.Count > 0
                && analysis.MessageDirection != MessageDirection.Unknown
                    ? new Phase7GroupKey(analysis.MessageDate, analysis.MessageDirection)
                    : (Phase7GroupKey?)null;

            if (summary.Key != key)
            {
                throw new InvalidOperationException(
                    Phase7RunReconciliation.DescribeTwoPassDisagreement(
                        "the group an attachment belongs to", analysis.AttachmentID));
            }

            if (summary.ExactDateCandidateCount != analysis.ExactDateCandidateCount)
            {
                throw new InvalidOperationException(
                    Phase7RunReconciliation.DescribeTwoPassDisagreement(
                        $"how many exact-date candidates an attachment has — " +
                        $"{summary.ExactDateCandidateCount} then " +
                        $"{analysis.ExactDateCandidateCount}",
                        analysis.AttachmentID));
            }

            if (summary.DirectionCompatibleCandidateCount
                != analysis.ExactDateDirectionCompatibleCandidateCount)
            {
                throw new InvalidOperationException(
                    Phase7RunReconciliation.DescribeTwoPassDisagreement(
                        $"how many of an attachment's exact-date candidates are " +
                        $"direction-compatible — {summary.DirectionCompatibleCandidateCount} then " +
                        $"{analysis.ExactDateDirectionCompatibleCandidateCount}",
                        analysis.AttachmentID));
            }

            return summary;
        }

        /// <summary>
        /// Appends this attachment's exact-date candidate rows and their occurrence children.
        /// </summary>
        private void AppendCandidates(
            AttachmentMatchAnalysis analysis,
            AttachmentPassSummary summary,
            Phase7GroupShape? shape)
        {
            foreach (var candidate in analysis.ExactDateCandidates)
            {
                // An adjacent-date candidate is by construction an asset with no copy on the message
                // date, so this makes the exact-date-only guarantee a property of the data rather
                // than of the loop that reads it.
                if (!candidate.HasExactMessageDateCopy)
                {
                    throw new InvalidOperationException(
                        $"Attachment {analysis.AttachmentID}'s exact-date candidate " +
                        $"{candidate.MediaAssetID} reports no copy on the message's own date. Only " +
                        $"exact-date candidates are ever written to AttachmentCandidate. Nothing has " +
                        $"been published and the workspace is unchanged.");
                }

                if (_occurrenceIndex.IsZeroByteAsset(candidate.MediaAssetID))
                {
                    throw new InvalidOperationException(
                        $"Attachment {analysis.AttachmentID}'s exact-date candidate " +
                        $"{candidate.MediaAssetID} holds no payload. The frozen analysis excludes " +
                        $"zero-byte assets from candidacy, so a candidate reaching one means this " +
                        $"run and the frozen analysis disagree about the eligible population. " +
                        $"Nothing has been published and the workspace is unchanged.");
                }

                var files =
                    _occurrenceIndex.SupportingFilesOn(candidate.MediaAssetID, analysis.MessageDate);

                // The per-candidate agreement proof: the occurrence set this run derived is exactly
                // the set the frozen analysis counted.
                if (files.Count != candidate.SupportingPhysicalCopyCount)
                {
                    throw new InvalidOperationException(
                        $"Attachment {analysis.AttachmentID}'s candidate {candidate.MediaAssetID} " +
                        $"has {files.Count} supporting physical copies on " +
                        $"{analysis.MessageDate:yyyy-MM-dd} by this run's own derivation, while the " +
                        $"frozen analysis reports {candidate.SupportingPhysicalCopyCount}. Two " +
                        $"derivations of one set size must agree. Nothing has been published and the " +
                        $"workspace is unchanged.");
                }

                var candidateIndex = _candidateRows.Count;

                _candidateRows.Add(
                    new Phase7CandidateRow(
                        analysis.AttachmentID,
                        candidate.MediaAssetID,
                        candidate.DirectionCompatibility,
                        candidate.SupportingPhysicalCopyCount));

                foreach (var file in files)
                {
                    _occurrenceRows.Add(
                        BuildOccurrenceRow(
                            candidateIndex, file, analysis, candidate, summary, shape));
                }
            }
        }

        /// <summary>
        /// Builds one occurrence row, deciding its order annotation in the frozen evaluation order.
        /// </summary>
        /// <remarks>
        /// Each occurrence of a candidate is classified independently at the attachment's rank. Two
        /// occurrences of one candidate may land on different positions and therefore carry different
        /// annotations, which is exactly the disagreement the ranking reduction refuses to resolve in
        /// the favourable direction.
        /// </remarks>
        private Phase7OccurrenceRow BuildOccurrenceRow(
            int candidateIndex,
            Phase7OccurrenceIndex.OccurrenceFile file,
            AttachmentMatchAnalysis analysis,
            AttachmentMatchCandidate candidate,
            AttachmentPassSummary summary,
            Phase7GroupShape? shape)
        {
            if (file.Token is not { } token)
            {
                // Unsupported grammar. NotAssessable, which the ranking reduction drops — never
                // LessSupportive, because the name saying nothing is not the name saying something
                // unfavourable.
                return new Phase7OccurrenceRow(
                    candidateIndex,
                    file.MediaFileID,
                    SequenceToken: null,
                    DateTokenKeyUniqueToPayload: null,
                    OrderConsistencyAnnotation.NotAssessable);
            }

            var unique = _occurrenceIndex.IsDateTokenKeyUniqueToPayload(analysis.MessageDate, token);

            var annotation = Annotate(
                token, unique, analysis, candidate, summary, shape);

            return new Phase7OccurrenceRow(
                candidateIndex, file.MediaFileID, token, unique, annotation);
        }

        /// <summary>
        /// The order annotation, in the frozen evaluation order.
        /// </summary>
        private static OrderConsistencyAnnotation Annotate(
            ushort token,
            bool dateTokenKeyUniqueToPayload,
            AttachmentMatchAnalysis analysis,
            AttachmentMatchCandidate candidate,
            AttachmentPassSummary summary,
            Phase7GroupShape? shape)
        {
            // The attachment has no known direction, or no exact-date candidate at all: no group, so
            // no question to ask.
            if (shape is null)
            {
                return OrderConsistencyAnnotation.NotAssessable;
            }

            // Order support is assessable only for direction-compatible candidates. That scope is
            // inherited, not chosen: the only measured group model builds its positions from
            // compatible assets alone, and building one from every exact-date candidate would be a
            // population Phase 6 never measured. It is an availability scope, not an exclusion and
            // not a precedence rule.
            if (candidate.DirectionCompatibility != DirectionCompatibility.Compatible)
            {
                return OrderConsistencyAnnotation.NotAssessable;
            }

            // The frozen collision gate. A genuine (FileDate, SequenceToken) collision makes this
            // occurrence's position ambiguous, so the reading is unavailable rather than silently
            // ignored. The position itself stays in the group's shape.
            if (!dateTokenKeyUniqueToPayload)
            {
                return OrderConsistencyAnnotation.NotAssessable;
            }

            // An infeasible group: no compatible candidate asset, no supported token position, or
            // fewer positions than members. Every occurrence in it would have no admissible
            // arrangement, but reading that as NoCompatibleArrangement would convert a group-level
            // shortfall into per-candidate negative evidence whose cause the evidence cannot
            // separate. The group fact is recorded, unranked, as IsWithinDirectionImpossibleGroup.
            if (!shape.IsFeasible)
            {
                return OrderConsistencyAnnotation.NotAssessable;
            }

            var position = shape.PositionIndexOf(token);

            if (position < 0)
            {
                throw new InvalidOperationException(
                    $"Attachment {analysis.AttachmentID}'s direction-compatible candidate " +
                    $"{candidate.MediaAssetID} has a supporting copy whose supported token is not " +
                    $"one of its group's token positions. The group's positions are built from the " +
                    $"supported tokens of exactly these copies, so the two cannot differ. Nothing " +
                    $"has been published and the workspace is unchanged.");
            }

            return shape.ClassifyPosition(summary.Rank, position) switch
            {
                Phase7ArrangementClass.None => OrderConsistencyAnnotation.NoCompatibleArrangement,
                Phase7ArrangementClass.ExactlyOne => OrderConsistencyAnnotation.UniquelyConstrained,
                Phase7ArrangementClass.Several =>
                    OrderConsistencyAnnotation.CompatibleWithAmbiguity,
                var unexpected => throw new InvalidOperationException(
                    $"The arrangement classification produced {unexpected}, which has no frozen " +
                    $"order annotation. Nothing has been published and the workspace is unchanged."),
            };
        }

        /// <summary>
        /// Appends this attachment's adjacent-date context rows and reports how many were written.
        /// </summary>
        private int AppendAdjacentContext(AttachmentMatchAnalysis analysis)
        {
            var written = 0;

            foreach (var candidate in analysis.AdjacentDateCandidates)
            {
                if (candidate.HasExactMessageDateCopy)
                {
                    throw new InvalidOperationException(
                        $"Attachment {analysis.AttachmentID}'s adjacent-date candidate " +
                        $"{candidate.MediaAssetID} reports a copy on the message's own date, which " +
                        $"would make it an exact-date candidate. The two bands are built by " +
                        $"different methods of the frozen analysis and are never merged. Nothing " +
                        $"has been published and the workspace is unchanged.");
                }

                if (!candidate.HasPreviousDayCopy && !candidate.HasNextDayCopy)
                {
                    throw new InvalidOperationException(
                        $"Attachment {analysis.AttachmentID}'s adjacent-date candidate " +
                        $"{candidate.MediaAssetID} reports a copy on neither adjacent day, so " +
                        $"nothing qualified it as adjacent-date context. Nothing has been published " +
                        $"and the workspace is unchanged.");
                }

                if (candidate.HasPreviousDayCopy)
                {
                    _adjacentRows.Add(
                        new Phase7AdjacentContextRow(
                            analysis.AttachmentID, candidate.MediaAssetID, PreviousDayOffset));

                    written++;
                }

                if (candidate.HasNextDayCopy)
                {
                    _adjacentRows.Add(
                        new Phase7AdjacentContextRow(
                            analysis.AttachmentID, candidate.MediaAssetID, NextDayOffset));

                    written++;
                }
            }

            return written;
        }

        /// <summary>
        /// Whether any exact-date candidate carries <paramref name="compatibility"/>, cross-checked
        /// against the frozen count where one exists.
        /// </summary>
        private static bool HasAnnotation(
            AttachmentMatchAnalysis analysis, DirectionCompatibility compatibility)
        {
            var present = analysis.ExactDateCandidates.Any(
                candidate => candidate.DirectionCompatibility == compatibility);

            if (compatibility != DirectionCompatibility.Compatible)
            {
                return present;
            }

            if (present != (analysis.ExactDateDirectionCompatibleCandidateCount > 0))
            {
                throw new InvalidOperationException(
                    $"Attachment {analysis.AttachmentID}'s exact-date candidate list and its " +
                    $"direction-compatible count disagree about whether any candidate is " +
                    $"Compatible. Both come from one pass of the frozen analysis. Nothing has been " +
                    $"published and the workspace is unchanged.");
            }

            return present;
        }

        /// <summary>The frozen offset for a copy on the day before the message.</summary>
        internal const int PreviousDayOffset = -1;

        /// <summary>The frozen offset for a copy on the day after the message.</summary>
        internal const int NextDayOffset = 1;
    }
}
