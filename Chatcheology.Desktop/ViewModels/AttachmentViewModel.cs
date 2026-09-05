using System.Globalization;
using System.Windows.Media.Imaging;
using Chatcheology.Data.Media;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Desktop.Services;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// One attachment, in one of three states the viewer must never let blur together.
    /// </summary>
    /// <remarks>
    /// <para>The three are: unresolved; resolved with a copy this machine can reach; and resolved
    /// with no copy it can reach. <strong>The third is not the first.</strong> An attachment whose
    /// recovery drive is unplugged is still resolved, and showing it as unresolved would say the
    /// project failed to identify something it did identify.</para>
    /// <para>Every word about resolution comes from <see cref="Phase7ReconstructionState"/>.
    /// Nothing here reasons about evidence, scores anything, or infers a status from the presence
    /// of a file.</para>
    /// <para><strong>Unresolved is the archive's ordinary state, and reads as one.</strong> It is
    /// worded and coloured as a neutral note that a decision has not been made yet, never as a
    /// warning or an error: an attachment nobody has matched is not a fault, and a viewer that
    /// coloured hundreds of them as problems would be reporting the project's own caution as
    /// damage.</para>
    /// </remarks>
    public sealed class AttachmentViewModel : ObservableObject
    {
        private readonly string? _fullPath;

        private string? _openError;

        internal AttachmentViewModel(
            ConversationAttachment attachment,
            Phase7ReconstructionState? state,
            MediaAvailability? media)
        {
            Ordinal = attachment.Ordinal;

            if (state is null)
            {
                // Only reachable if a page's attachment vanished between two reads of one
                // connection, which cannot happen on a read-only workspace. Said plainly rather
                // than guessed at.
                HeadlineText = "Attachment state unavailable";
                DetailText = string.Empty;

                return;
            }

            if (state.Status == Phase7ResolutionStatus.Unresolved)
            {
                IsUnresolved = true;
                HeadlineText = "Media not yet matched";
                DetailText = UnresolvedDetail(state);

                return;
            }

            var asset = state.ResolvedAsset!;

            AssetText = DescribeAsset(asset);

            if (media?.Available is { } location)
            {
                IsResolvedAndAvailable = true;
                HeadlineText = "Media resolved";
                DetailText = location.MediaSourceDisplayName;
                _fullPath = location.FullPath;
                FileName = location.FileName;

                if (location.MediaType == MediaType.Image)
                {
                    Thumbnail = MediaPresentation.TryLoadThumbnail(location.FullPath);
                }

                CanOpenExternally =
                    Thumbnail is null && MediaPresentation.CanOpenExternally(location.MediaType);

                if (Thumbnail is null && !CanOpenExternally)
                {
                    DetailText =
                        $"{location.MediaSourceDisplayName} — media type not classified, so it is " +
                        $"not opened automatically";
                }

                return;
            }

            IsResolvedAndUnavailable = true;
            HeadlineText = "Media resolved — file currently unavailable";

            DetailText = media is null || media.RecordedCopyCount == 0
                ? "The workspace records no local copy of this asset."
                : $"The workspace records {Count(media.RecordedCopyCount)}, none of which this " +
                  $"machine can reach right now.";
        }

        /// <summary>The attachment's position within its message.</summary>
        public int Ordinal { get; }

        /// <summary>The one-line state.</summary>
        public string HeadlineText { get; } = string.Empty;

        /// <summary>What the state rests on, in a sentence.</summary>
        public string DetailText { get; } = string.Empty;

        /// <summary>The resolved asset's own recorded facts, or empty when there is none.</summary>
        public string AssetText { get; } = string.Empty;

        /// <summary>The reachable copy's file name, or empty.</summary>
        public string FileName { get; } = string.Empty;

        /// <summary>Whether there is any resolved-asset description to show.</summary>
        public bool HasAssetText => AssetText.Length > 0;

        /// <summary>Whether a preview was decoded.</summary>
        public bool HasThumbnail => Thumbnail is not null;

        /// <summary>Whether Phase 7 reports no asset for this attachment.</summary>
        public bool IsUnresolved { get; }

        /// <summary>Whether a resolved asset has a copy this machine can reach.</summary>
        public bool IsResolvedAndAvailable { get; }

        /// <summary>Whether a resolved asset has no copy this machine can reach.</summary>
        public bool IsResolvedAndUnavailable { get; }

        /// <summary>A decoded preview, or <see langword="null"/> when there is none to show.</summary>
        public BitmapSource? Thumbnail { get; }

        /// <summary>Whether an Open media action applies.</summary>
        public bool CanOpenExternally { get; }

        /// <summary>Why the last Open media attempt did not work, or <see langword="null"/>.</summary>
        public string? OpenError
        {
            get => _openError;
            private set => SetProperty(ref _openError, value);
        }

        /// <summary>
        /// Hands the reachable copy to whatever this machine associates with it.
        /// </summary>
        public void OpenExternally()
        {
            if (_fullPath is null)
            {
                return;
            }

            OpenError = MediaPresentation.TryOpenExternally(_fullPath);
        }

        /// <remarks>
        /// Phase 7's headline reason, then the orthogonal facts its precedence had to choose
        /// between. Selecting a headline must not erase the rest, which is why the state carries
        /// them separately and why they are shown.
        /// </remarks>
        private static string UnresolvedDetail(Phase7ReconstructionState state)
        {
            var parts = new List<string> { Describe(state.PlaceholderReason) };

            if (state.EvidenceRunID is { } runID)
            {
                parts.Add($"evidence run {Text(runID)}");
            }

            if (state.ExactDateCandidateCount is { } exact)
            {
                parts.Add($"{Count(exact, "exact-date candidate")}");
            }

            if (state.AdjacentDateContextCount is { } adjacent and > 0)
            {
                parts.Add($"{Count(adjacent, "adjacent-date context row")}");
            }

            if (state.IsWithinDirectionImpossibleGroup is true)
            {
                parts.Add("in a group the within-direction hypothesis cannot satisfy");
            }

            return string.Join(" · ", parts);
        }

        /// <remarks>
        /// Each is a fact the workspace records, or the absence of one. None of them is a score, a
        /// rank or a confidence, and none of the wording invites reading one as a near miss.
        /// </remarks>
        private static string Describe(Phase7UnresolvedPlaceholderReason? reason) => reason switch
        {
            Phase7UnresolvedPlaceholderReason.NeverEvaluated =>
                "no completed run has evaluated this attachment",

            Phase7UnresolvedPlaceholderReason.NoCandidates =>
                "evaluated, with no candidate and no adjacent-date context",

            Phase7UnresolvedPlaceholderReason.NoExactDateCandidates =>
                "evaluated, with no exact-date candidate",

            Phase7UnresolvedPlaceholderReason.CandidateSetUnranked =>
                "candidates recorded, and the frozen order does not discriminate between them",

            Phase7UnresolvedPlaceholderReason.CandidateSetRanked =>
                "candidates recorded, and the frozen order separates them into tiers",

            Phase7UnresolvedPlaceholderReason.ImpossibleGroupFlagged =>
                "the attachment's group is flagged impossible",

            Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence =>
                "reviewed, and the evidence was found insufficient",

            Phase7UnresolvedPlaceholderReason.ReviewedAllCandidatesIndependentlyDisproven =>
                "every candidate of the current run carries an independent disproof",

            _ => "unresolved",
        };

        /// <remarks>
        /// The media type is shown as the workspace stores it. It is a label here and nothing
        /// branches on it: the typed value the presentation actually uses comes from the data layer
        /// with the location.
        /// </remarks>
        private static string DescribeAsset(Phase7ResolvedAssetDescriptor asset)
        {
            var parts = new List<string> { asset.MediaType, Bytes(asset.SizeBytes) };

            if (asset is { Width: { } width, Height: { } height })
            {
                parts.Add($"{Text(width)}×{Text(height)}");
            }

            if (asset.DurationMS is { } duration)
            {
                parts.Add(TimeSpan.FromMilliseconds(duration).ToString(@"m\:ss"));
            }

            return string.Join(" · ", parts);
        }

        private static string Bytes(long sizeBytes) => sizeBytes switch
        {
            < 1024 => $"{Text(sizeBytes)} B",
            < 1024 * 1024 => $"{(sizeBytes / 1024.0).ToString("0.#", Culture)} KB",
            _ => $"{(sizeBytes / (1024.0 * 1024.0)).ToString("0.#", Culture)} MB",
        };

        private static string Count(int value, string noun = "recorded copy") =>
            value == 1
                ? $"1 {noun}"
                : $"{Text(value)} {(noun.EndsWith('y') ? noun[..^1] + "ies" : noun + "s")}";

        private static string Text(long value) => value.ToString(Culture);

        private static CultureInfo Culture => CultureInfo.CurrentCulture;
    }
}
