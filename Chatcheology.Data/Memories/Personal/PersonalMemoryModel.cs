using System.Globalization;
using Chatcheology.Archive;

namespace Chatcheology.Data.Memories.Personal
{
    /// <summary>How exactly a personal memory is dated: a day, or a month. No time of day.</summary>
    public enum PersonalDatePrecision
    {
        /// <summary>A day, such as 2 January 2018.</summary>
        Day,

        /// <summary>A month, such as January 2018.</summary>
        Month,
    }

    /// <summary>What a personal memory points to: one message or one entry, never both.</summary>
    public sealed record PersonalLinkTarget
    {
        private PersonalLinkTarget(int? messageSequenceNumber, long? entryID)
        {
            MessageSequenceNumber = messageSequenceNumber;
            EntryID = entryID;
        }

        /// <summary>The workspace sequence number of a message of the open conversation, or null.</summary>
        public int? MessageSequenceNumber { get; }

        /// <summary>The entry (imported or personal), or null.</summary>
        public long? EntryID { get; }

        /// <summary>A link to a message of the open conversation, by its workspace sequence number.</summary>
        public static PersonalLinkTarget ToMessage(int sequenceNumber) => new(sequenceNumber, null);

        /// <summary>A link to an entry.</summary>
        public static PersonalLinkTarget ToEntry(long entryID) => new(null, entryID);
    }

    /// <summary>The image a saved memory should carry.</summary>
    public sealed record PersonalImageChoice
    {
        private PersonalImageChoice(string? existingSha256, string? newFilePath)
        {
            ExistingSha256 = existingSha256;
            NewFilePath = newFilePath;
        }

        /// <summary>No image.</summary>
        public static PersonalImageChoice None { get; } = new(null, null);

        /// <summary>The image the memory already has, by its hash.</summary>
        public string? ExistingSha256 { get; }

        /// <summary>A file the operator chose; its bytes are copied in on save. The file is only read.</summary>
        public string? NewFilePath { get; }

        /// <summary>Keeps the image already stored under <paramref name="sha256"/>.</summary>
        public static PersonalImageChoice Existing(string sha256) => new(sha256, null);

        /// <summary>Copies the bytes of <paramref name="path"/> in on save.</summary>
        public static PersonalImageChoice FromFile(string path) => new(null, path);
    }

    /// <summary>One extra photo of a memory (after its cover, CH-46): the image and an optional caption.</summary>
    /// <param name="Image">An image already stored (by hash), or a file to copy in on save. Never <see cref="PersonalImageChoice.None"/>.</param>
    /// <param name="Caption">Optional; trimmed, empty means none.</param>
    public sealed record PersonalPhotoDraft(PersonalImageChoice Image, string? Caption);

    /// <summary>What the form hands to a save: every field of one personal memory.</summary>
    public sealed record PersonalEntryDraft
    {
        /// <summary>The personal collection the memory belongs to.</summary>
        public required long CollectionID { get; init; }

        /// <summary>Required.</summary>
        public required string Title { get; init; }

        /// <summary>Required. With Month precision only the year and month are kept.</summary>
        public required DateOnly? Start { get; init; }

        /// <summary>Optional; not before the start (compared at the precision).</summary>
        public DateOnly? End { get; init; }

        /// <summary>Day or Month.</summary>
        public required PersonalDatePrecision Precision { get; init; }

        /// <summary>Optional short description.</summary>
        public string? Summary { get; init; }

        /// <summary>Required: where the memory comes from (for example "My own memory").</summary>
        public required string Source { get; init; }

        /// <summary>The image to carry after the save: the cover, shown on the card.</summary>
        public PersonalImageChoice Image { get; init; } = PersonalImageChoice.None;

        /// <summary>
        /// The extra photos after the cover, in the order they are shown (CH-46). Needs a cover; each
        /// photo at most once in a memory, the cover included.
        /// </summary>
        public IReadOnlyList<PersonalPhotoDraft> ExtraPhotos { get; init; } = [];

        /// <summary>The links the memory has after the save.</summary>
        public IReadOnlyList<PersonalLinkTarget> Links { get; init; } = [];

        /// <summary>
        /// Shown on On this day each year on its day (CH-53). Only for a memory dated to the day without an end
        /// date; anything else is refused with <see cref="PersonalFields.EveryYear"/>.
        /// </summary>
        public bool EveryYear { get; init; }
    }

    /// <summary>The field names a validation message is attached to.</summary>
    public static class PersonalFields
    {
        /// <summary>A collection's name.</summary>
        public const string CollectionName = "CollectionName";

        /// <summary>The memory's collection.</summary>
        public const string Collection = "Collection";

        /// <summary>The title.</summary>
        public const string Title = "Title";

        /// <summary>The start date.</summary>
        public const string Start = "Start";

        /// <summary>The end date.</summary>
        public const string End = "End";

        /// <summary>The source line.</summary>
        public const string Source = "Source";

        /// <summary>The description.</summary>
        public const string Summary = "Summary";

        /// <summary>The image.</summary>
        public const string Image = "Image";

        /// <summary>
        /// The links: a memory's links to messages and memories, or, from <see cref="ExternalLinkStore"/>, the
        /// external links of an entry or a noted date (CH-48; the bad one's index in
        /// <see cref="PersonalMemoryValidationException.LinkIndex"/>).
        /// </summary>
        public const string Links = "Links";

        /// <summary>The memory as a whole (for example: it is imported).</summary>
        public const string Entry = "Entry";

        /// <summary>A noted date's month and day (CH-47).</summary>
        public const string Date = "Date";

        /// <summary>A noted date's year (CH-47).</summary>
        public const string Year = "Year";

        /// <summary>A noted date's note (CH-47).</summary>
        public const string Note = "Note";

        /// <summary>A noted date as a whole (for example: it is no longer there) (CH-47).</summary>
        public const string NotedDate = "NotedDate";

        /// <summary>A memory's Every year switch (CH-53).</summary>
        public const string EveryYear = "EveryYear";
    }

    /// <summary>A save that was refused, with one message per field. Nothing was written.</summary>
    public sealed class PersonalMemoryValidationException : Exception
    {
        /// <summary>Creates the refusal.</summary>
        public PersonalMemoryValidationException(IReadOnlyDictionary<string, string> errors)
            : base(string.Join(" ", errors.Values) + " Nothing was written.")
        {
            Errors = errors;
        }

        /// <summary>Creates a refusal of one field.</summary>
        public PersonalMemoryValidationException(string field, string message)
            : this(new Dictionary<string, string> { [field] = message })
        {
        }

        /// <summary>One message per field (see <see cref="PersonalFields"/>).</summary>
        public IReadOnlyDictionary<string, string> Errors { get; }

        /// <summary>
        /// For a refused list of external links (CH-48): the index (0-based) of the first bad link, or
        /// <see langword="null"/> when the refusal is about the whole list (too many links) or not about links.
        /// </summary>
        public int? LinkIndex { get; init; }
    }

    /// <summary>A personal collection with its entries, date order.</summary>
    public sealed record PersonalCollectionSummary(long CollectionID, string Name, IReadOnlyList<PersonalEntrySummary> Entries);

    /// <summary>One personal memory as the list shows it.</summary>
    /// <param name="Precision">The stored precision: Day, Month, or Second for a memory with a time of day.</param>
    /// <param name="EveryYear">Shown on On this day each year (CH-53); false before format 6.</param>
    public sealed record PersonalEntrySummary(
        long EntryID, long CollectionID, string Title, DateTime StartLocal, DateTime? EndLocal, string Precision, bool EveryYear = false)
    {
        /// <summary>The date as the list shows it.</summary>
        public string DateText => PersonalMemoryRules.DateText(Precision, StartLocal, EndLocal);

        /// <summary>Whether the form can edit it (Day or Month; not one with a time of day).</summary>
        public bool IsEditable => PersonalMemoryRules.TryParsePrecision(Precision, out _);
    }

    /// <summary>An imported collection: shown read-only, by name and entry count.</summary>
    public sealed record ImportedCollectionSummary(long CollectionID, string Name, int EntryCount);

    /// <summary>What the memories file holds, for the Memories area.</summary>
    /// <param name="FileExists">False when there is no memories file yet (everything else is empty).</param>
    public sealed record MemoriesOverview(
        bool FileExists,
        IReadOnlyList<PersonalCollectionSummary> Personal,
        IReadOnlyList<ImportedCollectionSummary> Imported)
    {
        /// <summary>No file.</summary>
        public static MemoriesOverview Missing { get; } = new(false, [], []);
    }

    /// <summary>One existing link of a personal memory.</summary>
    /// <param name="EntryDescription">For a link to an entry: its title, date and collection.</param>
    public sealed record PersonalLinkDetails(long EntryLinkID, PersonalLinkTarget Target, string? EntryDescription);

    /// <summary>One personal memory, everything the form edits.</summary>
    public sealed record PersonalEntryDetails
    {
        /// <summary>The entry.</summary>
        public required long EntryID { get; init; }

        /// <summary>Its collection.</summary>
        public required long CollectionID { get; init; }

        /// <summary>The title.</summary>
        public required string Title { get; init; }

        /// <summary>The start day (the first of the month for Month precision).</summary>
        public required DateOnly Start { get; init; }

        /// <summary>The end day, or null.</summary>
        public required DateOnly? End { get; init; }

        /// <summary>Day or Month.</summary>
        public required PersonalDatePrecision Precision { get; init; }

        /// <summary>The summary, or null.</summary>
        public required string? Summary { get; init; }

        /// <summary>The source line (empty when none is recorded).</summary>
        public required string Source { get; init; }

        /// <summary>The image's hash, or null.</summary>
        public required string? ImageSha256 { get; init; }

        /// <summary>The image file in the memories media folder, or null when there is none or it is not there.</summary>
        public required string? ImagePath { get; init; }

        /// <summary>The links, in the order they were made.</summary>
        public required IReadOnlyList<PersonalLinkDetails> Links { get; init; }

        /// <summary>The extra photos after the cover, in order (none in a format 1 or 2 file).</summary>
        public IReadOnlyList<PersonalPhotoDetails> ExtraPhotos { get; init; } = [];

        /// <summary>Shown on On this day each year (CH-53); false in a file before format 6.</summary>
        public bool EveryYear { get; init; }
    }

    /// <summary>One stored extra photo of a memory.</summary>
    /// <param name="Sha256">The photo's hash.</param>
    /// <param name="Extension">Its stored file's extension (png, jpg, gif or webp).</param>
    /// <param name="Caption">Its caption, or <see langword="null"/>.</param>
    /// <param name="Path">The file in the memories media folder, or <see langword="null"/> when it is not there.</param>
    public sealed record PersonalPhotoDetails(string Sha256, string Extension, string? Caption, string? Path);

    /// <summary>What deleting a personal collection removed.</summary>
    /// <param name="Entries">Its memories.</param>
    /// <param name="Links">Links from or to those memories.</param>
    /// <param name="Photos">Their extra-photo rows (the stored files are kept).</param>
    public sealed record CollectionDeleteResult(int Entries, int Links, int Photos);

    /// <summary>
    /// The files of the memories media folder that nothing in the memories file uses (CH-46 tidy-up).
    /// </summary>
    /// <param name="UnusedFiles">The unused files' names (<c>&lt;sha256&gt;.&lt;ext&gt;</c>), sorted.</param>
    /// <param name="UnusedBytes">Their total size.</param>
    /// <param name="UsedFiles">Files of the pattern that something uses.</param>
    /// <param name="OtherItems">Files or folders not named <c>&lt;sha256&gt;.&lt;ext&gt;</c>: never touched.</param>
    public sealed record UnusedImagesSurvey(IReadOnlyList<string> UnusedFiles, long UnusedBytes, int UsedFiles, int OtherItems)
    {
        /// <summary>Nothing to tidy (no folder, or no file).</summary>
        public static UnusedImagesSurvey Empty { get; } = new([], 0, 0, 0);
    }

    /// <summary>What a tidy-up deleted.</summary>
    /// <param name="Deleted">Files deleted.</param>
    /// <param name="DeletedBytes">Their total size.</param>
    /// <param name="NowUsed">Listed files kept because something uses them by the time of deleting.</param>
    /// <param name="Gone">Listed files no longer there.</param>
    /// <param name="Failed">Listed files that could not be deleted (locked, say).</param>
    public sealed record UnusedImagesDeleteResult(int Deleted, long DeletedBytes, int NowUsed, int Gone, int Failed);

    /// <summary>An entry a link can point to, as the picker shows it.</summary>
    public sealed record EntryLinkCandidate(long EntryID, string CollectionName, string Title, string DateText);

    /// <summary>A message a link can point to, as the picker shows it.</summary>
    public sealed record MessageLinkCandidate(int SequenceNumber, DateTime TimeLocal, string? SenderName, string Preview);

    /// <summary>
    /// The messages of the open conversation, as links need them: whether one exists, and a day's
    /// messages for the picker. Read-only.
    /// </summary>
    public interface IMessageLinkTargets
    {
        /// <summary>Whether the conversation holds a message with this workspace sequence number.</summary>
        bool Exists(int sequenceNumber);

        /// <summary>The message with this sequence number, or null.</summary>
        MessageLinkCandidate? Read(int sequenceNumber);

        /// <summary>The messages of one local day, in sequence order.</summary>
        IReadOnlyList<MessageLinkCandidate> ReadDay(DateOnly day);
    }

    /// <summary>The rules a personal memory is checked against, before anything is written.</summary>
    public static class PersonalMemoryRules
    {
        /// <summary>The longest title kept (shared with the phone: <see cref="PersonalMemoryFieldRules"/>).</summary>
        public const int MaxTitleLength = PersonalMemoryFieldRules.MaxTitleLength;

        /// <summary>The longest collection name kept.</summary>
        public const int MaxCollectionNameLength = 100;

        /// <summary>The longest summary kept.</summary>
        public const int MaxSummaryLength = PersonalMemoryFieldRules.MaxSummaryLength;

        /// <summary>The longest source line kept.</summary>
        public const int MaxSourceLength = PersonalMemoryFieldRules.MaxSourceLength;

        /// <summary>The longest photo caption kept.</summary>
        public const int MaxCaptionLength = PersonalMemoryFieldRules.MaxCaptionLength;

        /// <summary>The stored precision word for a precision.</summary>
        public static string StoredPrecision(PersonalDatePrecision precision) => precision switch
        {
            PersonalDatePrecision.Day => MemoriesDatabase.PrecisionDay,
            PersonalDatePrecision.Month => MemoriesDatabase.PrecisionMonth,
            _ => throw new ArgumentOutOfRangeException(nameof(precision)),
        };

        /// <summary>Reads a stored precision; false for Second (a time of day) or anything else.</summary>
        public static bool TryParsePrecision(string stored, out PersonalDatePrecision precision)
        {
            switch (stored)
            {
                case MemoriesDatabase.PrecisionDay:
                    precision = PersonalDatePrecision.Day;
                    return true;
                case MemoriesDatabase.PrecisionMonth:
                    precision = PersonalDatePrecision.Month;
                    return true;
                default:
                    precision = default;
                    return false;
            }
        }

        /// <summary>A day as stored at a precision: the day itself, or the first of its month.</summary>
        public static DateOnly Normalise(DateOnly day, PersonalDatePrecision precision) =>
            PersonalMemoryFieldRules.Normalise(day, Shared(precision));

        /// <summary>The shared precision for a desktop precision (same values; an undefined one stays undefined).</summary>
        private static MemoryDatePrecision Shared(PersonalDatePrecision precision) => (MemoryDatePrecision)(int)precision;

        /// <summary>A collection name's problem, or null when it is fine.</summary>
        public static string? CollectionNameError(string? name)
        {
            var trimmed = name?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                return "Give the collection a name.";
            }

            return trimmed.Length > MaxCollectionNameLength
                ? $"Keep the name to {MaxCollectionNameLength.ToString(CultureInfo.CurrentCulture)} characters."
                : null;
        }

        /// <summary>
        /// The draft's problems that need nothing but the draft: title, dates, source, lengths. An empty
        /// result means the draft may go to a save, which checks the rest.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Validate(PersonalEntryDraft draft)
        {
            ArgumentNullException.ThrowIfNull(draft);

            var errors = new Dictionary<string, string>();

            if (PersonalMemoryFieldRules.TitleError(draft.Title) is { } titleError)
            {
                errors[PersonalFields.Title] = titleError;
            }

            if (draft.Start is null)
            {
                errors[PersonalFields.Start] = "Choose the date it starts.";
            }
            else if (PersonalMemoryFieldRules.EndError(draft.Start.Value, draft.End, Shared(draft.Precision)) is { } endError)
            {
                errors[PersonalFields.End] = endError;
            }

            if (!Enum.IsDefined(draft.Precision))
            {
                errors[PersonalFields.Start] = "Choose Day or Month.";
            }

            if (EveryYearError(draft.EveryYear, draft.Precision, draft.End) is { } everyYear)
            {
                errors[PersonalFields.EveryYear] = everyYear;
            }

            if (PersonalMemoryFieldRules.SourceError(draft.Source) is { } sourceError)
            {
                errors[PersonalFields.Source] = sourceError;
            }

            if (PersonalMemoryFieldRules.SummaryError(draft.Summary) is { } summaryError)
            {
                errors[PersonalFields.Summary] = summaryError;
            }

            var photos = draft.ExtraPhotos ?? [];

            if (photos.Count > 0 && draft.Image == PersonalImageChoice.None)
            {
                errors[PersonalFields.Image] = "The first photo is the cover: a memory with photos needs one.";
            }
            else if (photos.Any(p => p is null || p.Image == PersonalImageChoice.None))
            {
                errors[PersonalFields.Image] = "Every photo needs an image.";
            }
            else if (photos.Select(p => PersonalMemoryFieldRules.CaptionError(p.Caption)).FirstOrDefault(e => e is not null) is { } captionError)
            {
                errors[PersonalFields.Image] = captionError;
            }

            return errors;
        }

        /// <summary>
        /// Why a memory cannot be shown every year (CH-53), or <see langword="null"/> when it can or the switch is
        /// off: Every year needs a memory dated to the day without an end date (the store checks it is in a
        /// personal collection).
        /// </summary>
        public static string? EveryYearError(bool everyYear, PersonalDatePrecision precision, DateOnly? end) =>
            PersonalMemoryFieldRules.EveryYearError(everyYear, Shared(precision), end);

        /// <summary>A date as the desktop lists it: "2 January 2018", "January 2018", or a range "… to …".</summary>
        public static string DateText(string precision, DateTime start, DateTime? end)
        {
            var from = DateOf(precision, start);

            return end is { } last && DateOf(precision, last) is var to && to != from ? from + " to " + to : from;
        }

        private static string DateOf(string precision, DateTime value) => precision switch
        {
            MemoriesDatabase.PrecisionMonth => value.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            MemoriesDatabase.PrecisionSecond => value.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture),
            _ => value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture),
        };
    }
}
