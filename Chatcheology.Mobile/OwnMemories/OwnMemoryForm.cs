using Chatcheology.Archive;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>One photo on the add / edit form: a stored file, where it is, and its caption as typed.</summary>
    public sealed class OwnMemoryFormPhoto
    {
        /// <summary>A photo of the form.</summary>
        public OwnMemoryFormPhoto(string fileName, string path, string? caption)
        {
            FileName = fileName;
            Path = path;
            Caption = caption;
        }

        /// <summary>The stored file's name (<c>&lt;sha256&gt;.&lt;ext&gt;</c>).</summary>
        public string FileName { get; }

        /// <summary>The file in the own photos folder, for the thumbnail.</summary>
        public string Path { get; }

        /// <summary>The caption as typed (trimmed when saved; blank is none).</summary>
        public string? Caption { get; set; }
    }

    /// <summary>One link row on the form, as typed.</summary>
    public sealed class OwnMemoryFormLink
    {
        /// <summary>A link row.</summary>
        public OwnMemoryFormLink(string? url, string? label)
        {
            Url = url;
            Label = label;
        }

        /// <summary>The address as typed.</summary>
        public string? Url { get; set; }

        /// <summary>The label as typed.</summary>
        public string? Label { get; set; }
    }

    /// <summary>
    /// The outcome of checking the form (<see cref="OwnMemoryForm.Check"/>): every problem at once, by field, and each
    /// link row's own problem; the draft to save when there is none.
    /// </summary>
    /// <param name="Errors">One message per field (<see cref="OwnMemoryRules.Validate"/>), plus the photo files' check.</param>
    /// <param name="LinkRowErrors">Each link row's problem, by its index on the form (<see cref="GalleryLinks.Check"/>).</param>
    /// <param name="Draft">What to save, or <see langword="null"/> when there is a problem.</param>
    public sealed record OwnMemoryFormCheck(
        IReadOnlyDictionary<OwnMemoryField, string> Errors, IReadOnlyDictionary<int, string> LinkRowErrors, OwnMemoryDraft? Draft)
    {
        /// <summary>Whether the form may be saved.</summary>
        public bool IsValid => Draft is not null;
    }

    /// <summary>
    /// The add / edit form of an own memory (CH-54 step 2), without any screen: its fields as typed, the Every year rule
    /// (only Day with no end), photo order (the first is the cover), up to ten links, whether anything changed since it
    /// opened, and the check that shows every problem at once. The phone's screen draws it.
    /// </summary>
    public sealed class OwnMemoryForm
    {
        private readonly string _opened;
        private MemoryDatePrecision _precision;
        private DateOnly? _end;
        private bool _everyYear;

        /// <summary>A form to add a memory, starting on <paramref name="day"/> (today, or the day it was opened from).</summary>
        public OwnMemoryForm(DateOnly day)
        {
            Start = day;
            _precision = MemoryDatePrecision.Day;
            Title = string.Empty;
            _opened = Snapshot();
        }

        /// <summary>A form to edit <paramref name="memory"/>, with its fields as stored.</summary>
        public OwnMemoryForm(OwnMemory memory)
        {
            ArgumentNullException.ThrowIfNull(memory);

            Id = memory.Id;
            Start = memory.Start;
            _end = memory.End;
            _precision = memory.Precision;
            Title = memory.Title;
            Summary = memory.Summary;
            _everyYear = memory.EveryYear;
            Photos.AddRange(memory.Photos.Select(photo => new OwnMemoryFormPhoto(photo.FileName, photo.Path, photo.Caption)));
            Links.AddRange(memory.Links.Select(link => new OwnMemoryFormLink(link.Url, link.Label)));
            _opened = Snapshot();
        }

        /// <summary>The memory edited, or <see langword="null"/> for a new one.</summary>
        public Guid? Id { get; }

        /// <summary>Whether the form adds a memory (rather than editing one).</summary>
        public bool IsNew => Id is null;

        /// <summary>The start date (for Month, only its year and month count).</summary>
        public DateOnly? Start { get; set; }

        /// <summary>The optional end date. Setting one turns Every year off.</summary>
        public DateOnly? End
        {
            get => _end;
            set
            {
                _end = value;
                ClearEveryYearWhenNotAllowed();
            }
        }

        /// <summary>Day or Month. Month turns Every year off.</summary>
        public MemoryDatePrecision Precision
        {
            get => _precision;
            set
            {
                _precision = value;
                ClearEveryYearWhenNotAllowed();
            }
        }

        /// <summary>The title as typed.</summary>
        public string Title { get; set; }

        /// <summary>The summary as typed.</summary>
        public string? Summary { get; set; }

        /// <summary>Every year: on only while <see cref="CanEveryYear"/>; setting it otherwise leaves it off.</summary>
        public bool EveryYear
        {
            get => _everyYear;
            set => _everyYear = value && CanEveryYear;
        }

        /// <summary>Whether Every year may be switched on: Day precision without an end date.</summary>
        public bool CanEveryYear => _precision == MemoryDatePrecision.Day && _end is null;

        /// <summary>The short reason Every year is off and cannot be switched on, or <see langword="null"/> when it can.</summary>
        public string? EveryYearReason =>
            _precision != MemoryDatePrecision.Day ? "Only for a memory dated to the day."
            : _end is not null ? "Only for a memory without an end date."
            : null;

        /// <summary>The photos in order; the first is the cover.</summary>
        public List<OwnMemoryFormPhoto> Photos { get; } = [];

        /// <summary>The link rows in order.</summary>
        public List<OwnMemoryFormLink> Links { get; } = [];

        /// <summary>Whether another link row may be added (at most <see cref="ExternalLinkRules.MaxLinksPerOwner"/>).</summary>
        public bool CanAddLink => Links.Count < ExternalLinkRules.MaxLinksPerOwner;

        /// <summary>Whether anything differs from when the form opened (Back then asks first).</summary>
        public bool HasChanges => Snapshot() != _opened;

        /// <summary>Adds a stored photo at the end. False when it is already on the form (each photo once).</summary>
        public bool AddPhoto(string fileName, string path)
        {
            if (Photos.Any(photo => photo.FileName == fileName))
            {
                return false;
            }

            Photos.Add(new OwnMemoryFormPhoto(fileName, path, null));
            return true;
        }

        /// <summary>Moves the photo at <paramref name="from"/> to <paramref name="to"/> (the first is the cover). False when either is out of range.</summary>
        public bool MovePhoto(int from, int to) => Move(Photos, from, to);

        /// <summary>Removes the photo at <paramref name="index"/> from the form (its file goes when nothing uses it).</summary>
        public void RemovePhoto(int index)
        {
            if (index >= 0 && index < Photos.Count)
            {
                Photos.RemoveAt(index);
            }
        }

        /// <summary>Adds an empty link row; false at ten.</summary>
        public bool AddLink()
        {
            if (!CanAddLink)
            {
                return false;
            }

            Links.Add(new OwnMemoryFormLink(null, null));
            return true;
        }

        /// <summary>Moves a link row.</summary>
        public bool MoveLink(int from, int to) => Move(Links, from, to);

        /// <summary>Removes a link row.</summary>
        public void RemoveLink(int index)
        {
            if (index >= 0 && index < Links.Count)
            {
                Links.RemoveAt(index);
            }
        }

        /// <summary>
        /// Checks every field with the shared rules (<see cref="OwnMemoryRules.Validate"/>, links per row with
        /// <see cref="GalleryLinks.Check"/>) and returns every problem at once; with none, the draft to save. Rows with
        /// neither address nor label are left out.
        /// </summary>
        public OwnMemoryFormCheck Check()
        {
            var links = GalleryLinks.Check(Links.Select(link => (link.Url, link.Label)).ToList());
            var draft = new OwnMemoryDraft
            {
                Start = Start,
                End = _end,
                Precision = _precision,
                Title = Title ?? string.Empty,
                Summary = Summary,
                EveryYear = _everyYear,
                Photos = Photos.Select(photo => new OwnPhotoDraft(photo.FileName, photo.Caption)).ToList(),
                Links = links.IsValid
                    ? links.Links.Select(link => new OwnLink(link.Url, link.Label)).ToList()
                    : [],
            };

            var errors = new Dictionary<OwnMemoryField, string>(OwnMemoryRules.Validate(draft));

            if (!links.IsValid)
            {
                // The rows carry their own problems; the field line says only what is not a row's (too many).
                errors.Remove(OwnMemoryField.Links);

                if (links.CountError is { } count)
                {
                    errors[OwnMemoryField.Links] = count;
                }
                else if (links.RowErrors.Count > 0)
                {
                    errors[OwnMemoryField.Links] = links.RowErrors.Count == 1 ? "A link needs fixing." : "Some links need fixing.";
                }
            }

            return errors.Count == 0
                ? new OwnMemoryFormCheck(errors, links.RowErrors, draft)
                : new OwnMemoryFormCheck(errors, links.RowErrors, null);
        }

        private void ClearEveryYearWhenNotAllowed()
        {
            if (!CanEveryYear)
            {
                _everyYear = false;
            }
        }

        private static bool Move<T>(List<T> list, int from, int to)
        {
            if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to)
            {
                return false;
            }

            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
            return true;
        }

        /// <summary>Every field as one text, to tell whether anything changed (in memory only, never logged).</summary>
        private string Snapshot() => string.Join(
            "\u001F",
            [
                Start?.ToString("O") ?? string.Empty,
                _end?.ToString("O") ?? string.Empty,
                _precision.ToString(),
                Title ?? string.Empty,
                Summary ?? string.Empty,
                _everyYear ? "1" : "0",
                .. Photos.Select(photo => photo.FileName + "\u001E" + (photo.Caption ?? string.Empty)),
                "\u001D",
                .. Links.Select(link => (link.Url ?? string.Empty) + "\u001E" + (link.Label ?? string.Empty)),
            ]);
    }
}
