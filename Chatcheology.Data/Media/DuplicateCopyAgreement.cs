namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Whether several copies of one payload inside one scope and date carry the same token.
    /// </summary>
    /// <remarks>
    /// The denominator is reported as well as the outcome, because "all copies agree" means nothing
    /// without knowing how many groupings had several copies to agree about.
    /// <para>
    /// Neither outcome is interpreted as an error.
    /// </para>
    /// </remarks>
    public sealed class DuplicateCopyAgreement
    {
        /// <summary>Whether the grouping is per source or per device group.</summary>
        public required ScopeLevel Scope { get; init; }

        /// <summary>Payload, scope and date groupings holding several supported copies.</summary>
        public required int GroupsWithSeveralSupportedCopies { get; init; }

        /// <summary>Of those, groupings whose copies all carry one token.</summary>
        public required int GroupsWhereAllCopiesCarryTheSameToken { get; init; }

        /// <summary>Of those, groupings whose copies carry different tokens.</summary>
        public required int GroupsWhereCopiesCarryDifferentTokens { get; init; }
    }
}
