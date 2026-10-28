using Chatcheology.Android.Controls;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The details of a memory row (CH-42): what <see cref="MemoryDetails"/> says, laid out. For a PSN
    /// period: its trophies with exact local times, names and icons (or "Bronze trophy #12"), then the day
    /// evidence and the day's other periods. For a personal memory: its description and links. Nothing to
    /// share; everything was read when the archive opened.
    /// </summary>
    internal sealed class MemoryDetailsPage : ContentPage
    {
        private readonly Func<int, Task> _showMessage;
        private readonly Func<long, Task> _showEntry;

        /// <param name="details">What to show.</param>
        /// <param name="showMessage">Opens the conversation at a linked message.</param>
        /// <param name="showEntry">Opens the conversation at a linked entry's row.</param>
        internal MemoryDetailsPage(MemoryDetails details, Func<int, Task> showMessage, Func<long, Task> showEntry)
        {
            _showMessage = showMessage;
            _showEntry = showEntry;

            Title = details.CollectionName;

            var body = new VerticalStackLayout { Padding = 16, Spacing = 6 };

            if (details.ImagePath is { } image)
            {
                body.Add(new DecodedImage { WidthRequest = 320, HeightRequest = 180, FailedText = string.Empty, HorizontalOptions = LayoutOptions.Start, Path = image });
            }

            body.Add(new Label { Text = details.Title, FontSize = 20, FontAttributes = FontAttributes.Bold });
            body.Add(Muted(details.DateText, 14));

            if (details.Summary.Length > 0)
            {
                body.Add(new Label { Text = details.Summary, FontSize = 15 });
            }

            foreach (var line in details.Lines)
            {
                body.Add(new Label { Text = line, FontSize = 15 });
            }

            if (details.Items.Count > 0)
            {
                body.Add(Heading(MemoryWording.ItemsHeading));

                foreach (var item in details.Items)
                {
                    body.Add(ItemView(item));
                }
            }

            if (details.DayEvidenceText.Length > 0)
            {
                body.Add(Heading(MemoryWording.DayHeading));
                body.Add(new Label { Text = details.DayEvidenceText, FontSize = 15 });
            }

            if (details.OtherPeriods.Count > 0)
            {
                body.Add(Muted(MemoryWording.OtherPeriodsHeading(details.Key.PeriodIndex is not null), 13));

                foreach (var period in details.OtherPeriods)
                {
                    body.Add(new Label { Text = period, FontSize = 14 });
                }
            }

            if (details.Links.Count > 0)
            {
                body.Add(Heading(MemoryWording.LinksHeading));

                foreach (var link in details.Links)
                {
                    body.Add(LinkView(link));
                }
            }

            if (details.SourceText.Length > 0)
            {
                body.Add(Muted(MemoryWording.SourceLine(details.SourceText), 12));
            }

            Content = new ScrollView { Content = body };
        }

        private static View ItemView(MemoryItemLine item)
        {
            var text = new VerticalStackLayout
            {
                Spacing = 0,
                Children =
                {
                    new Label { Text = item.Name, FontSize = 15, FontAttributes = FontAttributes.Bold },
                    Muted(MemoryWording.ItemMeta(item), 12),
                },
            };

            if (item.HasDetail)
            {
                text.Add(new Label { Text = item.Detail, FontSize = 13 });
            }

            var row = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                ColumnSpacing = 10,
                Padding = new Thickness(0, 4),
            };

            if (item.IconPath is { } icon)
            {
                row.Add(new DecodedImage { WidthRequest = 40, HeightRequest = 40, FailedText = string.Empty, VerticalOptions = LayoutOptions.Start, Path = icon }, 0);
            }

            row.Add(text, 1);

            return row;
        }

        private View LinkView(MemoryLinkLine link)
        {
            var label = new Label
            {
                Text = link.Text,
                FontSize = 15,
                TextDecorations = TextDecorations.Underline,
                Padding = new Thickness(0, 4),
            }.TextIn("LinkText");

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                try
                {
                    if (link.SequenceNumber is { } sequence)
                    {
                        await _showMessage(sequence);
                    }
                    else if (link.EntryID is { } entry)
                    {
                        await _showEntry(entry);
                    }
                }
                catch (Exception)
                {
                    // A tap handler: nothing here may end the app.
                }
            };

            label.GestureRecognizers.Add(tap);

            return label;
        }

        private static Label Heading(string text) =>
            new() { Text = text, FontSize = 17, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 10, 0, 0) };

        private static Label Muted(string text, double size) => new Label { Text = text, FontSize = size }.TextIn("MutedText");
    }
}
