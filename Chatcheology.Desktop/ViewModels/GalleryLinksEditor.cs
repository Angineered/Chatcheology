using System.Collections.ObjectModel;
using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Data.Memories.Personal;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>One gallery link in a form (CH-48): an address, an optional label and its own problem.</summary>
    public sealed class GalleryLinkRowViewModel : ObservableObject
    {
        private readonly GalleryLinksEditor _editor;
        private string _url;
        private string _label;
        private string _urlError = string.Empty;
        private string _labelError = string.Empty;

        internal GalleryLinkRowViewModel(GalleryLinksEditor editor, string url, string? label)
        {
            _editor = editor;
            _url = url;
            _label = label ?? string.Empty;

            MoveUpCommand = new RelayCommand(() => _editor.Move(this, -1), () => _editor.CanMove(this, -1));
            MoveDownCommand = new RelayCommand(() => _editor.Move(this, +1), () => _editor.CanMove(this, +1));
            RemoveCommand = new RelayCommand(() => _editor.Remove(this), () => _editor.CanEdit);
        }

        /// <summary>The address as typed.</summary>
        public string Url
        {
            get => _url;
            set
            {
                if (SetProperty(ref _url, value ?? string.Empty))
                {
                    UrlError = string.Empty;
                    _editor.ClearListError();
                }
            }
        }

        /// <summary>The label as typed (optional).</summary>
        public string Label
        {
            get => _label;
            set
            {
                if (SetProperty(ref _label, value ?? string.Empty))
                {
                    LabelError = string.Empty;
                    _editor.ClearListError();
                }
            }
        }

        /// <summary>The address's problem, or empty. Never repeats the address.</summary>
        public string UrlError { get => _urlError; internal set => SetProperty(ref _urlError, value); }

        /// <summary>The label's problem, or empty.</summary>
        public string LabelError { get => _labelError; internal set => SetProperty(ref _labelError, value); }

        /// <summary>"Link 2": the row's place in the list.</summary>
        public string Heading => "Link " + (_editor.Rows.IndexOf(this) + 1).ToString(CultureInfo.CurrentCulture);

        /// <summary>One place earlier.</summary>
        public RelayCommand MoveUpCommand { get; }

        /// <summary>One place later.</summary>
        public RelayCommand MoveDownCommand { get; }

        /// <summary>Drops the row (nothing is written until Save).</summary>
        public RelayCommand RemoveCommand { get; }

        internal void Refresh()
        {
            OnPropertyChanged(nameof(Heading));
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
            RemoveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// The "Gallery links" list of a memory form or a noted-date form (CH-48): up to 10 rows, each an address
    /// and an optional label, which can be added, removed and moved. Every rule is
    /// <see cref="ExternalLinkRules"/>'s. Nothing is fetched; nothing is written until the form saves.
    /// </summary>
    public sealed class GalleryLinksEditor : ObservableObject
    {
        /// <summary>The line shown under the list.</summary>
        public const string Notice =
            "Links open outside Chatcheology and can stop working. Anyone with the archive can open them. " +
            "Photos added to the memory stay in the archive.";

        private readonly Func<bool> _canEdit;
        private string _listError = string.Empty;

        internal GalleryLinksEditor(IEnumerable<ExternalLink>? existing, Func<bool> canEdit)
        {
            _canEdit = canEdit;
            AddCommand = new RelayCommand(Add, () => CanEdit && Rows.Count < ExternalLinkRules.MaxLinksPerOwner);
            Rows.CollectionChanged += (_, _) => RowsChanged();

            foreach (var link in existing ?? [])
            {
                Rows.Add(new GalleryLinkRowViewModel(this, link.Url, link.Label));
            }
        }

        /// <summary>The links in order.</summary>
        public ObservableCollection<GalleryLinkRowViewModel> Rows { get; } = [];

        /// <summary>Adds an empty row (up to 10).</summary>
        public RelayCommand AddCommand { get; }

        /// <summary>The line under the list.</summary>
        public string NoticeText => Notice;

        /// <summary>"3 of 10 links".</summary>
        public string CountText =>
            Rows.Count.ToString(CultureInfo.CurrentCulture) + " of " +
            ExternalLinkRules.MaxLinksPerOwner.ToString(CultureInfo.CurrentCulture) + " links";

        /// <summary>A problem with the list as a whole (too many links, or a failed save), or empty.</summary>
        public string ListError { get => _listError; private set => SetProperty(ref _listError, value); }

        internal bool CanEdit => _canEdit();

        /// <summary>The list as it would be saved.</summary>
        internal IReadOnlyList<ExternalLinkDraft> Drafts() =>
            Rows.Select(r => new ExternalLinkDraft(r.Url, r.Label)).ToList();

        /// <summary>
        /// Checks every row and shows each problem on its row (a Save attempt reveals them all).
        /// True when the list may be saved.
        /// </summary>
        internal bool Validate()
        {
            ClearErrors();
            var ok = true;

            if (ExternalLinkRules.CountError(Rows.Count) is { } count)
            {
                ListError = count;
                ok = false;
            }

            foreach (var row in Rows)
            {
                if (ExternalLinkRules.UrlError(ExternalLinkRules.NormaliseUrl(row.Url)) is { } url)
                {
                    row.UrlError = url;
                    ok = false;
                }

                if (row.Label.Length > 0 && ExternalLinkRules.LabelError(ExternalLinkRules.NormaliseLabel(row.Label) ?? row.Label) is { } label)
                {
                    row.LabelError = label;
                    ok = false;
                }
            }

            // The store's own check, once more: it is the rule the save applies.
            if (ok && ExternalLinkStore.Validate(Drafts()) is { } error)
            {
                Show(error.Index, error.Message);
                ok = false;
            }

            return ok;
        }

        /// <summary>Shows a refusal from the link store on its row (or on the list, when it has no index).</summary>
        internal void ShowStoreError(PersonalMemoryValidationException exception)
        {
            Show(exception.LinkIndex, exception.Errors.TryGetValue(PersonalFields.Links, out var text) ? text : string.Join(" ", exception.Errors.Values));
        }

        internal void ShowListError(string message) => ListError = message;

        internal void ClearErrors()
        {
            ListError = string.Empty;

            foreach (var row in Rows)
            {
                row.UrlError = string.Empty;
                row.LabelError = string.Empty;
            }
        }

        internal void ClearListError()
        {
            if (ListError.Length > 0)
            {
                ListError = string.Empty;
            }
        }

        internal void RaiseCommandStates()
        {
            AddCommand.RaiseCanExecuteChanged();

            foreach (var row in Rows)
            {
                row.Refresh();
            }
        }

        internal bool CanMove(GalleryLinkRowViewModel row, int offset)
        {
            var index = Rows.IndexOf(row);

            return CanEdit && index >= 0 && index + offset >= 0 && index + offset < Rows.Count;
        }

        internal void Move(GalleryLinkRowViewModel row, int offset)
        {
            if (CanMove(row, offset))
            {
                var index = Rows.IndexOf(row);
                Rows.Move(index, index + offset);
            }
        }

        internal void Remove(GalleryLinkRowViewModel row)
        {
            if (CanEdit)
            {
                Rows.Remove(row);
            }
        }

        private void Add()
        {
            if (CanEdit && Rows.Count < ExternalLinkRules.MaxLinksPerOwner)
            {
                Rows.Add(new GalleryLinkRowViewModel(this, string.Empty, null));
            }
        }

        private void Show(int? index, string message)
        {
            // The store's message starts "Link n: "; on a row the row's own heading says which.
            var prefix = index is { } i ? "Link " + (i + 1).ToString(CultureInfo.CurrentCulture) + ": " : string.Empty;
            var text = prefix.Length > 0 && message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;

            if (index is { } at && at >= 0 && at < Rows.Count)
            {
                if (text.Contains("label", StringComparison.OrdinalIgnoreCase))
                {
                    Rows[at].LabelError = text;
                }
                else
                {
                    Rows[at].UrlError = text;
                }
            }
            else
            {
                ListError = text;
            }
        }

        private void RowsChanged()
        {
            ClearListError();
            OnPropertyChanged(nameof(CountText));
            RaiseCommandStates();
        }
    }

    /// <summary>The gallery text of a list line: "Gallery · host" or "N galleries".</summary>
    internal static class GalleryWording
    {
        /// <summary>Empty for none; "Gallery · photos.example" for one; "3 galleries" for several.</summary>
        internal static string For(IReadOnlyList<ExternalLink> links) => links.Count switch
        {
            0 => string.Empty,
            1 => links[0].Host is { } host ? "Gallery · " + host : "Gallery",
            _ => links.Count.ToString(CultureInfo.CurrentCulture) + " galleries",
        };
    }
}
