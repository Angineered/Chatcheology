namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// How the dates carrying cohort relations in both directions are made up.
    /// </summary>
    /// <remarks>
    /// Only <see cref="BothDirectionGroupsSingleton"/> can supply a clean relation-level comparison.
    /// The other two categories are structural evidence: a group holding several relations names one
    /// provisional asset for several messages and does not say which message, if any, it belongs to.
    /// </remarks>
    public sealed class CrossDirectionDateCategoryCounts
    {
        /// <summary>One outgoing relation and one incoming relation.</summary>
        public required int BothDirectionGroupsSingleton { get; init; }

        /// <summary>One direction holds a single relation, the other holds several.</summary>
        public required int OneSingletonOneMultiRelation { get; init; }

        /// <summary>Both directions hold several relations.</summary>
        public required int BothMultiRelation { get; init; }

        /// <summary>Every date contributing relations in both directions.</summary>
        public int Total =>
            BothDirectionGroupsSingleton + OneSingletonOneMultiRelation + BothMultiRelation;
    }
}
