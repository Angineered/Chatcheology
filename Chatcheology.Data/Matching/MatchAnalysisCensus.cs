namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// The bounded aggregate result of one matching analysis.
    /// </summary>
    /// <remarks>
    /// Deliberately counts and distributions only. A real conversation produces far more
    /// attachment/asset relationships than belong in one returned object, so the per-attachment
    /// detail is streamed to the caller's sink as it is produced and only these totals are carried
    /// out. The size of this object does not grow with the size of the archive.
    /// <para>
    /// Nothing here is a confidence level or a success rate. A census showing broad candidate sets
    /// and few unique ones is a correct result: it describes what the archive can support, and the
    /// value of saying so plainly is exactly that nothing downstream has to guess at it.
    /// </para>
    /// </remarks>
    public sealed class MatchAnalysisCensus
    {
        /// <summary>The conversation that was analysed.</summary>
        public required long ConversationID { get; init; }

        /// <summary>
        /// Whether the caller named a local participant, and therefore whether message direction
        /// took any part in this analysis.
        /// </summary>
        public required bool LocalParticipantIDSupplied { get; init; }

        /// <summary>Unresolved attachments in the analysed conversation.</summary>
        public required int ConversationUnresolvedAttachmentCount { get; init; }

        /// <summary>Unresolved attachments anywhere in the workspace.</summary>
        public required int WorkspaceUnresolvedAttachmentCount { get; init; }

        /// <summary>
        /// Unresolved attachments the analysis did not look at because they belong to another
        /// conversation.
        /// </summary>
        /// <remarks>
        /// Reported rather than assumed to be zero. If a workspace's attachments turn out not all
        /// to belong to the conversation being analysed, the census says so instead of quietly
        /// describing part of the problem as though it were the whole of it.
        /// </remarks>
        public required int UnresolvedAttachmentsOutsideAnalysedConversation { get; init; }

        /// <summary>
        /// Analysed attachments whose message carries no sender, which are system messages.
        /// </summary>
        /// <remarks>
        /// A counted state, not an error. Such a message has no direction to derive, so its
        /// candidates are direction-unknown.
        /// </remarks>
        public required int AttachmentsOnMessagesWithNullSender { get; init; }

        /// <summary>Physical files carrying a naming-derived date.</summary>
        public required int MediaFileWithFileDateCount { get; init; }

        /// <summary>Physical files carrying no naming-derived date.</summary>
        public required int MediaFileWithNullFileDateCount { get; init; }

        /// <summary>Distinct calendar dates across every message in the conversation.</summary>
        public required int DistinctConversationMessageDates { get; init; }

        /// <summary>
        /// Distinct calendar dates across the analysed unresolved attachments only.
        /// </summary>
        public required int DistinctAttachmentMessageDates { get; init; }

        /// <summary>
        /// Analysed attachments whose message date carries no dated eligible media anywhere in the
        /// archive.
        /// </summary>
        /// <remarks>
        /// The measure of how much of the conversation falls outside what the recovered media can
        /// speak to at all. It equals <see cref="AttachmentsWithNoExactDateCandidates"/> by
        /// construction — an attachment has exact-date candidates exactly when its date carries
        /// eligible media — and both are reported because they answer different questions: one is
        /// about the archive's coverage, the other about this analysis's output.
        /// </remarks>
        public required int AttachmentCountOnDatesWithNoDatedEligibleMedia { get; init; }

        /// <summary>Analysed attachments with at least one exact-date candidate.</summary>
        public required int AttachmentsWithExactDateCandidates { get; init; }

        /// <summary>Analysed attachments with no exact-date candidate.</summary>
        public required int AttachmentsWithNoExactDateCandidates { get; init; }

        /// <summary>How exact-date candidate counts spread across the analysed attachments.</summary>
        public required CandidateCountDistribution ExactDateCandidateCountDistribution { get; init; }

        /// <summary>
        /// Analysed attachments for which exactly one asset carries exact-date evidence.
        /// </summary>
        /// <remarks>
        /// Reported, and nothing more. Nothing in this phase can turn a unique candidate into a
        /// resolved attachment.
        /// </remarks>
        public required int UniqueExactDateCandidateCount { get; init; }

        /// <summary>
        /// How direction-compatible exact-date candidate counts spread across the analysed
        /// attachments.
        /// </summary>
        public required CandidateCountDistribution ExactDateCompatibleCandidateCountDistribution
        {
            get;
            init;
        }

        /// <summary>
        /// Analysed attachments with exactly one direction-compatible exact-date candidate. Subject
        /// to the same rule as <see cref="UniqueExactDateCandidateCount"/>.
        /// </summary>
        public required int UniqueExactDateAndDirectionCompatibleCandidateCount { get; init; }

        /// <summary>
        /// Analysed attachments with no exact-date candidate but at least one adjacent-date
        /// candidate.
        /// </summary>
        public required int AdjacentDateOnlyAttachmentCount { get; init; }

        /// <summary>
        /// Distinct eligible assets appearing as an exact-date or adjacent-date candidate for at
        /// least one analysed attachment.
        /// </summary>
        public required int DistinctCandidateMediaAssetsOverall { get; init; }

        /// <summary>
        /// Eligible assets whose every physical copy lacks a naming-derived date, and which are
        /// therefore candidates for no attachment at all.
        /// </summary>
        /// <remarks>
        /// Reported as a pool size rather than offered as candidates. Attaching every undated asset
        /// to every attachment would be true and useless: it would multiply the candidate sets by a
        /// figure that says nothing about any particular attachment.
        /// </remarks>
        public required int NoDateEvidenceAssetPoolCount { get; init; }

        /// <summary>Assets excluded from candidacy for holding no payload at all.</summary>
        public required int ZeroByteAssetsExcluded { get; init; }

        /// <summary>Physical files represented by those excluded zero-byte assets.</summary>
        public required int ZeroBytePhysicalFilesRepresentedByExcludedAsset { get; init; }

        /// <summary>Analysed attachments with at least one compatible exact-date candidate.</summary>
        /// <remarks>
        /// This and the three counts beside it are not mutually exclusive. One attachment's
        /// exact-date candidates can occupy several direction states at once, and it is counted in
        /// each state it reaches.
        /// </remarks>
        public required int AttachmentsWithAtLeastOneCompatibleExactCandidate { get; init; }

        /// <summary>Analysed attachments with at least one mixed exact-date candidate.</summary>
        public required int AttachmentsWithAtLeastOneMixedExactCandidate { get; init; }

        /// <summary>
        /// Analysed attachments with at least one direction-unknown exact-date candidate.
        /// </summary>
        public required int AttachmentsWithAtLeastOneUnknownDirectionExactCandidate { get; init; }

        /// <summary>
        /// Analysed attachments with at least one contradictory-only exact-date candidate.
        /// </summary>
        public required int AttachmentsWithAtLeastOneContradictoryOnlyExactCandidate { get; init; }

        /// <summary>Exact-date relationships whose supporting copies are compatible.</summary>
        public required int ExactCandidateRelationsCompatible { get; init; }

        /// <summary>Exact-date relationships whose supporting copies are mixed.</summary>
        public required int ExactCandidateRelationsMixed { get; init; }

        /// <summary>Exact-date relationships with no usable direction evidence.</summary>
        public required int ExactCandidateRelationsUnknown { get; init; }

        /// <summary>Exact-date relationships whose supporting copies only contradict.</summary>
        public required int ExactCandidateRelationsContradictoryOnly { get; init; }

        /// <summary>Exact-date relationships counted by the candidate asset's content kind.</summary>
        public required MediaTypeRelationCounts ExactCandidateRelationsByMediaType { get; init; }

        /// <summary>
        /// What each media source contributed, ordered by <c>MediaSourceID</c> ascending.
        /// </summary>
        public required IReadOnlyList<MediaSourceDateContribution> MediaSourceContributions
        {
            get;
            init;
        }

        /// <summary>How thickly eligible assets sit on the dates they carry.</summary>
        public required AssetsPerDateDensity AssetsPerDate { get; init; }
    }
}
