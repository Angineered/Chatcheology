using Chatcheology.Data.Matching;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The first read pass: collects group membership, each attachment's rank within its group, and
    /// the per-attachment summary the second pass is checked against.
    /// </summary>
    /// <remarks>
    /// Two passes rather than one, because a group's admissible window needs <c>M</c> — how many
    /// in-scope attachments share the group — before any occurrence in that group can be annotated.
    /// The alternative, one pass retaining every candidate list, was rejected for the reason
    /// <c>WorkspaceMatchingService</c>'s own documentation gives for streaming at all: "a real
    /// conversation's attachments and their candidates together are hundreds of thousands of
    /// relationships". This pass retains only group shape — a few thousand groups plus one integer
    /// rank per attachment.
    /// <para>
    /// The rank to attachment binding is <strong>new in Stage 7.1</strong>. Stage B2B computed
    /// per-rank windows without ever binding a rank to an attachment: <c>MeasureRanges</c> fills
    /// <c>UnionCounts[rank]</c> positionally and <c>CandidateGroup</c> holds only a
    /// <c>MessageCount</c>. The binding is forced rather than chosen, though — the hypothesis under
    /// audit is that within a <c>(date, direction)</c> group message source order corresponds to
    /// strictly increasing recovered token order, so rank order <em>is</em> message source order,
    /// which the frozen analysis already fixes deterministically as
    /// <c>(SequenceNumber, Ordinal, AttachmentID)</c>.
    /// </para>
    /// </remarks>
    internal sealed class Phase7GroupShapeCollector
    {
        private readonly List<AttachmentPassSummary> _summaries = [];
        private readonly Dictionary<Phase7GroupKey, GroupUnderConstruction> _groups = [];

        /// <summary>Receives one attachment's analysis, as the frozen analysis streams.</summary>
        /// <exception cref="InvalidOperationException">
        /// The analysis contradicts itself, or two members of one group expose different compatible
        /// candidate sets. Nothing has been published and the workspace is unchanged.
        /// </exception>
        internal void Add(AttachmentMatchAnalysis analysis)
        {
            ArgumentNullException.ThrowIfNull(analysis);

            var compatible = analysis.ExactDateCandidates
                .Where(
                    candidate =>
                        candidate.DirectionCompatibility == DirectionCompatibility.Compatible)
                .Select(candidate => candidate.MediaAssetID)
                .ToArray();

            // The assertion GroupCollector.Add makes: the count and the list come from one pass, so
            // they cannot disagree.
            if (compatible.Length != analysis.ExactDateDirectionCompatibleCandidateCount)
            {
                throw new InvalidOperationException(
                    $"The frozen analysis reports " +
                    $"{analysis.ExactDateDirectionCompatibleCandidateCount} direction-compatible " +
                    $"exact-date candidates for attachment {analysis.AttachmentID} while listing " +
                    $"{compatible.Length}. The count and the list come from one pass. Nothing has " +
                    $"been published and the workspace is unchanged.");
            }

            // Out of the group model, but never out of the run: an attachment with no exact-date
            // candidate, or no known direction, still receives its snapshot row and whatever
            // candidates and occurrence rows it has.
            if (analysis.ExactDateCandidates.Count == 0
                || analysis.MessageDirection == MessageDirection.Unknown)
            {
                _summaries.Add(
                    new AttachmentPassSummary(
                        analysis.AttachmentID,
                        Key: null,
                        Rank: NotAMember,
                        analysis.ExactDateCandidateCount,
                        analysis.ExactDateDirectionCompatibleCandidateCount));

                return;
            }

            var key = new Phase7GroupKey(analysis.MessageDate, analysis.MessageDirection);
            var fingerprint = Fold(compatible);

            if (!_groups.TryGetValue(key, out var group))
            {
                group = new GroupUnderConstruction(compatible, fingerprint);
                _groups.Add(key, group);
            }
            else if (group.Fingerprint != fingerprint
                || group.CompatibleAssets.Length != compatible.Length)
            {
                throw new InvalidOperationException(
                    $"Two attachments sharing date {key.Date:yyyy-MM-dd} and direction " +
                    $"{key.Direction} expose different compatible candidate sets. The frozen " +
                    $"analysis derives candidates from the date and direction alone, so they cannot " +
                    $"differ. Nothing has been published and the workspace is unchanged.");
            }

            var rank = group.MemberCount;
            group.MemberCount++;

            _summaries.Add(
                new AttachmentPassSummary(
                    analysis.AttachmentID,
                    key,
                    rank,
                    analysis.ExactDateCandidateCount,
                    analysis.ExactDateDirectionCompatibleCandidateCount));
        }

        /// <summary>
        /// Turns the collected membership into finished group shapes, using the occurrence index for
        /// the token positions.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// A copy supporting a direction-compatible candidate records the contradicting direction, or
        /// a compatible candidate asset has no qualifying physical copy on its group's date. Nothing
        /// has been published and the workspace is unchanged.
        /// </exception>
        internal Phase7PassOne Build(Phase7OccurrenceIndex occurrenceIndex)
        {
            ArgumentNullException.ThrowIfNull(occurrenceIndex);

            var shapes = new Dictionary<Phase7GroupKey, Phase7GroupShape>(_groups.Count);

            foreach (var (key, group) in _groups)
            {
                shapes.Add(key, BuildShape(key, group, occurrenceIndex));
            }

            return new Phase7PassOne(_summaries, shapes);
        }

        /// <summary>
        /// The rank recorded for an attachment that belongs to no group.
        /// </summary>
        internal const int NotAMember = -1;

        private static Phase7GroupShape BuildShape(
            Phase7GroupKey key,
            GroupUnderConstruction group,
            Phase7OccurrenceIndex occurrenceIndex)
        {
            var positions = new SortedSet<ushort>();

            // Which stored IsSent reading contradicts this group's message direction. An outgoing
            // message is contradicted by a copy known not to be beneath a Sent directory
            // (IsSent = 0); an incoming one by a copy that is (IsSent = 1). A copy recording no
            // direction at all contradicts nothing, which a null never equals either way.
            var contradictingIsSent = key.Direction != MessageDirection.Outgoing;

            foreach (var mediaAssetID in group.CompatibleAssets)
            {
                if (occurrenceIndex.IsZeroByteAsset(mediaAssetID))
                {
                    // A payload with no bytes is never a candidate, so a compatible candidate asset
                    // reaching one means this run and the frozen analysis disagree about the eligible
                    // population.
                    throw new InvalidOperationException(
                        $"MediaAsset {mediaAssetID} is a direction-compatible candidate on " +
                        $"{key.Date:yyyy-MM-dd} but holds no payload. The frozen analysis excludes " +
                        $"zero-byte assets from candidacy. Nothing has been published and the " +
                        $"workspace is unchanged.");
                }

                var files = occurrenceIndex.SupportingFilesOn(mediaAssetID, key.Date);

                if (files.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"MediaAsset {mediaAssetID} is a direction-compatible candidate on " +
                        $"{key.Date:yyyy-MM-dd} but has no qualifying physical copy on that date, " +
                        $"although the frozen analysis placed it there on the strength of one. " +
                        $"Nothing has been published and the workspace is unchanged.");
                }

                foreach (var file in files)
                {
                    if (file.Token is not { } token)
                    {
                        continue;
                    }

                    // The assertion TokenEvidence.BuildPositions makes: the frozen rule makes a
                    // relation compatible only when no supporting copy contradicts the message's
                    // direction, so a contradicting copy here means the two disagree.
                    if (file.IsSent == contradictingIsSent)
                    {
                        throw new InvalidOperationException(
                            $"MediaFile {file.MediaFileID} supports direction-compatible candidate " +
                            $"asset {mediaAssetID} on {key.Date:yyyy-MM-dd} but records the opposite " +
                            $"direction. The frozen rule makes a relation compatible only when no " +
                            $"supporting copy contradicts the message's direction. Nothing has been " +
                            $"published and the workspace is unchanged.");
                    }

                    // A token is a key, so one payload recovered into several stores at one position
                    // contributes one position. A genuine repeat carries a different name, hence a
                    // different token, and survives as a second position.
                    positions.Add(token);
                }
            }

            var positionCount = positions.Count;

            var feasibility =
                group.CompatibleAssets.Length == 0
                    ? Phase7GroupFeasibility.NoCompatibleCandidateAsset
                    : positionCount == 0
                        ? Phase7GroupFeasibility.NoSupportedTokenPosition
                        : positionCount < group.MemberCount
                            ? Phase7GroupFeasibility.TooFewTokenPositions
                            : Phase7GroupFeasibility.EnoughTokenPositions;

            return new Phase7GroupShape
            {
                Key = key,
                CompatibleAssets = group.CompatibleAssets,
                MemberCount = group.MemberCount,
                Positions = [.. positions],
                Feasibility = feasibility,
            };
        }

        /// <remarks>
        /// An order-sensitive fold, which is enough because the frozen analysis emits candidates in
        /// ascending <c>MediaAssetID</c> order: two groups agreeing on count and fold agree on the
        /// set. The same device <c>GroupCollector.Fold</c> uses, and cheaper than comparing whole
        /// lists per relation.
        /// </remarks>
        private static long Fold(long[] assets)
        {
            var hash = 1469598103934665603L;

            foreach (var asset in assets)
            {
                hash = unchecked((hash ^ asset) * 1099511628211L);
            }

            return hash;
        }

        /// <summary>One group while its members are still arriving.</summary>
        private sealed class GroupUnderConstruction(long[] compatibleAssets, long fingerprint)
        {
            internal long[] CompatibleAssets { get; } = compatibleAssets;

            internal long Fingerprint { get; } = fingerprint;

            internal int MemberCount { get; set; }
        }
    }

    /// <summary>What the first pass leaves behind for the second.</summary>
    /// <param name="Summaries">
    /// One entry per in-scope attachment, in the frozen analysis order, so the second pass can prove
    /// it saw the same attachments in the same order with the same counts.
    /// </param>
    /// <param name="Shapes">The finished group shapes, by key.</param>
    internal sealed record Phase7PassOne(
        IReadOnlyList<AttachmentPassSummary> Summaries,
        IReadOnlyDictionary<Phase7GroupKey, Phase7GroupShape> Shapes);

    /// <summary>
    /// What the first pass recorded about one attachment, and what the second pass must agree with.
    /// </summary>
    /// <param name="AttachmentID">The attachment.</param>
    /// <param name="Key">
    /// Its group, or <see langword="null"/> when it has no exact-date candidate or no known
    /// direction.
    /// </param>
    /// <param name="Rank">
    /// Its 0-based rank within that group, or <see cref="Phase7GroupShapeCollector.NotAMember"/>.
    /// </param>
    /// <param name="ExactDateCandidateCount">How many exact-date candidates it had.</param>
    /// <param name="DirectionCompatibleCandidateCount">How many of those were compatible.</param>
    internal readonly record struct AttachmentPassSummary(
        long AttachmentID,
        Phase7GroupKey? Key,
        int Rank,
        int ExactDateCandidateCount,
        int DirectionCompatibleCandidateCount);
}
