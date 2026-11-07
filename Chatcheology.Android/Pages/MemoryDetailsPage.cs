using Chatcheology.Android.Controls;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The details of a memory row (CH-42): what <see cref="MemoryDetails"/> says, laid out. For a PSN
    /// period: its trophies with exact local times, names and icons (or "Bronze trophy #12"), then the day
    /// evidence and the day's other periods. For a personal memory: its photos (a strip with captions when
    /// there is more than one, CH-46; a tap opens the full-screen viewer over them), description, links and external
    /// links (CH-48, shown as "Links" since CH-59; each opens in another app only after a confirmation). Only a
    /// photo can be shared, from the viewer (CH-59); everything was read when the archive opened.
    /// <para>One of the phone owner's own memories (CH-54) also has Edit and Delete (Delete asks first); an archive's
    /// memory stays read-only.</para>
    /// </summary>
    internal sealed class MemoryDetailsPage : ContentPage
    {
        private readonly Func<int, Task> _showMessage;
        private readonly Func<long, Task> _showEntry;
        private readonly MemoryDetails _details;
        private readonly IReadOnlyList<string> _shareRoots;

        /// <param name="details">What to show.</param>
        /// <param name="shareRoots">
        /// The installed archives and the own photos folder (CH-54), for sharing a photo from the viewer (CH-59).
        /// </param>
        /// <param name="showMessage">Opens the conversation at a linked message.</param>
        /// <param name="showEntry">Opens the conversation at a linked entry's row.</param>
        /// <param name="showInChat">
        /// Opened from a memory on the On this day card (CH-53) or the Memories list (CH-56): opens the conversation
        /// at the memory's row when it is visible, otherwise at its day. Given only when there is such a target (a
        /// visible row, or a day with messages); without it there is no Show in chat.
        /// </param>
        /// <param name="own">
        /// Edit and Delete for one of the phone owner's own memories (CH-54); ignored for an archive's memory.
        /// </param>
        internal MemoryDetailsPage(
            MemoryDetails details,
            IReadOnlyList<string> shareRoots,
            Func<int, Task> showMessage,
            Func<long, Task> showEntry,
            Func<Task>? showInChat = null,
            OwnMemoryActions? own = null)
        {
            _shareRoots = shareRoots;
            _showMessage = showMessage;
            _showEntry = showEntry;

            Title = details.CollectionName;

            _details = details;

            var body = new VerticalStackLayout { Padding = 16, Spacing = 6 };

            if (details.HasPhotoStrip)
            {
                // Every photo, the cover first, with its caption (CH-46). A tap opens the full-screen
                // viewer over this memory's photos only.
                var strip = new HorizontalStackLayout { Spacing = 8 };

                for (var index = 0; index < details.Photos.Count; index++)
                {
                    strip.Add(StripPhoto(details.Photos[index], index));
                }

                body.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = strip });
            }
            else if (details.ImagePath is { } image)
            {
                var single = new DecodedImage { WidthRequest = 320, HeightRequest = 180, FailedText = string.Empty, HorizontalOptions = LayoutOptions.Start, Path = image };
                OpenOnTap(single, 0);
                body.Add(single);
            }

            body.Add(new Label { Text = details.Title, FontSize = 20, FontAttributes = FontAttributes.Bold });
            body.Add(Muted(details.DateText, 14));

            if (showInChat is not null)
            {
                var show = new Button { Text = "Show in chat", HorizontalOptions = LayoutOptions.Start };
                show.Clicked += async (_, _) =>
                {
                    try
                    {
                        if (Navigation.NavigationStack.LastOrDefault() == this)
                        {
                            await showInChat();
                        }
                    }
                    catch (Exception)
                    {
                        // A button handler: nothing here may end the app.
                    }
                };

                body.Add(show);
            }

            if (details.Key.IsOwn && own is not null)
            {
                body.Add(OwnActions(own));
            }

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

            if (details.Galleries.Count > 0)
            {
                // CH-48: each external link as its label, or its host; a tap asks before leaving the app.
                body.Add(Heading(details.Galleries.Count == 1 ? "Link" : "Links"));

                foreach (var gallery in details.Galleries)
                {
                    body.Add(GalleryView(gallery));
                }
            }

            if (details.SourceText.Length > 0)
            {
                body.Add(Muted(MemoryWording.SourceLine(details.SourceText), 12));
            }

            Content = new ScrollView { Content = body };
        }

        private View StripPhoto(MemoryPhoto photo, int index)
        {
            var image = new DecodedImage { WidthRequest = 140, HeightRequest = 105, FailedText = string.Empty, Path = photo.Path };
            var column = new VerticalStackLayout { Spacing = 2, WidthRequest = 140, Children = { image } };

            if (photo.HasCaption)
            {
                column.Add(new Label { Text = photo.CaptionText, FontSize = 12, MaxLines = 3, LineBreakMode = LineBreakMode.TailTruncation });
            }

            OpenOnTap(column, index);

            return column;
        }

        /// <summary>A tap on <paramref name="view"/> opens the viewer at the memory's photo <paramref name="index"/>.</summary>
        private void OpenOnTap(View view, int index)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                try
                {
                    if (Navigation.NavigationStack.LastOrDefault() == this && index < _details.Photos.Count)
                    {
                        await Navigation.PushAsync(new ImageViewerPage(_details.BrowsePhotos(index), _shareRoots));
                    }
                }
                catch (Exception)
                {
                    // A tap handler: nothing here may end the app.
                }
            };

            view.GestureRecognizers.Add(tap);
        }

        /// <summary>Edit, and Delete after a confirmation that says it cannot be undone (CH-54).</summary>
        private View OwnActions(OwnMemoryActions own)
        {
            var edit = new Button { Text = "Edit", HorizontalOptions = LayoutOptions.Start };
            var delete = new Button { Text = "Delete", HorizontalOptions = LayoutOptions.Start };
            SemanticProperties.SetDescription(edit, "Edit this memory");
            SemanticProperties.SetDescription(delete, "Delete this memory");

            edit.Clicked += async (_, _) =>
            {
                try
                {
                    if (Navigation.NavigationStack.LastOrDefault() == this)
                    {
                        await own.Edit();
                    }
                }
                catch (Exception)
                {
                    // A button handler: nothing here may end the app.
                }
            };

            delete.Clicked += async (_, _) =>
            {
                try
                {
                    if (Navigation.NavigationStack.LastOrDefault() == this
                        && await DisplayAlertAsync("Delete memory", $"Delete \"{_details.Title}\"? This can't be undone.", "Delete", "Cancel"))
                    {
                        await own.Delete();
                    }
                }
                catch (Exception)
                {
                    // A button handler: nothing here may end the app.
                }
            };

            return new HorizontalStackLayout { Spacing = 8, Children = { edit, delete } };
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

        private View GalleryView(GalleryLink gallery)
        {
            var label = new Label
            {
                Text = gallery.Text,
                FontSize = 15,
                TextDecorations = TextDecorations.Underline,
                Padding = new Thickness(0, 4),
            }.TextIn("LinkText");

            SemanticProperties.SetDescription(label, "Open link " + gallery.Text + " outside Chatcheology");

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                if (Navigation.NavigationStack.LastOrDefault() == this)
                {
                    await GalleryLinkOpener.OpenAsync(this, gallery);
                }
            };

            label.GestureRecognizers.Add(tap);

            return label;
        }

        private static Label Heading(string text) =>
            new() { Text = text, FontSize = 17, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 10, 0, 0) };

        private static Label Muted(string text, double size) => new Label { Text = text, FontSize = size }.TextIn("MutedText");
    }

    /// <summary>What Edit and Delete do on an own memory's details (CH-54): given by the conversation page.</summary>
    /// <param name="Edit">Opens the form over the memory.</param>
    /// <param name="Delete">Deletes it (already confirmed) and closes the details.</param>
    internal sealed record OwnMemoryActions(Func<Task> Edit, Func<Task> Delete);
}
