using Chatcheology.Data.Workspace;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// How every Stage 7.2 decision vocabulary is stored, and how stored text is read back.
    /// </summary>
    /// <remarks>
    /// The same device <see cref="Phase7EvidenceText"/> uses for the evidence vocabularies, and for the
    /// same two reasons: one place decides the stored spelling, so a writer and a reader cannot drift
    /// into two spellings of one meaning; and an unrecognised stored value throws rather than being
    /// read as some benign default, because turning somebody else's value into an absence of evidence
    /// is the quiet failure this project cannot afford.
    /// </remarks>
    internal static class Phase7DecisionText
    {
        internal const string ReviewedInsufficientEvidence = "ReviewedInsufficientEvidence";

        internal const string IndependentlyDisproven = "IndependentlyDisproven";

        internal const string HumanContentOrContextInspection = "HumanContentOrContextInspection";
        internal const string ExternalProvenanceOrGroundTruth = "ExternalProvenanceOrGroundTruth";

        internal const string Confirm = "Confirm";
        internal const string ConfirmReplacement = "ConfirmReplacement";
        internal const string Revoke = "Revoke";

        /// <summary>
        /// The stored text of a resolved attachment.
        /// </summary>
        /// <remarks>
        /// Its unresolved counterpart already has a home with the schema, as
        /// <see cref="WorkspaceDatabase.UnresolvedAttachmentStatus"/>, because the importer and the
        /// version-2 migration both write it. Nothing before Stage 7.2 ever wrote the resolved value,
        /// so this is its first definition; both are asserted against the column's own CHECK by test
        /// rather than trusted.
        /// </remarks>
        internal const string ResolvedAttachmentStatus = "Resolved";

        internal static string Format(Phase7ReviewOutcome outcome) => outcome switch
        {
            Phase7ReviewOutcome.ReviewedInsufficientEvidence => ReviewedInsufficientEvidence,
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "There is no stored representation for this review outcome."),
        };

        internal static Phase7ReviewOutcome ParseReviewOutcome(string text) => text switch
        {
            ReviewedInsufficientEvidence => Phase7ReviewOutcome.ReviewedInsufficientEvidence,
            _ => throw new InvalidOperationException(
                $"The workspace stores an AttachmentReviewEvent outcome this build does not " +
                $"recognise, of length {text.Length}. The column's own CHECK constrains it to one " +
                $"value, so anything else was written by something else, and nothing is inferred " +
                $"from it."),
        };

        internal static string Format(Phase7CandidateDisposition disposition) => disposition switch
        {
            Phase7CandidateDisposition.IndependentlyDisproven => IndependentlyDisproven,
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "There is no stored representation for this candidate disposition."),
        };

        internal static Phase7CandidateDisposition ParseCandidateDisposition(string text) =>
            text switch
            {
                IndependentlyDisproven => Phase7CandidateDisposition.IndependentlyDisproven,
                _ => throw new InvalidOperationException(
                    $"The workspace stores a candidate disposition this build does not recognise, of " +
                    $"length {text.Length}. The column's own CHECK constrains it to one value, and " +
                    $"nothing is inferred from another."),
            };

        internal static string Format(Phase7IndependentBasisType basisType) => basisType switch
        {
            Phase7IndependentBasisType.HumanContentOrContextInspection =>
                HumanContentOrContextInspection,
            Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth =>
                ExternalProvenanceOrGroundTruth,

            // Reached only by a caller casting an undefined value into the enum. It must never become
            // a stored basis, so it fails here rather than reaching the database at all.
            _ => throw new ArgumentOutOfRangeException(
                nameof(basisType),
                basisType,
                "There is no stored representation for this independent basis type. The vocabulary " +
                "is closed at two values and has no Other."),
        };

        internal static Phase7IndependentBasisType ParseIndependentBasisType(string text) =>
            text switch
            {
                HumanContentOrContextInspection =>
                    Phase7IndependentBasisType.HumanContentOrContextInspection,
                ExternalProvenanceOrGroundTruth =>
                    Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth,
                _ => throw new InvalidOperationException(
                    $"The workspace stores an independent basis type this build does not recognise, " +
                    $"of length {text.Length}. Reading it as either known value would attribute an " +
                    $"authority nobody recorded."),
            };

        internal static string Format(Phase7ResolutionEventType eventType) => eventType switch
        {
            Phase7ResolutionEventType.Confirm => Confirm,
            Phase7ResolutionEventType.ConfirmReplacement => ConfirmReplacement,
            Phase7ResolutionEventType.Revoke => Revoke,
            _ => throw new ArgumentOutOfRangeException(
                nameof(eventType),
                eventType,
                "There is no stored representation for this resolution event type. There are three " +
                "transitions and no fourth."),
        };

        internal static Phase7ResolutionEventType ParseResolutionEventType(string text) => text switch
        {
            Confirm => Phase7ResolutionEventType.Confirm,
            ConfirmReplacement => Phase7ResolutionEventType.ConfirmReplacement,
            Revoke => Phase7ResolutionEventType.Revoke,
            _ => throw new InvalidOperationException(
                $"The workspace stores a resolution event type this build does not recognise, of " +
                $"length {text.Length}. Replay cannot apply an operation it has no rules for, and it " +
                $"does not guess."),
        };

        internal static string Format(Phase7ResolutionStatus status) => status switch
        {
            Phase7ResolutionStatus.Unresolved => WorkspaceDatabase.UnresolvedAttachmentStatus,
            Phase7ResolutionStatus.Resolved => ResolvedAttachmentStatus,
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "There is no stored representation for this resolution status."),
        };

        internal static Phase7ResolutionStatus ParseResolutionStatus(string text) => text switch
        {
            WorkspaceDatabase.UnresolvedAttachmentStatus => Phase7ResolutionStatus.Unresolved,
            ResolvedAttachmentStatus => Phase7ResolutionStatus.Resolved,
            _ => throw new InvalidOperationException(
                $"The workspace stores an attachment resolution status this build does not " +
                $"recognise, of length {text.Length}. The column's own CHECK constrains it to two " +
                $"values, and reading a third as unresolved would hide a linked asset."),
        };

        /// <summary>
        /// Requires a caller-supplied mandatory text field to carry something a person wrote.
        /// </summary>
        /// <remarks>
        /// Whitespace-only is refused rather than stored. The column's length constraint would accept
        /// a single space, which satisfies the letter of "non-empty" while recording nothing a later
        /// reader could inspect — and inspectability is the whole point of requiring the field.
        /// <para>
        /// The value is stored <strong>verbatim</strong> once accepted. Trimming would alter what a
        /// person wrote, which an audit record must not do.
        /// </para>
        /// </remarks>
        internal static string RequireStatedText(string? value, string parameterName, string what)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            throw new ArgumentException(
                $"{what} is required and must say something. A null, empty or whitespace-only value " +
                $"records nothing a later reader could inspect, and inspectability is the reason the " +
                $"field exists. Nothing has been opened and nothing has been written.",
                parameterName);
        }

        /// <summary>
        /// Accepts an optional caller-supplied text field, refusing a whitespace-only one.
        /// </summary>
        /// <remarks>
        /// Null means "not recorded", which is a legitimate and distinct state the column permits. A
        /// string of spaces is neither that nor a value, so it is a caller mistake rather than either.
        /// </remarks>
        internal static string? AcceptOptionalText(string? value, string parameterName, string what)
        {
            if (value is null || !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            throw new ArgumentException(
                $"{what} was supplied as whitespace only. Omit it entirely to record that it is not " +
                $"known, or supply something a later reader can read. Nothing has been opened and " +
                $"nothing has been written.",
                parameterName);
        }
    }
}
