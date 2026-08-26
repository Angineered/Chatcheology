using Chatcheology.Data.Matching;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Maps the Phase 7 vocabularies to and from the text the workspace stores.
    /// </summary>
    /// <remarks>
    /// Mapped explicitly rather than by <see cref="Enum.ToString()"/> and
    /// <see cref="Enum.Parse{TEnum}(string)"/>, which is the convention
    /// <see cref="Media.MediaTypeText"/> already establishes: the stored values then depend on
    /// neither an enum's numeric ordering nor its member names, so renaming a member becomes a
    /// compile-time decision about the database rather than a silent change to data already written.
    /// <para>
    /// Every string here is also a value the schema's own <c>CHECK</c> constraints name, so the
    /// vocabulary is enforced in two places that cannot drift apart without a test failing.
    /// </para>
    /// </remarks>
    internal static class Phase7EvidenceText
    {
        internal const string Running = "Running";
        internal const string Completed = "Completed";
        internal const string Failed = "Failed";

        internal const string NotAssessable = "NotAssessable";
        internal const string NoCompatibleArrangement = "NoCompatibleArrangement";
        internal const string CompatibleWithAmbiguity = "CompatibleWithAmbiguity";
        internal const string UniquelyConstrained = "UniquelyConstrained";

        internal const string DirectionUnknown = "Unknown";
        internal const string DirectionCompatible = "Compatible";
        internal const string DirectionMixed = "Mixed";
        internal const string DirectionContradictoryOnly = "ContradictoryOnly";

        /// <summary>The text the database stores for <paramref name="status"/>.</summary>
        internal static string Format(Phase7RunStatus status) => status switch
        {
            Phase7RunStatus.Running => Running,
            Phase7RunStatus.Completed => Completed,
            Phase7RunStatus.Failed => Failed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(status), status, "There is no stored representation for this run status."),
        };

        /// <summary>The run status <paramref name="text"/> stands for.</summary>
        internal static Phase7RunStatus ParseRunStatus(string text) => text switch
        {
            Running => Phase7RunStatus.Running,
            Completed => Phase7RunStatus.Completed,
            Failed => Phase7RunStatus.Failed,
            _ => throw new InvalidOperationException(
                $"The workspace stores a Phase7Run status this build does not recognise, of length " +
                $"{text.Length}. The schema's own CHECK constrains the column to three values, so a " +
                $"fourth means the row was written by something else. Nothing is inferred from it."),
        };

        /// <summary>The text the database stores for <paramref name="annotation"/>.</summary>
        internal static string Format(OrderConsistencyAnnotation annotation) => annotation switch
        {
            OrderConsistencyAnnotation.NotAssessable => NotAssessable,
            OrderConsistencyAnnotation.NoCompatibleArrangement => NoCompatibleArrangement,
            OrderConsistencyAnnotation.CompatibleWithAmbiguity => CompatibleWithAmbiguity,
            OrderConsistencyAnnotation.UniquelyConstrained => UniquelyConstrained,
            _ => throw new ArgumentOutOfRangeException(
                nameof(annotation),
                annotation,
                "There is no stored representation for this order-consistency annotation."),
        };

        /// <summary>The order-consistency annotation <paramref name="text"/> stands for.</summary>
        internal static OrderConsistencyAnnotation ParseOrderConsistencyAnnotation(string text) =>
            text switch
            {
                NotAssessable => OrderConsistencyAnnotation.NotAssessable,
                NoCompatibleArrangement => OrderConsistencyAnnotation.NoCompatibleArrangement,
                CompatibleWithAmbiguity => OrderConsistencyAnnotation.CompatibleWithAmbiguity,
                UniquelyConstrained => OrderConsistencyAnnotation.UniquelyConstrained,
                _ => throw new InvalidOperationException(
                    $"The workspace stores an OrderConsistencyAnnotation this build does not " +
                    $"recognise, of length {text.Length}. Reading it as NotAssessable would quietly " +
                    $"turn somebody else's value into an absence of evidence."),
            };

        /// <summary>
        /// The text the database stores for the frozen candidate-level direction aggregate.
        /// </summary>
        /// <remarks>
        /// The four values are Phase 6's, persisted verbatim. Stage 7.1 neither adds a fifth nor
        /// collapses any two of them.
        /// </remarks>
        internal static string Format(DirectionCompatibility compatibility) => compatibility switch
        {
            DirectionCompatibility.Unknown => DirectionUnknown,
            DirectionCompatibility.Compatible => DirectionCompatible,
            DirectionCompatibility.Mixed => DirectionMixed,
            DirectionCompatibility.ContradictoryOnly => DirectionContradictoryOnly,
            _ => throw new ArgumentOutOfRangeException(
                nameof(compatibility),
                compatibility,
                "There is no stored representation for this direction compatibility."),
        };

        /// <summary>The direction compatibility <paramref name="text"/> stands for.</summary>
        internal static DirectionCompatibility ParseDirectionCompatibility(string text) => text switch
        {
            DirectionUnknown => DirectionCompatibility.Unknown,
            DirectionCompatible => DirectionCompatibility.Compatible,
            DirectionMixed => DirectionCompatibility.Mixed,
            DirectionContradictoryOnly => DirectionCompatibility.ContradictoryOnly,
            _ => throw new InvalidOperationException(
                $"The workspace stores a DirectionCompatibilityAnnotation this build does not " +
                $"recognise, of length {text.Length}. It is not read as Unknown, because that would " +
                $"turn an unrecognised annotation into an absence of direction evidence."),
        };
    }
}
