using System.Text.RegularExpressions;
using Chatcheology.Archive;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>One photo of an own memory as a save hands it in: a stored photo file and an optional caption.</summary>
    /// <param name="FileName">The stored file's name, <c>&lt;sha256&gt;.&lt;ext&gt;</c>, as <see cref="OwnPhotoIntake"/> gave it.</param>
    /// <param name="Caption">Optional; trimmed, empty means none.</param>
    public sealed record OwnPhotoDraft(string FileName, string? Caption);

    /// <summary>One external link of an own memory: an https address and an optional label (<see cref="ExternalLinkRules"/>).</summary>
    public sealed record OwnLink(string Url, string? Label);

    /// <summary>What the phone's form hands to a save: every field of one own memory (CH-54).</summary>
    /// <remarks>
    /// Aligned with the desktop's personal memory: no time of day, no participants. The source is fixed
    /// (<see cref="OwnMemoryRules.Source"/>) and is not part of the draft.
    /// </remarks>
    public sealed record OwnMemoryDraft
    {
        /// <summary>Required. With Month precision only the year and month are kept.</summary>
        public required DateOnly? Start { get; init; }

        /// <summary>Optional; not before the start (compared at the precision).</summary>
        public DateOnly? End { get; init; }

        /// <summary>Day or Month.</summary>
        public required MemoryDatePrecision Precision { get; init; }

        /// <summary>Required.</summary>
        public required string Title { get; init; }

        /// <summary>Optional.</summary>
        public string? Summary { get; init; }

        /// <summary>Shown on On this day each year; only for Day precision without an end.</summary>
        public bool EveryYear { get; init; }

        /// <summary>The photos in the order they are shown; the first is the cover. Each file at most once.</summary>
        public IReadOnlyList<OwnPhotoDraft> Photos { get; init; } = [];

        /// <summary>Up to <see cref="ExternalLinkRules.MaxLinksPerOwner"/> links, in order.</summary>
        public IReadOnlyList<OwnLink> Links { get; init; } = [];
    }

    /// <summary>One stored photo of an own memory.</summary>
    /// <param name="FileName">The file's name, <c>&lt;sha256&gt;.&lt;ext&gt;</c>.</param>
    /// <param name="Caption">Its caption, or <see langword="null"/>.</param>
    /// <param name="Path">The file in the own photos folder.</param>
    public sealed record OwnMemoryPhoto(string FileName, string? Caption, string Path);

    /// <summary>One own memory as stored.</summary>
    public sealed record OwnMemory
    {
        /// <summary>The local row id: never reused, even after a delete. Not stable across phones.</summary>
        public required long RowID { get; init; }

        /// <summary>The memory's identity (in save files, mutes and later on other phones).</summary>
        public required Guid Id { get; init; }

        /// <summary>The start day (the first of the month for Month precision).</summary>
        public required DateOnly Start { get; init; }

        /// <summary>The end day, or null.</summary>
        public required DateOnly? End { get; init; }

        /// <summary>Day or Month.</summary>
        public required MemoryDatePrecision Precision { get; init; }

        /// <summary>The title.</summary>
        public required string Title { get; init; }

        /// <summary>The summary, or null.</summary>
        public required string? Summary { get; init; }

        /// <summary>Shown on On this day each year.</summary>
        public required bool EveryYear { get; init; }

        /// <summary>The photos in order; the first is the cover.</summary>
        public required IReadOnlyList<OwnMemoryPhoto> Photos { get; init; }

        /// <summary>The links in order.</summary>
        public required IReadOnlyList<OwnLink> Links { get; init; }

        /// <summary>Always <see cref="OwnMemoryRules.Source"/>.</summary>
        public string Source => OwnMemoryRules.Source;
    }

    /// <summary>The fields of an own memory a refusal names.</summary>
    public enum OwnMemoryField
    {
        /// <summary>The title.</summary>
        Title,

        /// <summary>The start date or the precision.</summary>
        Start,

        /// <summary>The end date.</summary>
        End,

        /// <summary>The summary.</summary>
        Summary,

        /// <summary>The Every year switch.</summary>
        EveryYear,

        /// <summary>The photos or a caption.</summary>
        Photos,

        /// <summary>The links.</summary>
        Links,

        /// <summary>The memory as a whole (for example: it is no longer there).</summary>
        Memory,
    }

    /// <summary>A write that was refused, with one message per field. Nothing was written.</summary>
    public sealed class OwnMemoryValidationException : Exception
    {
        /// <summary>Creates the refusal.</summary>
        public OwnMemoryValidationException(IReadOnlyDictionary<OwnMemoryField, string> errors)
            : base(string.Join(" ", errors.Values) + " Nothing was written.")
        {
            Errors = errors;
        }

        /// <summary>One message per field.</summary>
        public IReadOnlyDictionary<OwnMemoryField, string> Errors { get; }
    }

    /// <summary>The rules an own memory is checked against before anything is written (CH-54).</summary>
    public static partial class OwnMemoryRules
    {
        /// <summary>The fixed source of every own memory; not editable.</summary>
        public const string Source = "Added on the phone";

        /// <summary>
        /// The draft's problems that need nothing but the draft: the shared personal memory rules
        /// (<see cref="PersonalMemoryFieldRules"/>), the photo names and the links (<see cref="ExternalLinkRules"/>).
        /// Empty when the draft may be saved (the store then checks the photo files are there).
        /// </summary>
        public static IReadOnlyDictionary<OwnMemoryField, string> Validate(OwnMemoryDraft draft)
        {
            ArgumentNullException.ThrowIfNull(draft);

            var errors = new Dictionary<OwnMemoryField, string>();

            if (PersonalMemoryFieldRules.TitleError(draft.Title) is { } title)
            {
                errors[OwnMemoryField.Title] = title;
            }

            if (!Enum.IsDefined(draft.Precision))
            {
                errors[OwnMemoryField.Start] = "Choose Day or Month.";
            }
            else if (draft.Start is null)
            {
                errors[OwnMemoryField.Start] = "Choose the date it starts.";
            }
            else if (PersonalMemoryFieldRules.EndError(draft.Start.Value, draft.End, draft.Precision) is { } end)
            {
                errors[OwnMemoryField.End] = end;
            }

            if (PersonalMemoryFieldRules.EveryYearError(draft.EveryYear, draft.Precision, draft.End) is { } everyYear)
            {
                errors[OwnMemoryField.EveryYear] = everyYear;
            }

            if (PersonalMemoryFieldRules.SummaryError(draft.Summary) is { } summary)
            {
                errors[OwnMemoryField.Summary] = summary;
            }

            if (draft.Photos is null)
            {
                errors[OwnMemoryField.Photos] = "The list of photos is missing.";
            }
            else if (PhotosError(draft.Photos) is { } photos)
            {
                errors[OwnMemoryField.Photos] = photos;
            }

            var links = draft.Links;

            if (links is null)
            {
                errors[OwnMemoryField.Links] = "The list of links is missing.";
            }
            else if (links.Any(l => l is null))
            {
                errors[OwnMemoryField.Links] = "Enter the link.";
            }
            else if (ExternalLinkRules.Validate([.. links.Select(l => (l.Url, l.Label))]) is { } link)
            {
                errors[OwnMemoryField.Links] = link.Message;
            }

            return errors;
        }

        /// <summary>
        /// True when <paramref name="fileName"/> is a stored photo's name: 64 lower-case hex digits, a dot and one of
        /// <c>jpg</c>, <c>png</c>, <c>webp</c>, <c>gif</c>, <c>bmp</c>. Never a path.
        /// </summary>
        public static bool IsPhotoFileName(string? fileName) => fileName is not null && PhotoName().IsMatch(fileName);

        private static string? PhotosError(IReadOnlyList<OwnPhotoDraft> photos)
        {
            if (photos.Any(p => p is null || !IsPhotoFileName(p.FileName)))
            {
                return "Every photo needs an image.";
            }

            if (photos.Select(p => p.FileName).Distinct(StringComparer.Ordinal).Count() != photos.Count)
            {
                return "Each photo can be in a memory only once.";
            }

            return photos.Select(p => PersonalMemoryFieldRules.CaptionError(p.Caption)).FirstOrDefault(e => e is not null);
        }

        [GeneratedRegex("^[0-9a-f]{64}\\.(jpg|png|webp|gif|bmp)\\z", RegexOptions.CultureInvariant)]
        private static partial Regex PhotoName();
    }
}
