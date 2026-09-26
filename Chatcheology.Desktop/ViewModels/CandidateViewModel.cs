using System.Globalization;
using System.Windows.Media.Imaging;
using Chatcheology.Data.Media;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Desktop.Services;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// One candidate in the review panel.
    /// </summary>
    /// <remarks>
    /// <para><strong>Nothing here recommends anything.</strong> Tier, direction and order support are
    /// shown because they help a person decide what to look at. No wording calls a candidate best,
    /// likely, recommended or a match, no number is presented as a score, and being alone in a tier
    /// is not a status.</para>
    /// <para><strong>A disproven candidate stays.</strong> It is de-emphasised and labelled, never
    /// removed: a list that quietly shrank would let "one left" start to look like an answer.</para>
    /// <para><strong>Unreachable media is not evidence.</strong> A candidate whose recorded copy this
    /// machine cannot currently open is still a candidate, still selectable and still confirmable.
    /// </para>
    /// </remarks>
    public sealed class CandidateViewModel : ObservableObject
    {
        private readonly MediaAssetLocation? _available;

        private BitmapSource? _thumbnail;
        private bool _thumbnailAttempted;
        private bool _isSelected;

        internal CandidateViewModel(
            AttachmentReviewCandidate candidate, int tierCount, MediaAvailability? media)
        {
            AttachmentCandidateID = candidate.AttachmentCandidateID;
            MediaAssetID = candidate.MediaAssetID;
            RunID = candidate.RunID;
            IsIndependentlyDisproven = candidate.IsIndependentlyDisproven;
            MediaType = candidate.MediaType;

            TierText = DescribeTier(candidate.TierIndex, tierCount);
            SupportText = DescribeSupport(candidate.Direction, candidate.OrderSupport);
            AssetText = DescribeAsset(candidate);

            _available = media?.Available;

            if (_available is { } location)
            {
                HasAvailableCopy = true;
                FileName = location.FileName;
                MediaSourceText = location.MediaSourceDisplayName;

                CanOpenExternally = MediaPresentation.CanOpenExternally(location.MediaType);
            }
            else
            {
                MediaStateText = media is null || media.RecordedCopyCount == 0
                    ? "The workspace records no local copy of this asset."
                    : "The workspace records a copy this machine cannot reach right now.";
            }
        }

        /// <summary>The candidate row. The identity a disproof names.</summary>
        public long AttachmentCandidateID { get; }

        /// <summary>The asset this candidate proposes.</summary>
        public long MediaAssetID { get; }

        /// <summary>The evidence run this candidate belongs to.</summary>
        public long RunID { get; }

        /// <summary>Whether this exact row already carries an independent disproof.</summary>
        public bool IsIndependentlyDisproven { get; }

        /// <summary>The asset's recorded kind.</summary>
        public MediaType MediaType { get; }

        /// <summary>Where this candidate sits in the current comparison, in words.</summary>
        public string TierText { get; }

        /// <summary>What the evidence dimensions support, in words.</summary>
        public string SupportText { get; }

        /// <summary>The asset's own recorded facts.</summary>
        public string AssetText { get; }

        /// <summary>The reachable copy's file name, or empty.</summary>
        public string FileName { get; } = string.Empty;

        /// <summary>Which media source the reachable copy came from, or empty.</summary>
        public string MediaSourceText { get; } = string.Empty;

        /// <summary>Why there is no preview, when there is none.</summary>
        public string MediaStateText { get; } = string.Empty;

        /// <summary>Whether a recorded copy is currently reachable.</summary>
        public bool HasAvailableCopy { get; }

        /// <summary>Whether an Open media action applies to the reachable copy.</summary>
        public bool CanOpenExternally { get; }

        /// <summary>Whether a preview has been decoded for this candidate.</summary>
        public bool HasThumbnail => Thumbnail is not null;

        /// <summary>A decoded preview, or <see langword="null"/> until one is asked for.</summary>
        /// <remarks>
        /// Decoded when the candidate is selected rather than when the list is built. A page of
        /// messages is bounded at a hundred and fifty; a candidate list is bounded by the archive,
        /// and decoding every one of them to draw a list nobody has looked at yet is work the
        /// operator did not ask for.
        /// </remarks>
        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            private set
            {
                if (SetProperty(ref _thumbnail, value))
                {
                    OnPropertyChanged(nameof(HasThumbnail));
                }
            }
        }

        /// <summary>Whether this candidate is the one being inspected.</summary>
        /// <remarks>
        /// Selecting a candidate loads its preview. <strong>It writes nothing</strong> — no review,
        /// no disproof, no resolution — and it must never begin to.
        /// </remarks>
        public bool IsSelected
        {
            get => _isSelected;
            internal set
            {
                if (SetProperty(ref _isSelected, value) && value)
                {
                    LoadThumbnail();
                }
            }
        }

        /// <summary>Hands the reachable copy to whatever this machine associates with it.</summary>
        /// <returns>A failure message, or <see langword="null"/> when it was handed over.</returns>
        public string? OpenExternally() =>
            _available is { } location
                ? MediaPresentation.TryOpenExternally(location.FullPath)
                : "The recorded media file is not available on this machine.";

        /// <remarks>
        /// Attempted once. A file that will not decode is a fact about this file on this machine, and
        /// re-reading it on every reselection would repeat the same disappointment.
        /// </remarks>
        private void LoadThumbnail()
        {
            if (_thumbnailAttempted || _available is not { } location)
            {
                return;
            }

            _thumbnailAttempted = true;

            if (location.MediaType == MediaType.Image)
            {
                Thumbnail = MediaPresentation.TryLoadThumbnail(location.FullPath);
            }
        }

        /// <remarks>
        /// A tier is a position in a partial order, not a magnitude, so it is rendered as a group
        /// rather than a rank. One tier is the honest reading that the comparison distinguished
        /// nothing, and is said in words rather than shown as "1 of 1", which reads like a winner.
        /// </remarks>
        private static string DescribeTier(int tierIndex, int tierCount) => tierCount switch
        {
            <= 1 => "The available comparison did not distinguish the candidates",
            _ => $"Compared group {Text(tierIndex)} of {Text(tierCount)}",
        };

        private static string DescribeSupport(
            Phase7DirectionSupport direction, Phase7OrderSupport order)
        {
            var parts = new List<string>
            {
                direction == Phase7DirectionSupport.Supportive
                    ? "direction supportive"
                    : "direction says nothing",

                order switch
                {
                    Phase7OrderSupport.Supportive => "order supportive",
                    Phase7OrderSupport.LessSupportive => "order less supportive",
                    _ => "order says nothing",
                },
            };

            return string.Join(" · ", parts);
        }

        private static string DescribeAsset(AttachmentReviewCandidate candidate)
        {
            var parts = new List<string> { candidate.MediaType.ToString(), Bytes(candidate.SizeBytes) };

            if (candidate is { Width: { } width, Height: { } height })
            {
                parts.Add($"{Text(width)}×{Text(height)}");
            }

            if (candidate.DurationMS is { } duration)
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

        private static string Text(long value) => value.ToString(Culture);

        private static CultureInfo Culture => CultureInfo.CurrentCulture;
    }
}
