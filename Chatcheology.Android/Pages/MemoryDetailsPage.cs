using Chatcheology.Android.Controls;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The details of a memory row (CH-42): what <see cref="MemoryDetails"/> says, laid out. Since CH-68 the heading
    /// and date come first, then the photos, then the buttons, then the rest. For a PSN period: its trophies with
    /// exact local times, names and icons (or "Bronze trophy #12"), then the day evidence and the day's other periods.
    /// For a personal memory: its photos (CH-46; laid out by <see cref="MemoryPhotoLayout"/> since CH-68: one photo at
    /// its own shape, two or more as a grid of square tiles, three across, at most nine, the ninth with "+N"; a tap
    /// opens the full-screen viewer over all of them, captions shown there), description, links and external links
    /// (CH-48, shown as "Links" since CH-59; each opens in another app only after a confirmation). Only a photo can be
    /// shared, from the viewer (CH-59); everything was read when the archive opened.
    /// <para>One of the phone owner's own memories (CH-54) also has Edit and Delete (Delete asks first); an archive's
    /// memory stays read-only.</para>
    /// <para>A game (PSN) memory (CH-67, <see cref="MemoryDetails.Game"/>): heading, date with play window and the
    /// day's evidence block (a tap shows its meaning), the game picture uncropped, Show in chat, then the trophies
    /// (together trophies on one row). Nothing below the trophies.</para>
    /// </summary>
    internal sealed class MemoryDetailsPage : ContentPage
    {
        /// <summary>The page's side padding, in device-independent units.</summary>
        private const double SidePadding = 16;

        private readonly Func<int, Task> _showMessage;
        private readonly Func<long, Task> _showEntry;
        private readonly MemoryDetails _details;
        private readonly IReadOnlyList<string> _shareRoots;
        private readonly MemoryPhotoLayout _photoLayout;

        /// <summary>Holds the photos once the page's size is known; not in the page at all when there is none.</summary>
        private readonly ContentView _photoSection = new();

        private double _pageWidth;
        private double _pageHeight;

        /// <summary>The single photo's upright size once read (0, 0 when it cannot be); null until then.</summary>
        private (int Width, int Height)? _singleImageSize;

        /// <summary>The sizes the photos were last drawn at, so a size change that changes nothing redraws nothing.</summary>
        private (double Width, double Height) _drawnAt;

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

            // Only photos whose file is there (CH-46), the cover first; a PSN game's picture is the one photo.
            _photoLayout = MemoryPhotoLayout.For(details.Photos);

            var body = new VerticalStackLayout { Padding = SidePadding, Spacing = 6 };

            body.Add(new Label { Text = details.Title, FontSize = 22, FontAttributes = FontAttributes.Bold });

            if (details.Game is { } game)
            {
                AddGameDate(body, game);
            }
            else
            {
                body.Add(Muted(details.DateText, 14));
            }

            if (_photoLayout.Arrangement != MemoryPhotoArrangement.None)
            {
                // Drawn once the page's width is known (OnSizeAllocated); no photo, no section and no gap.
                _photoSection.Margin = new Thickness(0, 6, 0, 0);
                body.Add(_photoSection);

                if (_photoLayout.Arrangement == MemoryPhotoArrangement.Single)
                {
                    ReadSingleSize(details.Photos[0].Path!);
                }
            }

            var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center };

            if (showInChat is not null)
            {
                var show = new Button { Text = "Show in chat", Margin = new Thickness(0, 4, 8, 4) };
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

                buttons.Add(show);
            }

            if (details.Key.IsOwn && own is not null)
            {
                foreach (var button in OwnActions(own))
                {
                    buttons.Add(button);
                }
            }

            if (buttons.Count > 0)
            {
                body.Add(buttons);
            }

            if (details.Game is { } gameDetails)
            {
                // CH-67: a game memory ends with its trophies; nothing shows below them.
                if (gameDetails.Trophies.Count > 0)
                {
                    body.Add(Heading(MemoryWording.ItemsHeading));

                    foreach (var trophy in gameDetails.Trophies)
                    {
                        body.Add(ItemView(trophy.First, MemoryWording.GameItemMeta(trophy)));
                    }
                }

                Content = new ScrollView { Content = body };
                return;
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
                    body.Add(ItemView(item, MemoryWording.ItemMeta(item)));
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

        /// <inheritdoc />
        /// <remarks>The tiles and the single photo are sized from the page, as the Photos gallery sizes its cells.</remarks>
        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);

            try
            {
                if (width > 0 && height > 0)
                {
                    _pageWidth = width;
                    _pageHeight = height;
                    DrawPhotos();
                }
            }
            catch (Exception)
            {
                // A layout callback: nothing here may end the app; the photos are then not drawn.
            }
        }

        /// <summary>
        /// Reads the single photo's size from its header on a worker thread, then draws it at that shape. A size
        /// that cannot be read draws a 4:3 box (the photo, or an empty box when it will not decode either).
        /// </summary>
        private void ReadSingleSize(string path)
        {
            Task.Run(() => ImageDecoding.ReadUprightSize(path)).ContinueWith(
                read =>
                {
                    var size = read.IsCompletedSuccessfully ? read.Result : (0, 0);

                    try
                    {
                        Dispatcher.Dispatch(() =>
                        {
                            try
                            {
                                _singleImageSize = size;

                                // A page already closed draws nothing.
                                if (Handler is not null)
                                {
                                    DrawPhotos();
                                }
                            }
                            catch (Exception)
                            {
                                // A posted callback: nothing here may end the app.
                            }
                        });
                    }
                    catch (Exception)
                    {
                        // The page's dispatcher is gone: nothing to draw.
                    }
                },
                TaskScheduler.Default);
        }

        /// <summary>
        /// Draws the photo section for the page's size: the single photo once its own size is read, or the grid.
        /// Each picture is decoded at the size it is shown (its requested size is set before its path), through
        /// <see cref="DecodedImage"/> and its cache. Redraws only when that size changes.
        /// </summary>
        private void DrawPhotos()
        {
            var available = _pageWidth - 2 * SidePadding;

            if (_photoLayout.Arrangement == MemoryPhotoArrangement.Single)
            {
                if (_singleImageSize is not { } image)
                {
                    return;
                }

                var (width, height) = MemoryPhotoLayout.SingleSize(image.Width, image.Height, available, _pageHeight);

                if (width <= 0 || (width, height) == _drawnAt)
                {
                    return;
                }

                _drawnAt = (width, height);
                _photoSection.Content = SinglePhoto(width, height);
            }
            else if (_photoLayout.Arrangement == MemoryPhotoArrangement.Grid)
            {
                var tile = MemoryPhotoLayout.TileSize(available);

                if (tile <= 0 || (tile, tile) == _drawnAt)
                {
                    return;
                }

                _drawnAt = (tile, tile);
                _photoSection.Content = PhotoGrid(tile);
            }
        }

        /// <summary>
        /// The one photo at its own shape: the box has the photo's shape, so the image view's centre-crop crops
        /// nothing (at most a rounding pixel). Centred when the height limit makes it narrower than the page.
        /// </summary>
        private View SinglePhoto(double width, double height)
        {
            var image = new DecodedImage { WidthRequest = width, HeightRequest = height, FailedText = string.Empty, HorizontalOptions = LayoutOptions.Center };
            image.Path = _details.Photos[0].Path;

            var holder = new Grid { WidthRequest = width, HeightRequest = height, HorizontalOptions = LayoutOptions.Center };
            holder.Add(image);

            if (_details.Photos[0].HasCaption)
            {
                holder.Add(new CaptionMark());
            }

            SemanticProperties.SetDescription(holder, _photoLayout.SingleDescription);
            OpenOnTap(holder, 0);

            return holder;
        }

        /// <summary>The grid: three square tiles across, only as many rows as needed, cover first.</summary>
        private View PhotoGrid(double tile)
        {
            var grid = new Grid
            {
                ColumnSpacing = MemoryPhotoLayout.Gap,
                RowSpacing = MemoryPhotoLayout.Gap,
                HorizontalOptions = LayoutOptions.Center,
            };

            for (var column = 0; column < MemoryPhotoLayout.Columns; column++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(tile)));
            }

            for (var row = 0; row < _photoLayout.Rows; row++)
            {
                grid.RowDefinitions.Add(new RowDefinition(new GridLength(tile)));
            }

            foreach (var tileInfo in _photoLayout.Tiles)
            {
                grid.Add(Tile(tileInfo, tile), tileInfo.Column, tileInfo.Row);
            }

            return grid;
        }

        private View Tile(MemoryPhotoTile tile, double size)
        {
            var image = new DecodedImage { WidthRequest = size, HeightRequest = size, FailedText = string.Empty };
            image.Path = _details.Photos[tile.PhotoIndex].Path;

            var cell = new Grid { WidthRequest = size, HeightRequest = size };
            cell.Add(image);

            if (tile.MoreCount > 0)
            {
                // "+N" over the photo, on a dark veil so it reads on any picture, light or dark.
                var more = new Label
                {
                    Text = tile.MoreText,
                    FontSize = 22,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Colors.White,
                    BackgroundColor = Color.FromRgba(0, 0, 0, 0.5),
                    HorizontalTextAlignment = TextAlignment.Center,
                    VerticalTextAlignment = TextAlignment.Center,
                    InputTransparent = true,
                };
                AutomationProperties.SetIsInAccessibleTree(more, false);
                cell.Add(more);
            }

            if (tile.HasCaption)
            {
                cell.Add(new CaptionMark());
            }

            SemanticProperties.SetDescription(cell, tile.Description);
            OpenOnTap(cell, tile.PhotoIndex);

            return cell;
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
        private IEnumerable<View> OwnActions(OwnMemoryActions own)
        {
            var edit = new Button { Text = "Edit", Margin = new Thickness(0, 4, 8, 4) };
            var delete = new Button { Text = "Delete", Margin = new Thickness(0, 4, 8, 4) };
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

            return [edit, delete];
        }

        /// <summary>
        /// A game memory's date line (CH-67): the day and play window, then the day's evidence as a colour block when
        /// its level is known. A tap on the block shows its one-line meaning under the date; another tap hides it.
        /// </summary>
        private static void AddGameDate(VerticalStackLayout body, GameDetails game)
        {
            var date = Muted(game.DateText, 14);
            date.LineBreakMode = LineBreakMode.WordWrap;
            date.VerticalOptions = LayoutOptions.Start;

            if (game.EvidenceBlock is not { } block || game.EvidenceMeaning is not { } meaning)
            {
                body.Add(date);
                return;
            }

            var blockLabel = new Label
            {
                Text = block,
                FontSize = 16,
                VerticalOptions = LayoutOptions.Start,
                Padding = new Thickness(8, 0, 8, 4),
            };
            SemanticProperties.SetDescription(blockLabel, game.EvidenceDescription ?? meaning);

            var meaningLabel = Muted(meaning, 13);
            meaningLabel.IsVisible = false;

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                try
                {
                    meaningLabel.IsVisible = !meaningLabel.IsVisible;
                }
                catch (Exception)
                {
                    // A tap handler: nothing here may end the app.
                }
            };
            blockLabel.GestureRecognizers.Add(tap);

            // The date takes the rest of the width and wraps; the block keeps its own width, so at any font scale or
            // date length it stays on screen and tappable.
            var line = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 2,
            };
            line.Add(date, 0);
            line.Add(blockLabel, 1);
            body.Add(line);
            body.Add(meaningLabel);
        }

        private static View ItemView(MemoryItemLine item, string meta)
        {
            var text = new VerticalStackLayout
            {
                Spacing = 0,
                Children =
                {
                    new Label { Text = item.Name, FontSize = 15, FontAttributes = FontAttributes.Bold },
                    Muted(meta, 12),
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

    /// <summary>
    /// The small mark on a memory photo that has a caption (CH-68): a white speech bubble on a dark round backing in
    /// the tile's bottom-right corner, so it reads on any photo in light and dark mode. Fixed colours, so no theme
    /// subscription. Drawn only: taps pass through to the tile and screen readers hear the tile's description.
    /// </summary>
    internal sealed class CaptionMark : GraphicsView
    {
        private const double Size = 22;

        internal CaptionMark()
        {
            Drawable = new BubbleDrawable();
            WidthRequest = Size;
            HeightRequest = Size;
            Margin = new Thickness(4);
            HorizontalOptions = LayoutOptions.End;
            VerticalOptions = LayoutOptions.End;
            BackgroundColor = Colors.Transparent;
            InputTransparent = true;
            AutomationProperties.SetIsInAccessibleTree(this, false);
        }

        private sealed class BubbleDrawable : IDrawable
        {
            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                var cx = dirtyRect.Center.X;
                var cy = dirtyRect.Center.Y;
                var radius = Math.Min(dirtyRect.Width, dirtyRect.Height) / 2f;

                canvas.FillColor = Color.FromRgba(0, 0, 0, 0.5);
                canvas.FillCircle(cx, cy, radius);

                // A rounded bubble with a short tail at its lower left.
                canvas.FillColor = Colors.White.WithAlpha(0.9f);
                canvas.FillRoundedRectangle(cx - 6.5f, cy - 5.5f, 13f, 9f, 2.5f);

                var tail = new PathF();
                tail.MoveTo(cx - 4f, cy + 3f);
                tail.LineTo(cx - 4.5f, cy + 6.5f);
                tail.LineTo(cx - 0.5f, cy + 3f);
                tail.Close();
                canvas.FillPath(tail);
            }
        }
    }

    /// <summary>What Edit and Delete do on an own memory's details (CH-54): given by the conversation page.</summary>
    /// <param name="Edit">Opens the form over the memory.</param>
    /// <param name="Delete">Deletes it (already confirmed) and closes the details.</param>
    internal sealed record OwnMemoryActions(Func<Task> Edit, Func<Task> Delete);
}
