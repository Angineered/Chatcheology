using System.Globalization;
using Chatcheology.Data.Phase7;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// One entry of an attachment's resolution history.
    /// </summary>
    /// <remarks>
    /// Rendered in the order Phase 7 returned, which is its event order. Nothing here sorts,
    /// re-sorts or replays: two events written in the same second are still ordered by the sequence
    /// they were recorded in, and a display that sorted by time could put them the other way round.
    /// </remarks>
    public sealed class ResolutionEventViewModel
    {
        internal ResolutionEventViewModel(Phase7ResolutionHistoryEntry entry)
        {
            EventID = entry.AttachmentResolutionEventID;

            HeadlineText = entry.EventType switch
            {
                Phase7ResolutionEventType.Confirm => "Confirmed",
                Phase7ResolutionEventType.ConfirmReplacement => "Replaced",
                Phase7ResolutionEventType.Revoke => "Revoked",
                _ => entry.EventType.ToString(),
            };

            OccurredText = entry.OccurredAtUtc.ToLocalTime()
                .ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);

            DetailText = Describe(entry);

            TechnicalText = Technical(entry);
        }

        /// <summary>The event's own identifier, for the technical detail line.</summary>
        public long EventID { get; }

        /// <summary>What kind of event this was.</summary>
        public string HeadlineText { get; }

        /// <summary>When it was recorded, in this machine's local time.</summary>
        public string OccurredText { get; }

        /// <summary>The basis or the revocation reason, as recorded.</summary>
        public string DetailText { get; }

        /// <summary>Identifiers and provenance, for the technical detail expander.</summary>
        public string TechnicalText { get; }

        private static string Describe(Phase7ResolutionHistoryEntry entry)
        {
            if (entry.RevocationReason is { } reason)
            {
                return reason;
            }

            var basis = entry.IndependentBasisType switch
            {
                Phase7IndependentBasisType.HumanContentOrContextInspection =>
                    "Human content or context inspection",

                Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth =>
                    "External provenance or ground truth",

                _ => null,
            };

            return basis is null
                ? entry.IndependentBasisDescription ?? string.Empty
                : $"{basis} — {entry.IndependentBasisDescription}";
        }

        /// <remarks>
        /// Raw identifiers are kept out of the primary reading and gathered here, where somebody
        /// chasing a Phase 7 refusal message can find the event or run it names.
        /// </remarks>
        private static string Technical(Phase7ResolutionHistoryEntry entry)
        {
            var parts = new List<string>
            {
                $"event {Text(entry.AttachmentResolutionEventID)}",
            };

            if (entry.MediaAssetID is { } asset)
            {
                parts.Add($"asset {Text(asset)}");
            }

            if (entry.SupersedesEventID is { } superseded)
            {
                parts.Add($"supersedes event {Text(superseded)}");
            }

            if (entry.SourceRunID is { } run)
            {
                parts.Add($"cited run {Text(run)}");
            }

            if (entry.ActorLabel is { } actor)
            {
                parts.Add(actor);
            }

            return string.Join(" · ", parts);
        }

        private static string Text(long value) =>
            value.ToString(CultureInfo.CurrentCulture);
    }
}
