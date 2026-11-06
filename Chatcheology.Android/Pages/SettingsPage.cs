using System.Globalization;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.OwnMemories;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// Settings for the open archive: Me (a participant, the archive's choice by default, remembered per
    /// archive), On this day (on unless turned off, per archive; CH-39) with the noted-date notification time
    /// (per app) and the archive's noted dates and yearly memories (CH-47, CH-53; Notify switches only), Memories
    /// (one switch per layer, with filters for an imported one; CH-42), My memories (per phone, for every archive;
    /// CH-54), Remove archive (after a confirmation) and About.
    /// </summary>
    /// <remarks>
    /// The phone's own noted dates (Add / Edit / Delete here, CH-47) were removed by CH-54 step 2: the phone owner's own
    /// memories replace them, with their own section.
    /// </remarks>
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
        private readonly VerticalStackLayout _own = new() { Spacing = 10 };
        private readonly OwnMemoryStore? _ownStore;
        private readonly Func<Task> _layersChanged;
        private readonly Func<Task> _ownChanged;
        private bool _loading;
        private bool _revertingLayer;

        /// <param name="session">The open archive.</param>
        /// <param name="ownStore">The phone owner's own memories (CH-54), or none: no My memories section.</param>
        /// <param name="meChanged">"Me" was changed.</param>
        /// <param name="removeArchive">Remove archive was confirmed.</param>
        /// <param name="onThisDayChanged">On this day or a Notify switch changed.</param>
        /// <param name="layersChanged">A memory layer or filter, or the My memories switch, changed.</param>
        /// <param name="ownChanged">The own memories changed (Delete all): the conversation reads them again.</param>
        internal SettingsPage(
            ArchiveSession session,
            OwnMemoryStore? ownStore,
            Func<Task> meChanged,
            Func<Task> removeArchive,
            Action onThisDayChanged,
            Func<Task> layersChanged,
            Func<Task> ownChanged)
        {
            _ownStore = ownStore;
            _layersChanged = layersChanged;
            _ownChanged = ownChanged;
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
            RebuildOwn();

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

                        _own,

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
        /// "Noted dates" (CH-47): the archive's yearly memories (CH-53) and gifted dates, each with a Notify switch
        /// (neither can be edited or deleted here). Shown only when the archive has any.
        /// </summary>
        private void RebuildNotedDates()
        {
            _noted.Children.Clear();

            var listings = _session.NotedDateListings;
            var gifted = listings.Where(date => date.IsGifted).ToList();
            var yearly = _session.YearlyMemoryListings;

            if (gifted.Count == 0 && yearly.Count == 0)
            {
                return;
            }

            _noted.Add(new Label { Text = "Noted dates", FontSize = 15, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 6, 0, 0) });
            _noted.Add(new Label { Text = "Days that matter to you, shown on the card every year and notified that morning. Notify off: no notification.", FontSize = 13 }.TextIn("MutedText"));

            // CH-53: the archive's yearly memories, each with a Notify switch of its own (its own settings key).
            if (yearly.Count > 0)
            {
                _noted.Add(new Label { Text = "Yearly memories", FontSize = 13, FontAttributes = FontAttributes.Bold }.TextIn("MutedText"));
            }

            foreach (var memory in yearly)
            {
                var toggle = new Switch { IsToggled = memory.Notify, VerticalOptions = LayoutOptions.Center }.Themed(Switch.OnColorProperty, "Primary");
                SemanticProperties.SetDescription(toggle, "Notify for " + memory.Title);
                toggle.Toggled += (_, e) =>
                {
                    _session.SetYearlyMemoryMuted(memory.EntryID, !e.Value);
                    _notedDatesChanged();
                };

                var row = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) },
                    ColumnSpacing = 8,
                };

                row.Add(new VerticalStackLayout
                {
                    Spacing = 0,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new Label { Text = memory.Title, FontSize = 15 },
                        new Label { Text = memory.DateText, FontSize = 13 }.TextIn("MutedText"),
                    },
                }, 0);
                row.Add(new Label { Text = "Notify", FontSize = 13, VerticalOptions = LayoutOptions.Center }.TextIn("MutedText"), 1);
                row.Add(toggle, 2);
                _noted.Add(row);
            }

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
        }

        /// <summary>
        /// "My memories" (CH-54): per phone, the same in every archive. The layer switch (rebuilds the conversation like
        /// any layer), the reminders switch, a Notify switch per own yearly memory, and Delete all my memories (asks
        /// first; it cannot be undone). Save and Load come in CH-54 step 3, below Delete all.
        /// </summary>
        private void RebuildOwn()
        {
            _own.Children.Clear();

            if (_ownStore is not { } store)
            {
                return;
            }

            _own.Add(Heading("My memories"));
            _own.Add(new Label { Text = "Memories you add on this phone. They show in every archive on this phone and are never written into an archive.", FontSize = 13 }.TextIn("MutedText"));

            IReadOnlyList<OwnYearlyMemoryListing> yearly;
            bool reminders;
            int count;

            try
            {
                yearly = OwnMemoryIndex.YearlyListings(store);
                reminders = store.Settings.RemindersOn;
                count = store.Count();
            }
            catch (Exception exception) when (IsStoreFailure(exception))
            {
                _own.Add(new Label { Text = "Your memories could not be read just now.", FontSize = 13 }.TextIn("ErrorText"));
                return;
            }

            Switch? layerSwitch = null;
            var layerRow = SwitchRow("Show my memories in the chat", 15, _session.OwnLayerShown, async on =>
            {
                if (_revertingLayer)
                {
                    return;
                }

                _session.OwnLayerShown = on;

                if (_session.OwnLayerShown != on)
                {
                    // Not stored (the file was busy): the switch goes back, without storing again.
                    _revertingLayer = true;
                    layerSwitch!.IsToggled = !on;
                    _revertingLayer = false;
                    await DisplayAlertAsync("My memories", "That could not be changed just now. Try again.", "OK");
                    return;
                }

                await _layersChanged();
            });

            layerSwitch = layerRow.Children.OfType<Switch>().Single();
            SemanticProperties.SetDescription(layerSwitch, "Show my memories in the chat");
            _own.Add(layerRow);

            var remindersRow = SwitchRow("Remind me on the day of my yearly memories", 15, reminders, on =>
            {
                try
                {
                    store.Settings.RemindersOn = on;
                }
                catch (Exception exception) when (IsStoreFailure(exception))
                {
                    RebuildOwn();
                    return Task.CompletedTask;
                }

                _ = NotedDateNotifier.UpdateAsync();
                return Task.CompletedTask;
            });

            SemanticProperties.SetDescription(remindersRow.Children.OfType<Switch>().Single(), "Remind me on the day of my yearly memories");
            _own.Add(remindersRow);

            if (yearly.Count > 0)
            {
                _own.Add(new Label { Text = "Yearly memories", FontSize = 13, FontAttributes = FontAttributes.Bold }.TextIn("MutedText"));
            }

            foreach (var memory in yearly)
            {
                var toggle = new Switch { IsToggled = memory.Notify, VerticalOptions = LayoutOptions.Center }.Themed(Switch.OnColorProperty, "Primary");
                SemanticProperties.SetDescription(toggle, "Notify for " + memory.Title);
                toggle.Toggled += (_, e) =>
                {
                    try
                    {
                        store.Settings.SetMuted(memory.Id, !e.Value);
                    }
                    catch (Exception exception) when (IsStoreFailure(exception))
                    {
                        RebuildOwn();
                        return;
                    }

                    _ = NotedDateNotifier.UpdateAsync();
                };

                var row = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) },
                    ColumnSpacing = 8,
                };

                row.Add(new VerticalStackLayout
                {
                    Spacing = 0,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new Label { Text = memory.Title, FontSize = 15 },
                        new Label { Text = memory.DateText, FontSize = 13 }.TextIn("MutedText"),
                    },
                }, 0);
                row.Add(new Label { Text = "Notify", FontSize = 13, VerticalOptions = LayoutOptions.Center }.TextIn("MutedText"), 1);
                row.Add(toggle, 2);
                _own.Add(row);
            }

            // CH-54 step 3 puts Save to a file and Load from a file here, above Delete all.
            var deleteAll = new Button
            {
                Text = "Delete all my memories…",
                TextColor = Colors.White,
                BackgroundColor = Color.FromArgb("#B3261E"),
                IsEnabled = count > 0,
            };

            deleteAll.Clicked += async (_, _) => await DeleteAllAsync(store);
            _own.Add(deleteAll);
        }

        private async Task DeleteAllAsync(OwnMemoryStore store)
        {
            try
            {
                if (!await DisplayAlertAsync(
                        "Delete all my memories",
                        "Delete every memory you added on this phone, with their photos? This can't be undone.",
                        "Delete all",
                        "Cancel"))
                {
                    return;
                }

                var deleted = await Task.Run(store.DeleteAll);
                AppState.LogTiming("own-memories-deleted-all", TimeSpan.Zero, $"deleted={deleted}");

                await _ownChanged();
                RebuildOwn();
            }
            catch (Exception exception) when (IsStoreFailure(exception))
            {
                await DisplayAlertAsync("Delete all my memories", "Your memories could not be deleted. Try again.", "OK");
            }
        }

        private static bool IsStoreFailure(Exception exception) =>
            exception is OwnMemoriesStoreException or SqliteException or IOException or UnauthorizedAccessException;

        /// <summary>The title and date and, with gallery links (CH-48), "Link · host" or "N links" to open them.</summary>
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

        /// <summary>A tappable "Link · host" / "N links" line that opens the links (after a confirmation).</summary>
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
