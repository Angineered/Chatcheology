using System.Globalization;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// Settings for the open archive: Me (a participant, the archive's choice by default, remembered per
    /// archive), On this day (on unless turned off, per archive; CH-39) with the noted-date notification time
    /// (per app) and the noted dates (CH-47), Memories (one switch per layer, with filters for an imported one;
    /// CH-42), Remove archive (after a confirmation) and About.
    /// </summary>
    internal sealed class SettingsPage : ContentPage
    {
        private readonly ArchiveSession _session;
        private readonly Func<Task> _meChanged;
        private readonly Func<Task> _remove;
        private readonly Picker _me = new() { Title = "Me" };
        private readonly IReadOnlyList<MeChoice> _choices;
        private readonly VerticalStackLayout _noted = new();
        private readonly Action _notedDatesChanged;
        private readonly VerticalStackLayout _notificationsOff = new() { Spacing = 4, IsVisible = false };
        private bool _loading;

        internal SettingsPage(ArchiveSession session, Func<Task> meChanged, Func<Task> removeArchive, Action onThisDayChanged, Func<Task> layersChanged)
        {
            _session = session;
            _meChanged = meChanged;
            _remove = removeArchive;
            _choices = session.MeChoices;

            Title = "Settings";

            _loading = true;
            _me.ItemsSource = _choices.Select(choice => choice.Label).ToList();
            _me.SelectedIndex = IndexOf(session.Me);
            _loading = false;
            _me.SelectedIndexChanged += OnMeChanged;

            var onThisDay = new Switch { IsToggled = session.OnThisDayEnabled, VerticalOptions = LayoutOptions.Center }
                .Themed(Switch.OnColorProperty, "Primary");
            onThisDay.Toggled += (_, e) =>
            {
                session.OnThisDayEnabled = e.Value;
                onThisDayChanged();
                _ = NotedDateNotifier.UpdateAsync();
            };

            var onThisDayRow = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 12,
            };

            onThisDayRow.Add(new Label { Text = "Show earlier years' messages from today's date when the conversation opens.", FontSize = 13, VerticalOptions = LayoutOptions.Center }.TextIn("MutedText"), 0);
            onThisDayRow.Add(onThisDay, 1);

            // Every change to noted dates or mutes schedules the notifications again (CH-47).
            _notedDatesChanged = () =>
            {
                onThisDayChanged();
                _ = NotedDateNotifier.UpdateAsync();
            };
            _noted.Spacing = 8;
            RebuildNotedDates();

            var memories = MemoriesSection(session, layersChanged);

            var remove = new Button { Text = "Remove archive…", TextColor = Colors.White, BackgroundColor = Color.FromArgb("#B3261E") };
            remove.Clicked += OnRemoveClicked;

            var version = $"{AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})";

            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = 16,
                    Spacing = 10,
                    Children =
                    {
                        Heading("Me"),
                        new Label { Text = "Whose messages are shown on the right.", FontSize = 13 }.TextIn("MutedText"),
                        _me,

                        Heading("On this day"),
                        onThisDayRow,
                        NotificationTimeRow(),
                        _notificationsOff,
                        _noted,

                        memories,

                        Heading("Remove archive"),
                        new Label { Text = "Deletes the app's copy of this archive. The folder you imported it from is not touched.", FontSize = 13 }.TextIn("MutedText"),
                        remove,

                        Heading("About"),
                        Line("App version", version),
                        Line("Archive format version", session.FormatVersion.ToString(CultureInfo.CurrentCulture)),
                        Line("Last validation", session.ValidationText),
                    },
                },
            };
        }

        /// <inheritdoc />
        protected override void OnAppearing()
        {
            base.OnAppearing();

            // Read again each time: the system settings may have been changed meanwhile.
            ShowNotificationState();
        }

        /// <summary>
        /// The time noted dates notify (CH-47): one for the whole app, 09:00 until changed. A change schedules
        /// the notifications again.
        /// </summary>
        private static Grid NotificationTimeRow()
        {
            var picker = new TimePicker
            {
                Time = NotedDateNotifier.Notifications.Time.ToTimeSpan(),
                Format = "HH:mm",
                VerticalOptions = LayoutOptions.Center,
            };

            SemanticProperties.SetDescription(picker, "Noted-date notification time");
            picker.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TimePicker.Time) || picker.Time is not { } value)
                {
                    return;
                }

                var chosen = new TimeOnly(value.Hours, value.Minutes);

                if (chosen != NotedDateNotifier.Notifications.Time)
                {
                    NotedDateNotifier.Notifications.Time = chosen;
                    _ = NotedDateNotifier.UpdateAsync();
                }
            };

            var row = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 12,
            };

            row.Add(new Label { Text = "Noted-date notification time (for every archive).", FontSize = 13, VerticalOptions = LayoutOptions.Center }.TextIn("MutedText"), 0);
            row.Add(picker, 1);

            return row;
        }

        /// <summary>
        /// "Notifications are off" with a way to the system settings, when the app may not notify: refused on
        /// Android 13+ (once asked), or turned off in the system settings.
        /// </summary>
        private void ShowNotificationState()
        {
            bool off;

            try
            {
                off = !NotedDateNotifier.NotificationsEnabled
                      && (!OperatingSystem.IsAndroidVersionAtLeast(33) || NotedDateNotifier.Notifications.PermissionAsked);
            }
            catch (Exception)
            {
                off = false;
            }

            if (off && _notificationsOff.Children.Count == 0)
            {
                var open = new Button { Text = "Open notification settings", HorizontalOptions = LayoutOptions.Start };
                open.Clicked += (_, _) =>
                {
                    try
                    {
                        NotedDateNotifier.OpenSystemSettings();
                    }
                    catch (Exception)
                    {
                        // A button handler: nothing here may end the app.
                    }
                };

                _notificationsOff.Add(new Label { Text = "Notifications are off", FontSize = 15, FontAttributes = FontAttributes.Bold });
                _notificationsOff.Add(new Label { Text = "Noted dates still show on the card. To be reminded, allow notifications for Chatcheology.", FontSize = 13 }.TextIn("MutedText"));
                _notificationsOff.Add(open);
            }

            _notificationsOff.IsVisible = off;
        }

        /// <summary>
        /// "Noted dates" (CH-47): the archive's gifted dates, each with a Notify switch (a gifted date cannot be
        /// edited or deleted here), then the phone's own, each with Edit and Delete (after a confirmation), and Add.
        /// </summary>
        private void RebuildNotedDates()
        {
            _noted.Children.Clear();
            _noted.Add(new Label { Text = "Noted dates", FontSize = 15, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 6, 0, 0) });
            _noted.Add(new Label { Text = "Days that matter to you, shown on the card every year and notified that morning. Notify off: no notification.", FontSize = 13 }.TextIn("MutedText"));

            var listings = _session.NotedDateListings;
            var gifted = listings.Where(date => date.IsGifted).ToList();
            var own = listings.Where(date => !date.IsGifted).ToList();

            if (gifted.Count > 0)
            {
                _noted.Add(new Label { Text = "From the archive", FontSize = 13, FontAttributes = FontAttributes.Bold }.TextIn("MutedText"));
            }

            foreach (var date in gifted)
            {
                var toggle = new Switch { IsToggled = date.Notify, VerticalOptions = LayoutOptions.Center }.Themed(Switch.OnColorProperty, "Primary");
                SemanticProperties.SetDescription(toggle, "Notify for " + date.Title);
                toggle.Toggled += (_, e) =>
                {
                    _session.SetNotedDateMuted(long.Parse(date.Id, CultureInfo.InvariantCulture), !e.Value);
                    _notedDatesChanged();
                };

                var row = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) },
                    ColumnSpacing = 8,
                };

                row.Add(DateLabel(date), 0);
                row.Add(new Label { Text = "Notify", FontSize = 13, VerticalOptions = LayoutOptions.Center }.TextIn("MutedText"), 1);
                row.Add(toggle, 2);
                _noted.Add(row);
            }

            if (own.Count > 0)
            {
                _noted.Add(new Label { Text = "Added on this phone", FontSize = 13, FontAttributes = FontAttributes.Bold }.TextIn("MutedText"));
            }

            foreach (var date in own)
            {
                var edit = new Button { Text = "Edit", Padding = new Thickness(10, 0), HeightRequest = 36 };
                var delete = new Button { Text = "Delete", Padding = new Thickness(10, 0), HeightRequest = 36 };
                SemanticProperties.SetDescription(edit, "Edit " + date.Title);
                SemanticProperties.SetDescription(delete, "Delete " + date.Title);
                edit.Clicked += async (_, _) => await Navigation.PushAsync(new NotedDatePage(_session, date, () =>
                {
                    RebuildNotedDates();
                    _notedDatesChanged();
                }));
                delete.Clicked += async (_, _) =>
                {
                    if (await DisplayAlertAsync("Delete noted date", $"Delete \"{date.Title}\"?", "Delete", "Cancel"))
                    {
                        _session.DeleteOwnNotedDate(date.Id);
                        RebuildNotedDates();
                        _notedDatesChanged();
                    }
                };

                var row = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) },
                    ColumnSpacing = 6,
                };

                row.Add(DateLabel(date), 0);
                row.Add(edit, 1);
                row.Add(delete, 2);
                _noted.Add(row);
            }

            var add = new Button { Text = "Add a noted date" };
            add.Clicked += async (_, _) => await Navigation.PushAsync(new NotedDatePage(_session, null, () =>
            {
                RebuildNotedDates();
                _notedDatesChanged();
            }));
            _noted.Add(add);
        }

        /// <summary>The title and date and, with gallery links (CH-48), "Gallery · host" or "N galleries" to open them.</summary>
        private View DateLabel(NotedDateListing date)
        {
            var label = new VerticalStackLayout
            {
                Spacing = 0,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label { Text = date.Title, FontSize = 15 },
                    new Label { Text = date.DateText, FontSize = 13 }.TextIn("MutedText"),
                },
            };

            if (date.Galleries.Count > 0)
            {
                label.Add(GalleryLine(this, date.GalleryText, date.Galleries));
            }

            return label;
        }

        /// <summary>A tappable "Gallery · host" / "N galleries" line that opens the links (after a confirmation).</summary>
        internal static Label GalleryLine(Page page, string text, IReadOnlyList<GalleryLink> galleries)
        {
            var line = new Label
            {
                Text = text,
                FontSize = 13,
                TextDecorations = TextDecorations.Underline,
                Padding = new Thickness(0, 2),
            }.TextIn("LinkText");

            SemanticProperties.SetDescription(line, text + ", opens outside Chatcheology");

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                if (page.Navigation.NavigationStack.LastOrDefault() == page)
                {
                    await GalleryLinkOpener.ChooseAndOpenAsync(page, galleries);
                }
            };

            line.GestureRecognizers.Add(tap);

            return line;
        }

        private int IndexOf(long participantID)
        {
            for (var i = 0; i < _choices.Count; i++)
            {
                if (_choices[i].ParticipantID == participantID)
                {
                    return i;
                }
            }

            return -1;
        }

        private async void OnMeChanged(object? sender, EventArgs e)
        {
            if (_loading || _me.SelectedIndex < 0 || _me.SelectedIndex >= _choices.Count)
            {
                return;
            }

            var chosen = _choices[_me.SelectedIndex].ParticipantID;

            if (chosen == _session.Me)
            {
                return;
            }

            _session.SetMe(chosen);
            await _meChanged();
        }

        private async void OnRemoveClicked(object? sender, EventArgs e)
        {
            var confirmed = await DisplayAlertAsync(
                "Remove archive",
                "Remove this archive from the app? The app's copy is deleted; to read it again you would import it again.",
                "Remove",
                "Cancel");

            if (confirmed)
            {
                await _remove();
            }
        }

        /// <summary>
        /// One switch per memory layer and, under an imported one, its filters; empty for an archive without
        /// memories. A change is stored at once and the conversation rebuilt.
        /// </summary>
        private static View MemoriesSection(ArchiveSession session, Func<Task> layersChanged)
        {
            var section = new VerticalStackLayout { Spacing = 10 };
            var layers = session.MemoryLayers;

            if (layers.Count == 0)
            {
                return section;
            }

            section.Add(Heading("Memories"));
            section.Add(new Label { Text = "Layers shown in the conversation beside the messages. They are never messages.", FontSize = 13 }.TextIn("MutedText"));

            foreach (var layer in layers)
            {
                var filters = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(16, 0, 0, 0), IsVisible = layer.IsShown };

                section.Add(SwitchRow(layer.Name, 15, layer.IsShown, async on =>
                {
                    session.SetLayerShown(layer.CollectionID, on);
                    filters.IsVisible = on;
                    await layersChanged();
                }));

                foreach (var (filter, isOn) in layer.Filters)
                {
                    filters.Add(SwitchRow(MemoryFilters.Title(filter), 13, isOn, async on =>
                    {
                        session.SetMemoryFilter(layer.CollectionID, filter, on);
                        await layersChanged();
                    }));
                }

                if (layer.Filters.Count > 0)
                {
                    section.Add(filters);
                }
            }

            return section;
        }

        private static Grid SwitchRow(string text, double size, bool on, Func<bool, Task> changed)
        {
            var toggle = new Switch { IsToggled = on, VerticalOptions = LayoutOptions.Center }.Themed(Switch.OnColorProperty, "Primary");
            toggle.Toggled += async (_, e) =>
            {
                try
                {
                    await changed(e.Value);
                }
                catch (Exception)
                {
                    // A switch handler: nothing here may end the app.
                }
            };

            var row = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 12,
            };

            row.Add(new Label { Text = text, FontSize = size, VerticalOptions = LayoutOptions.Center }, 0);
            row.Add(toggle, 1);

            return row;
        }

        private static Label Heading(string text) =>
            new() { Text = text, FontSize = 18, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 12, 0, 0) };

        private static View Line(string name, string value) => new VerticalStackLayout
        {
            Spacing = 0,
            Children =
            {
                new Label { Text = name, FontSize = 13 }.TextIn("MutedText"),
                new Label { Text = value, FontSize = 15 },
            },
        };
    }
}
