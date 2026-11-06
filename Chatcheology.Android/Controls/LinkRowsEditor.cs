using Chatcheology.Archive;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.OwnMemories;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// The ordered link rows of a form (CH-48's editor, moved here from the removed noted-date page for CH-54): up to ten,
    /// each an address and an optional label, with move up / down and remove; the typed text stays with its row. Each
    /// row shows its own problem; the rules are the shared <see cref="ExternalLinkRules"/> (checked by the form).
    /// </summary>
    internal sealed class LinkRowsEditor : VerticalStackLayout
    {
        private readonly List<OwnMemoryFormLink> _links;
        private readonly Action _changed;
        private readonly List<(Entry Url, Entry Label, Label Error)> _rows = [];
        private readonly VerticalStackLayout _rowViews = new() { Spacing = 8 };
        private readonly Button _add = new() { Text = "Add a link", HorizontalOptions = LayoutOptions.Start };

        /// <param name="links">The form's link rows, edited in place.</param>
        /// <param name="changed">Called after any change, so the form can tell it has unsaved changes.</param>
        internal LinkRowsEditor(List<OwnMemoryFormLink> links, Action changed)
        {
            _links = links;
            _changed = changed;
            Spacing = 8;

            _add.Clicked += (_, _) =>
            {
                if (_links.Count < ExternalLinkRules.MaxLinksPerOwner)
                {
                    _links.Add(new OwnMemoryFormLink(null, null));
                    Rebuild();
                    _changed();
                    _rows[^1].Url.Focus();
                }
            };

            Children.Add(_rowViews);
            Children.Add(_add);
            Children.Add(new Label { Text = GalleryLinks.OwnLinksNote, FontSize = 12 }.TextIn("MutedText"));
            Rebuild();
        }

        /// <summary>Shows each row's problem (by its index), or clears it.</summary>
        internal void ShowErrors(IReadOnlyDictionary<int, string> rowErrors)
        {
            for (var index = 0; index < _rows.Count; index++)
            {
                var error = rowErrors.GetValueOrDefault(index);
                _rows[index].Error.Text = error;
                _rows[index].Error.IsVisible = error is not null;
            }
        }

        /// <summary>Lays out the rows in order; Add is offered until there are ten.</summary>
        private void Rebuild()
        {
            _rowViews.Children.Clear();
            _rows.Clear();

            for (var index = 0; index < _links.Count; index++)
            {
                var link = _links[index];
                var at = index;

                var url = new Entry { Placeholder = "https://…", Keyboard = Keyboard.Url, Text = link.Url };

                // No MaxLength: Android would silently cut a longer paste, and a cut link usually still passes the
                // rules. The shared rule reports a link that is too long instead; the label likewise.
                var label = new Entry { Placeholder = "Label (optional)", Text = link.Label };
                var error = FormParts.Error();
                SemanticProperties.SetDescription(url, $"Link {at + 1} address");
                SemanticProperties.SetDescription(label, $"Link {at + 1} label");

                url.TextChanged += (_, e) =>
                {
                    link.Url = e.NewTextValue;
                    _changed();
                };
                label.TextChanged += (_, e) =>
                {
                    link.Label = e.NewTextValue;
                    _changed();
                };

                var up = FormParts.SmallButton("Up", "Move link up");
                var down = FormParts.SmallButton("Down", "Move link down");
                var remove = FormParts.SmallButton("Remove", "Remove link");
                up.IsEnabled = at > 0;
                down.IsEnabled = at < _links.Count - 1;
                up.Clicked += (_, _) => Move(at, at - 1);
                down.Clicked += (_, _) => Move(at, at + 1);
                remove.Clicked += (_, _) =>
                {
                    _links.RemoveAt(at);
                    Rebuild();
                    _changed();
                };

                _rows.Add((url, label, error));
                _rowViews.Add(new VerticalStackLayout
                {
                    Spacing = 2,
                    Children = { url, label, error, new HorizontalStackLayout { Spacing = 6, Children = { up, down, remove } } },
                });
            }

            _add.IsEnabled = _links.Count < ExternalLinkRules.MaxLinksPerOwner;
        }

        private void Move(int from, int to)
        {
            if (to < 0 || to >= _links.Count)
            {
                return;
            }

            (_links[from], _links[to]) = (_links[to], _links[from]);
            Rebuild();
            _changed();
        }
    }

    /// <summary>Small pieces the forms share.</summary>
    internal static class FormParts
    {
        /// <summary>A field's problem line, hidden while empty.</summary>
        internal static Label Error() => new Label { FontSize = 13, IsVisible = false }.TextIn("ErrorText");

        /// <summary>Shows <paramref name="text"/> in <paramref name="label"/>, or hides it.</summary>
        internal static void Show(Label label, string? text)
        {
            label.Text = text;
            label.IsVisible = !string.IsNullOrEmpty(text);
        }

        /// <summary>A small button with a screen-reader description.</summary>
        internal static Button SmallButton(string text, string description)
        {
            var button = new Button { Text = text, Padding = new Thickness(10, 0), HeightRequest = 36, FontSize = 13 };
            SemanticProperties.SetDescription(button, description);

            return button;
        }

        /// <summary>A muted caption over a field.</summary>
        internal static Label Caption(string text) => new Label { Text = text, FontSize = 13 }.TextIn("MutedText");
    }
}
