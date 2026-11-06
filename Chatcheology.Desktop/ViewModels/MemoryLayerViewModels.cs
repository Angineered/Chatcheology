using System.Collections.Concurrent;
using System.Windows.Media;
using Chatcheology.Desktop.Services;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// One memory row in the conversation list (CH-42 step 5): the phone's row (same placement, content
    /// and wording), with its date separator, its decoded image and whether it is selected.
    /// </summary>
    /// <remarks>
    /// Never a message: it has no sequence number, no sender and no attachment, and selecting it opens
    /// the memory's details only. It never selects or changes a message for review.
    /// </remarks>
    public sealed class MemoryRowViewModel : ObservableObject
    {
        private DateOnly? _dateSeparator;
        private bool _isSelected;

        /// <param name="placement">Where the shared layout placed the row.</param>
        /// <param name="images">Decoded images by file, shared by the rows of one load.</param>
        internal MemoryRowViewModel(MemoryPlacement placement, ConcurrentDictionary<string, ImageSource?> images)
        {
            ArgumentNullException.ThrowIfNull(placement);

            Row = placement.CreateRow();
            Key = placement.Key;
            After = placement.After;
            Day = Row.Day;

            switch (Row)
            {
                case PeriodCardRow card:
                    IsPeriodCard = true;
                    Title = card.Title;
                    DateText = card.DateText;
                    Lines = card.ParticipantLines;
                    LabelText = card.LabelText;
                    SourceText = card.SourceText;
                    Image = MemoryImages.Load(card.ImagePath, images);
                    break;

                case EntryCardRow card:
                    IsEntryCard = true;
                    Title = card.Title;
                    DateText = card.DateText;
                    Summary = card.Summary;
                    SourceText = card.SourceText;
                    CollectionName = card.CollectionName;
                    Image = MemoryImages.Load(card.ImagePath, images);
                    ExtraPhotosBadge = card.ExtraPhotosBadge;
                    break;

                case DifferentTimesRow line:
                    IsDifferentTimes = true;
                    Text = line.Text;
                    break;
            }
        }

        /// <summary>The phone's row this shows.</summary>
        internal MemoryRow Row { get; }

        /// <summary>Which entry (and period) the row shows.</summary>
        public MemoryKey Key { get; }

        /// <summary>The sequence number of the message the row follows; 0 before the first.</summary>
        public int After { get; }

        /// <summary>The row's local day.</summary>
        public DateOnly Day { get; }

        /// <summary>A PSN period card.</summary>
        public bool IsPeriodCard { get; }

        /// <summary>A memory card at its start (a personal memory).</summary>
        public bool IsEntryCard { get; }

        /// <summary>The muted "different times" line.</summary>
        public bool IsDifferentTimes { get; }

        /// <summary>The title.</summary>
        public string Title { get; } = string.Empty;

        /// <summary>The day, or a personal memory's date or range.</summary>
        public string DateText { get; } = string.Empty;

        /// <summary>A period card's lines: each participant's trophy count and first-to-last time.</summary>
        public IReadOnlyList<string> Lines { get; } = [];

        /// <summary>"This period: …".</summary>
        public string LabelText { get; } = string.Empty;

        /// <summary>The source line, or empty.</summary>
        public string SourceText { get; } = string.Empty;

        /// <summary>Whether there is a source line.</summary>
        public bool HasSource => SourceText.Length > 0;

        /// <summary>A personal memory's description, or empty.</summary>
        public string Summary { get; } = string.Empty;

        /// <summary>Whether there is a description.</summary>
        public bool HasSummary => Summary.Length > 0;

        /// <summary>A personal memory's collection, such as "Holidays".</summary>
        public string CollectionName { get; } = string.Empty;

        /// <summary>The "different times" line.</summary>
        public string Text { get; } = string.Empty;

        /// <summary>The decoded image, or <see langword="null"/>.</summary>
        public ImageSource? Image { get; }

        /// <summary>Whether there is an image to draw.</summary>
        public bool HasImage => Image is not null;

        /// <summary>A personal card's "+4" badge for its extra photos (CH-46), or empty.</summary>
        public string ExtraPhotosBadge { get; } = string.Empty;

        /// <summary>Whether the badge shows: a cover is drawn and the memory has extra photos.</summary>
        public bool HasExtraPhotos => HasImage && ExtraPhotosBadge.Length > 0;

        /// <summary>Whether this row begins a new calendar day on the page.</summary>
        public bool HasDateSeparator => _dateSeparator is not null;

        /// <summary>The separator's text, or empty.</summary>
        public string DateSeparatorText => MessageViewModel.SeparatorText(_dateSeparator);

        /// <summary>Whether the row's details are showing.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            internal set => SetProperty(ref _isSelected, value);
        }

        internal void SetDateSeparator(DateOnly? day)
        {
            if (_dateSeparator != day)
            {
                _dateSeparator = day;
                OnPropertyChanged(nameof(HasDateSeparator));
                OnPropertyChanged(nameof(DateSeparatorText));
            }
        }
    }

    /// <summary>One filter of an imported layer, as the Layers control shows it.</summary>
    public sealed class MemoryFilterViewModel : ObservableObject
    {
        private readonly Action _changed;
        private bool _isOn;

        internal MemoryFilterViewModel(long collectionID, string filter, bool isOn, Action changed)
        {
            CollectionID = collectionID;
            Filter = filter;
            _isOn = isOn;
            _changed = changed;
        }

        /// <summary>The collection.</summary>
        public long CollectionID { get; }

        /// <summary>The stored word (<see cref="MemoryFilters"/>).</summary>
        public string Filter { get; }

        /// <summary>How the phone's Settings names it.</summary>
        public string Title => MemoryFilters.Title(Filter);

        /// <summary>Whether the filter is on.</summary>
        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (SetProperty(ref _isOn, value))
                {
                    _changed();
                }
            }
        }
    }

    /// <summary>One collection's switch in the Layers control, with its filters when it is imported.</summary>
    public sealed class MemoryLayerViewModel : ObservableObject
    {
        private readonly Action _changed;
        private bool _isShown;

        internal MemoryLayerViewModel(
            long collectionID, string name, bool isShown, IReadOnlyList<(string Filter, bool IsOn)> filters, Action changed)
        {
            CollectionID = collectionID;
            Name = name;
            _isShown = isShown;
            _changed = changed;
            Filters = filters.Select(f => new MemoryFilterViewModel(collectionID, f.Filter, f.IsOn, changed)).ToList();
        }

        /// <summary>The collection.</summary>
        public long CollectionID { get; }

        /// <summary>Its name.</summary>
        public string Name { get; }

        /// <summary>The filters (an imported collection's); empty for a personal one.</summary>
        public IReadOnlyList<MemoryFilterViewModel> Filters { get; }

        /// <summary>Whether there are filters.</summary>
        public bool HasFilters => Filters.Count > 0;

        /// <summary>Whether the layer is on.</summary>
        public bool IsShown
        {
            get => _isShown;
            set
            {
                if (SetProperty(ref _isShown, value))
                {
                    _changed();
                }
            }
        }
    }

    /// <summary>One trophy in the details.</summary>
    public sealed class MemoryItemViewModel
    {
        internal MemoryItemViewModel(MemoryItemLine item, ConcurrentDictionary<string, ImageSource?> images)
        {
            Name = item.Name;
            Meta = MemoryWording.ItemMeta(item);
            Detail = item.Detail;
            Icon = MemoryImages.Load(item.IconPath, images);
        }

        /// <summary>The definition's name, or "Bronze trophy #12".</summary>
        public string Name { get; }

        /// <summary>"20:18:03 · Alex", with " · hidden trophy" when marked hidden.</summary>
        public string Meta { get; }

        /// <summary>The definition's detail, or empty.</summary>
        public string Detail { get; }

        /// <summary>Whether there is a detail line.</summary>
        public bool HasDetail => Detail.Length > 0;

        /// <summary>The decoded icon, or <see langword="null"/>.</summary>
        public ImageSource? Icon { get; }

        /// <summary>Whether there is an icon to draw.</summary>
        public bool HasIcon => Icon is not null;
    }

    /// <summary>One photo in the memory details' strip (CH-46): its thumbnail and caption.</summary>
    public sealed class MemoryStripPhotoViewModel
    {
        internal MemoryStripPhotoViewModel(int index, MemoryPhoto photo, ConcurrentDictionary<string, ImageSource?> images)
        {
            Index = index;
            Thumbnail = MemoryImages.Load(photo.Path, images);
            Caption = photo.CaptionText;
        }

        /// <summary>Its place among the memory's photos (0 is the cover).</summary>
        public int Index { get; }

        /// <summary>The decoded thumbnail, or <see langword="null"/>.</summary>
        public ImageSource? Thumbnail { get; }

        /// <summary>The caption, or empty.</summary>
        public string Caption { get; }

        /// <summary>Whether there is a caption.</summary>
        public bool HasCaption => Caption.Length > 0;
    }

    /// <summary>
    /// The large photo view of one memory (CH-46): the desktop's stand-in for the phone's full-screen
    /// viewer. Previous and Next move through this memory's photos only, and stop at either end.
    /// </summary>
    public sealed class MemoryPhotoViewerViewModel : ObservableObject
    {
        private readonly MemoryPhotoBrowser _browser;
        private readonly Func<string, ImageSource?> _load;
        private ImageSource? _image;

        /// <param name="browser">The memory's photos, at the one opened.</param>
        /// <param name="close">Closes the view.</param>
        /// <param name="load">Decodes a photo file for the large view.</param>
        internal MemoryPhotoViewerViewModel(MemoryPhotoBrowser browser, Action close, Func<string, ImageSource?>? load = null)
        {
            _browser = browser ?? throw new ArgumentNullException(nameof(browser));
            _load = load ?? (path => MediaPresentation.TryLoadImage(path, MediaPresentation.LargeWidth));

            PreviousCommand = new RelayCommand(() => Move(_browser.Previous()), () => _browser.HasPrevious);
            NextCommand = new RelayCommand(() => Move(_browser.Next()), () => _browser.HasNext);
            CloseCommand = new RelayCommand(close ?? throw new ArgumentNullException(nameof(close)));

            _image = _load(_browser.Current.Path!);
        }

        /// <summary>The photos it moves through.</summary>
        internal MemoryPhotoBrowser Browser => _browser;

        /// <summary>"Photo 2 of 5", or "Photo".</summary>
        public string Title => _browser.Title;

        /// <summary>The photo shown, decoded large; <see langword="null"/> when it does not decode.</summary>
        public ImageSource? Image
        {
            get => _image;
            private set => SetProperty(ref _image, value);
        }

        /// <summary>Whether the photo decoded.</summary>
        public bool HasImage => Image is not null;

        /// <summary>Whether the photo did not decode (a line says so).</summary>
        public bool CannotShow => Image is null;

        /// <summary>The photo's caption, or empty.</summary>
        public string Caption => _browser.Current.CaptionText;

        /// <summary>Whether there is a caption.</summary>
        public bool HasCaption => Caption.Length > 0;

        /// <summary>The previous photo of this memory.</summary>
        public RelayCommand PreviousCommand { get; }

        /// <summary>The next photo of this memory.</summary>
        public RelayCommand NextCommand { get; }

        /// <summary>Back to the details.</summary>
        public RelayCommand CloseCommand { get; }

        private void Move(bool moved)
        {
            if (!moved)
            {
                return;
            }

            Image = _load(_browser.Current.Path!);
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(CannotShow));

            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Caption));
            OnPropertyChanged(nameof(HasCaption));
            PreviousCommand.RaiseCanExecuteChanged();
            NextCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// What the details area shows for a selected memory row (CH-42 step 5): the phone's
    /// <see cref="MemoryDetails"/>, laid out with the phone's headings.
    /// </summary>
    public sealed class MemoryDetailsViewModel
    {
        internal MemoryDetailsViewModel(MemoryDetails details, bool isPersonal, ConcurrentDictionary<string, ImageSource?> images)
        {
            ArgumentNullException.ThrowIfNull(details);

            Details = details;
            Key = details.Key;
            IsPersonal = isPersonal;
            Title = details.Title;
            DateText = details.DateText;
            CollectionName = details.CollectionName;
            Image = MemoryImages.Load(details.ImagePath, images);
            Photos = details.HasPhotoStrip
                ? details.Photos.Select((photo, index) => new MemoryStripPhotoViewModel(index, photo, images)).ToList()
                : [];
            Summary = details.Summary;
            Lines = details.Lines;
            Items = details.Items.Select(i => new MemoryItemViewModel(i, images)).ToList();
            DayEvidenceText = details.DayEvidenceText;
            OtherPeriodsHeading = MemoryWording.OtherPeriodsHeading(details.Key.PeriodIndex is not null);
            OtherPeriods = details.OtherPeriods;
            Links = details.Links;
            SourceLine = details.SourceText.Length > 0 ? MemoryWording.SourceLine(details.SourceText) : string.Empty;
        }

        /// <summary>The phone's details this lays out.</summary>
        internal MemoryDetails Details { get; }

        /// <summary>The row the details belong to.</summary>
        public MemoryKey Key { get; }

        /// <summary>Whether this is a personal memory (it can be edited).</summary>
        public bool IsPersonal { get; }

        /// <summary>The title.</summary>
        public string Title { get; }

        /// <summary>The day, or a personal memory's date or range.</summary>
        public string DateText { get; }

        /// <summary>The layer.</summary>
        public string CollectionName { get; }

        /// <summary>The decoded image, or <see langword="null"/>.</summary>
        public ImageSource? Image { get; }

        /// <summary>Whether there is an image.</summary>
        public bool HasImage => Image is not null;

        /// <summary>
        /// The strip of every photo (the cover first) when the memory has more than one (CH-46); empty
        /// otherwise, when the single image shows as before.
        /// </summary>
        public IReadOnlyList<MemoryStripPhotoViewModel> Photos { get; }

        /// <summary>Whether the strip shows.</summary>
        public bool HasPhotoStrip => Photos.Count > 0;

        /// <summary>Whether the single image shows (an image, and no strip).</summary>
        public bool ShowsSingleImage => HasImage && !HasPhotoStrip;

        /// <summary>A personal memory's description, or empty.</summary>

        public string Summary { get; }

        /// <summary>Whether there is a description.</summary>
        public bool HasSummary => Summary.Length > 0;

        /// <summary>The card's lines: each participant's count and times, and "This period: …".</summary>
        public IReadOnlyList<string> Lines { get; }

        /// <summary>"Trophies".</summary>
        public string ItemsHeading => MemoryWording.ItemsHeading;

        /// <summary>The trophies with exact local times.</summary>
        public IReadOnlyList<MemoryItemViewModel> Items { get; }

        /// <summary>Whether there are trophies.</summary>
        public bool HasItems => Items.Count > 0;

        /// <summary>"This day".</summary>
        public string DayHeading => MemoryWording.DayHeading;

        /// <summary>"Day evidence: …", or empty.</summary>
        public string DayEvidenceText { get; }

        /// <summary>Whether there is day evidence.</summary>
        public bool HasDayEvidence => DayEvidenceText.Length > 0;

        /// <summary>"Other periods this day:" or "Periods this day:".</summary>
        public string OtherPeriodsHeading { get; }

        /// <summary>The day's other periods as short lines.</summary>
        public IReadOnlyList<string> OtherPeriods { get; }

        /// <summary>Whether there are other periods.</summary>
        public bool HasOtherPeriods => OtherPeriods.Count > 0;

        /// <summary>"Linked messages".</summary>
        public string LinksHeading => MemoryWording.LinksHeading;

        /// <summary>A personal memory's links.</summary>
        public IReadOnlyList<MemoryLinkLine> Links { get; }

        /// <summary>Whether there are links.</summary>
        public bool HasLinks => Links.Count > 0;

        /// <summary>"Source: …", or empty.</summary>
        public string SourceLine { get; }

        /// <summary>Whether there is a source line.</summary>
        public bool HasSource => SourceLine.Length > 0;
    }

    /// <summary>Decodes memory images and trophy icons once per load, off the dispatcher where possible.</summary>
    internal static class MemoryImages
    {
        internal static ImageSource? Load(string? path, ConcurrentDictionary<string, ImageSource?> images) =>
            path is null ? null : images.GetOrAdd(path, file => MediaPresentation.TryLoadThumbnail(file));
    }
}
