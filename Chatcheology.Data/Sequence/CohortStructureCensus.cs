using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0 — what the provisional cohort is actually made of, and therefore how many independent
    /// observations it can supply.
    /// </summary>
    /// <remarks>
    /// The first output of the census and the gate on every figure after it. The frozen first pass
    /// builds candidates from the message's date and direction alone, so all cohort relations sharing
    /// a date and a direction rest on one asset's evidence. Counting relations would therefore
    /// overstate the evidence by whatever the repetition happens to be, and this section is what
    /// makes that visible before any correlation is reported.
    /// </remarks>
    public sealed class CohortStructureCensus
    {
        /// <summary>Attachments with exactly one direction-compatible exact-date candidate.</summary>
        public required int CohortRelationCount { get; init; }

        /// <summary>Distinct <c>(date, direction)</c> groups those relations fall into.</summary>
        public required int QualifyingGroupCount { get; init; }

        /// <summary>Distinct provisional candidate assets the cohort names.</summary>
        public required int DistinctCandidateAssetCount { get; init; }

        /// <summary>Distinct message dates the cohort covers.</summary>
        public required int DistinctCohortDateCount { get; init; }

        /// <summary>Dates whose cohort relations are all outgoing.</summary>
        public required int OutgoingOnlyDateCount { get; init; }

        /// <summary>Dates whose cohort relations are all incoming.</summary>
        public required int IncomingOnlyDateCount { get; init; }

        /// <summary>Dates carrying cohort relations in both directions.</summary>
        public required int BothDirectionDateCount { get; init; }

        /// <summary>How many relations each group holds, as a spread and in bands.</summary>
        public required CountSummary RelationsPerGroup { get; init; }

        /// <summary>Groups holding exactly one relation.</summary>
        public required int SingletonGroupCount { get; init; }

        /// <summary>Groups holding more than one.</summary>
        public required int MultiRelationGroupCount { get; init; }

        /// <summary>
        /// Relations sitting in a group with others, which is the size of the population that must
        /// never be read as independent support.
        /// </summary>
        public required int RelationsInMultiRelationGroups { get; init; }

        /// <summary>The largest number of relations one group holds.</summary>
        public required int MaximumRelationsInOneGroup { get; init; }

        /// <summary>How the both-direction dates break down.</summary>
        public required CrossDirectionDateCategoryCounts CrossDirectionDates { get; init; }
    }
}
