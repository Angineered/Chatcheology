namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The frozen two-dimension comparison rule, and the tiered partial order it induces. Pure: no
    /// SQL, no connection, no persistence, and nothing stored anywhere.
    /// </summary>
    /// <remarks>
    /// The rule, in full. For candidates A and B of one attachment, let <c>D</c> be the dimensions
    /// where both have an available reading. A is evidentially preferred to B only when, for every
    /// dimension in <c>D</c>, A is no worse than B, and for at least one, A is strictly better. If
    /// <c>D</c> is empty, or each wins on a different dimension, they are incomparable — tied, not
    /// arbitrarily ordered.
    /// <para>
    /// Unavailable evidence never counts as negative for either candidate. There is no score, no
    /// weight, no threshold and no total order, and nothing here awards support for token uniqueness,
    /// for a small remaining-position count, for a forced group shape, or for how many physical copies
    /// survived.
    /// </para>
    /// </remarks>
    public static class Phase7SupportRanking
    {
        /// <summary>
        /// Compares two candidates under the frozen rule.
        /// </summary>
        public static Phase7SupportComparison Compare(
            Phase7CandidateSupport left, Phase7CandidateSupport right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);

            return Compare(
                Phase7SupportProfile.Of(left), Phase7SupportProfile.Of(right));
        }

        /// <summary>
        /// Arranges candidates into tiers of the partial order the frozen rule induces.
        /// </summary>
        /// <remarks>
        /// Tier 0 is every candidate with no evidential predecessor among those remaining; remove it
        /// and repeat.
        /// <para>
        /// <strong>A tier index is not a magnitude and not a score.</strong> "Tier 0 versus tier 1"
        /// says only that some candidate in tier 0 is preferred to that tier-1 candidate under the
        /// rule; it does not say by how much, does not compare two members of one tier, and does not
        /// make tier 2 twice as far from tier 0 as tier 1 is.
        /// </para>
        /// <para>
        /// Within a tier, candidates are ordered by <c>MediaAssetID</c> ascending, <strong>for display
        /// stability only, carrying zero evidential meaning</strong> — the same convention and the same
        /// stated reason <c>AttachmentMatchCandidate</c> already uses, so that nothing in the
        /// presentation can be read as a preference.
        /// </para>
        /// </remarks>
        public static Phase7RankedCandidateSet BuildTiers(
            IEnumerable<Phase7CandidateSupport> candidates)
        {
            ArgumentNullException.ThrowIfNull(candidates);

            // Sorted once here, so every tier comes out in display order without a sort per tier and
            // so two runs over permuted input produce identical tiers.
            var entries = candidates
                .OrderBy(candidate => candidate.MediaAssetID)
                .Select(candidate => (Candidate: candidate, Profile: Phase7SupportProfile.Of(candidate)))
                .ToList();

            var remaining = new List<int>(Enumerable.Range(0, entries.Count));

            var tiers = new List<IReadOnlyList<Phase7CandidateSupport>>();

            while (remaining.Count > 0)
            {
                var tier = remaining
                    .Where(index => !remaining.Any(
                        other => Compare(entries[other].Profile, entries[index].Profile)
                            == Phase7SupportComparison.LeftPreferred))
                    .ToList();

                if (tier.Count == 0)
                {
                    // Not reachable under the frozen value sets, where a strict preference can only
                    // come from one totally-ordered dimension. Handled rather than assumed away: if a
                    // future dimension set ever admitted a preference cycle, the honest answer is that
                    // those candidates are mutually incomparable — one tier — not an infinite loop.
                    tiers.Add([.. remaining.Select(index => entries[index].Candidate)]);

                    break;
                }

                tiers.Add([.. tier.Select(index => entries[index].Candidate)]);

                var taken = tier.ToHashSet();

                remaining.RemoveAll(taken.Contains);
            }

            return new Phase7RankedCandidateSet { Tiers = tiers };
        }

        /// <summary>
        /// The comparison itself, over the generic dimension-indexed profile.
        /// </summary>
        /// <remarks>
        /// Written as a loop over <see cref="Phase7SupportDimension"/> rather than as two hard-coded
        /// conditions, so no dimension has hidden precedence and adding one is a compile-time-visible
        /// change.
        /// </remarks>
        internal static Phase7SupportComparison Compare(
            Phase7SupportProfile left, Phase7SupportProfile right)
        {
            var common = 0;
            var leftWins = 0;
            var rightWins = 0;

            foreach (var dimension in Enum.GetValues<Phase7SupportDimension>())
            {
                var leftLevel = left[dimension];
                var rightLevel = right[dimension];

                // Unavailable on either side means the dimension is not in D at all. It is never
                // negative for the candidate that lacks it, and never positive for the one that has it.
                if (leftLevel == Phase7SupportLevel.Unavailable
                    || rightLevel == Phase7SupportLevel.Unavailable)
                {
                    continue;
                }

                common++;

                if (leftLevel > rightLevel)
                {
                    leftWins++;
                }
                else if (rightLevel > leftLevel)
                {
                    rightWins++;
                }
            }

            if (common == 0)
            {
                return Phase7SupportComparison.Incomparable;
            }

            return (leftWins, rightWins) switch
            {
                (> 0, 0) => Phase7SupportComparison.LeftPreferred,
                (0, > 0) => Phase7SupportComparison.RightPreferred,

                // Either the evidence conflicts, or the two are equal on every common dimension.
                // Both are the same answer: neither is preferred.
                _ => Phase7SupportComparison.Incomparable,
            };
        }
    }
}
