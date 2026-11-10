using System.Globalization;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Notifications;
using Chatcheology.Mobile.OwnMemories;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// Settings for the open archive. Shown (CH-78): On this day with one Notify me switch (<see cref="NotifyMeState"/>),
    /// the notification time (per app) and "Notifications are off" when needed; a closed More settings; About. Under More
    /// settings, working as before: Me (a participant, the archive's choice by default, remembered per archive), the On
    /// this day switch (on unless turned off, per archive; CH-39), the archive's noted dates and yearly memories (CH-47,
    /// CH-53; Notify switches only), Memories (one switch per layer, with filters for an imported one; CH-42), My memories
    /// (per phone, for every archive; CH-54), Import archive (back to the start screen's import) and Remove archive (after
    /// a confirmation).
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
        private readonly Func<Task> _importArchive;
        private bool _loading;
        private bool _importing;

        /// <summary>The Notify me switch (CH-78) and its row (hidden when it stands for nothing).</summary>
        private readonly Switch _notifyMe = new Switch { VerticalOptions = LayoutOptions.Center }.Themed(Switch.OnColorProperty, "Primary");
        private readonly Grid _notifyMeRow;

        /// <summary>Set while the page itself moves the Notify me switch, so that is not taken as a tap.</summary>
        private bool _showingNotifyMe;

        /// <summary>The My memories reminders switch as last read (off the UI thread), or none (no store, not read yet, unreadable).</summary>
        private bool? _reminders;
        private bool _revertingLayer;

        /// <summary>A save or load is running (CH-54 step 3): the file buttons and Delete all are off.</summary>
        private bool _fileBusy;
        private readonly Label _fileWorking = new Label { FontSize = 13, IsVisible = false }.TextIn("MutedText");

        /// <param name="session">The open archive.</param>
        /// <param name="ownStore">The phone owner's own memories (CH-54), or none: no My memories section.</param>
        /// <param name="meChanged">"Me" was changed.</param>
        /// <param name="removeArchive">Remove archive was confirmed.</param>
        /// <param name="onThisDayChanged">On this day or a Notify switch changed.</param>
        /// <param name="layersChanged">A memory layer or filter, or the My memories switch, changed.</param>
        /// <param name="ownChanged">The own memories changed (Delete all): the conversation reads them again.</param>
        /// <param name="importArchive">Import archive was tapped (CH-78): close the archive and import from the start screen.</param>
        internal SettingsPage(
            ArchiveSession session,
            OwnMemoryStore? ownStore,
            Func<Task> meChanged,
            Func<Task> removeArchive,
            Action onThisDayChanged,
            Func<Task> layersChanged,
            Func<Task> ownChanged,
            Func<Task> importArchive)
        {
            _importArchive = importArchive;
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

            // CH-78: one switch for the notify settings that exist (noted dates, yearly memories, My memories reminders).
            _notifyMeRow = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 12,
            };

            _notifyMeRow.Add(new VerticalStackLayout
            {
                Spacing = 0,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label { Text = "Notify me", FontSize = 15 },
                    new Label { Text = "On the day of noted dates and yearly memories, at the time below.", FontSize = 13 }.TextIn("MutedText"),
                },
            }, 0);
            _notifyMeRow.Add(_notifyMe, 1);
            SemanticProperties.SetDescription(_notifyMe, "Notify me on the day of noted dates and yearly memories");
            _notifyMe.Toggled += OnNotifyMeToggled;

            RebuildNotedDates();
            ShowNotifyMe();

            var memories = MemoriesSection(session, layersChanged);
            _ = RebuildOwnAsync();

            var remove = new Button { Text = "Remove archive…", TextColor = Colors.White, BackgroundColor = Color.FromArgb("#B3261E") };
            remove.Clicked += OnRemoveClicked;

            var import = new Button { Text = "Import archive…", HorizontalOptions = LayoutOptions.Start };
            SemanticProperties.SetDescription(import, "Import archive, closes this archive and opens the folder picker");
            import.Clicked += OnImportClicked;

            // CH-78: everything else is here, closed at first, working as before.
            var more = new MoreExpander("More settings", 18) { Margin = new Thickness(0, 12, 0, 0) };
            var items = more.Items;
            items.Add(Heading("Me"));
            items.Add(new Label { Text = "Whose messages are shown on the right.", FontSize = 13 }.TextIn("MutedText"));
            items.Add(_me);
            items.Add(Heading("On this day"));
            items.Add(onThisDayRow);
            items.Add(_noted);
            items.Add(memories);
            items.Add(_own);
            items.Add(Heading("Import archive"));
            items.Add(new Label { Text = "Closes this archive and opens the folder picker. A newer copy of this archive replaces it and keeps your stars and settings.", FontSize = 13 }.TextIn("MutedText"));
            items.Add(import);
            items.Add(Heading("Remove archive"));
            items.Add(new Label { Text = "Deletes the app's copy of this archive. The folder you imported it from is not touched.", FontSize = 13 }.TextIn("MutedText"));
            items.Add(remove);

            var version = $"{AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})";

            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = 16,
                    Spacing = 10,
                    Children =
                    {
                        Heading("On this day"),
                        _notifyMeRow,
                        NotificationTimeRow(),
                        _notificationsOff,

                        more,

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

        /// <summary>Shows the Notify me switch from the stored values (CH-78), without taking that as a tap.</summary>
        private void ShowNotifyMe()
        {
            var state = _session.NotifyMeState(_reminders);

            _showingNotifyMe = true;
            _notifyMe.IsToggled = state.IsOn;
            _showingNotifyMe = false;
            _notifyMeRow.IsVisible = state.HasAny || _ownStore is not null;
        }

        /// <summary>
        /// Notify me turned (CH-78): on sets every notify setting it stands for on, off sets every one off; the separate
        /// switches under More settings are read again. Never throws.
        /// </summary>
        private async void OnNotifyMeToggled(object? sender, ToggledEventArgs e)
        {
            if (_showingNotifyMe)
            {
                return;
            }

            try
            {
                var changes = _session.NotifyMeState(_reminders).ChangesFor(e.Value);
                _session.ApplyNotifyMe(changes);

                if (changes.Reminders is { } reminders && _ownStore is { } store)
                {
                    try
                    {
                        await Task.Run(() => store.Settings.RemindersOn = reminders);
                        _reminders = reminders;
                    }
                    catch (Exception exception) when (IsStoreFailure(exception))
                    {
                        await DisplayAlertAsync("Notify me", "Your memories' reminders could not be changed just now. Try again.", "OK");
                    }
                }

                RebuildNotedDates();
                _notedDatesChanged();
                await RebuildOwnAsync();
            }
            catch (Exception)
            {
                // A switch handler: nothing here may end the app.
            }
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
                    ShowNotifyMe();
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
                    ShowNotifyMe();
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
        /// first; it cannot be undone), with Save to a file and Load from a file above it (CH-54 step 3). The store is read
        /// off the UI thread (step 2 review N3). Never throws.
        /// </summary>
        private async Task RebuildOwnAsync()
        {
            try
            {
                if (_ownStore is not { } store)
                {
                    _own.Children.Clear();
                    return;
                }

                (IReadOnlyList<OwnYearlyMemoryListing> Yearly, bool Reminders, int Count)? read;

                try
                {
                    read = await Task.Run(() => (OwnMemoryIndex.YearlyListings(store), store.Settings.RemindersOn, store.Count()));
                }
                catch (Exception exception) when (IsStoreFailure(exception))
                {
                    read = null;
                }

                BuildOwn(store, read);
            }
            catch (Exception)
            {
                // Settings stays open; the section shows what it had.
            }
        }

        private void BuildOwn(OwnMemoryStore store, (IReadOnlyList<OwnYearlyMemoryListing> Yearly, bool Reminders, int Count)? read)
        {
            _own.Children.Clear();
            _own.Add(Heading("My memories"));
            _own.Add(new Label { Text = "Memories you add on this phone. They show in every archive on this phone and are never written into an archive.", FontSize = 13 }.TextIn("MutedText"));

            if (read is not { } values)
            {
                _reminders = null;
                ShowNotifyMe();
                _own.Add(new Label { Text = "Your memories could not be read just now.", FontSize = 13 }.TextIn("ErrorText"));
                return;
            }

            var (yearly, reminders, count) = values;
            _reminders = reminders;
            ShowNotifyMe();

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
                    _ = RebuildOwnAsync();
                    return Task.CompletedTask;
                }

                _reminders = on;
                ShowNotifyMe();

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
                        _ = RebuildOwnAsync();
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

            // CH-54 step 3: a file of the memories, to keep or to bring them to another phone.
            _own.Add(new Label { Text = "Keep your memories in a file, or bring them back from one. Loading only adds memories that are not here yet.", FontSize = 13, Margin = new Thickness(0, 6, 0, 0) }.TextIn("MutedText"));

            var save = new Button { Text = "Save to a file…", HorizontalOptions = LayoutOptions.Start, IsEnabled = count > 0 && !_fileBusy };
            var load = new Button { Text = "Load from a file…", HorizontalOptions = LayoutOptions.Start, IsEnabled = !_fileBusy };
            SemanticProperties.SetDescription(save, count > 0 ? "Save my memories to a file" : "Save my memories to a file, nothing to save yet");
            SemanticProperties.SetDescription(load, "Load memories from a file");
            save.Clicked += async (_, _) => await SaveToFileAsync(store);
            load.Clicked += async (_, _) => await LoadFromFileAsync(store);
            _own.Add(new HorizontalStackLayout { Spacing = 8, Children = { save, load } });
            _own.Add(_fileWorking);

            var deleteAll = new Button
            {
                Text = "Delete all my memories…",
                TextColor = Colors.White,
                BackgroundColor = Color.FromArgb("#B3261E"),
                IsEnabled = count > 0 && !_fileBusy,
            };

            deleteAll.Clicked += async (_, _) => await DeleteAllAsync(store);
            _own.Add(deleteAll);
        }

        /// <summary>
        /// Save to a file (CH-54 step 3): one plain sentence first, then the system Save dialog; the result in counts.
        /// Never throws.
        /// </summary>
        private async Task SaveToFileAsync(OwnMemoryStore store)
        {
            if (_fileBusy || Navigation.NavigationStack.LastOrDefault() != this)
            {
                return;
            }

            try
            {
                if (!await DisplayAlertAsync("Save to a file", OwnMemoryFile.SaveWarning, "Save", "Cancel"))
                {
                    return;
                }

                await WhileWorkingAsync("Saving your memories…", async () =>
                {
                    if (await OwnMemoryFileDialogs.SaveAsync(store) is { } message)
                    {
                        await DisplayAlertAsync("Save to a file", message, "OK");
                    }
                });
            }
            catch (Exception)
            {
                // A button handler: nothing here may end the app.
            }
        }

        /// <summary>
        /// Load from a file (CH-54 step 3): what it does to deleted memories first, then the system Open dialog; the result
        /// in counts, and everything refreshed as after a save. Never throws.
        /// </summary>
        private async Task LoadFromFileAsync(OwnMemoryStore store)
        {
            if (_fileBusy || Navigation.NavigationStack.LastOrDefault() != this)
            {
                return;
            }

            try
            {
                if (!await DisplayAlertAsync("Load from a file", OwnMemoryFile.LoadWarning, "Load", "Cancel"))
                {
                    return;
                }

                await WhileWorkingAsync("Loading memories…", async () =>
                {
                    if (await OwnMemoryFileDialogs.LoadAsync(store) is not { } outcome)
                    {
                        return;
                    }

                    if (outcome.Added)
                    {
                        await _ownChanged();
                    }

                    await DisplayAlertAsync("Load from a file", outcome.Message, "OK");
                });
            }
            catch (Exception)
            {
                // A button handler: nothing here may end the app.
            }
        }

        /// <summary>Runs <paramref name="work"/> with the file buttons off and a line saying what is happening, then reads the section again.</summary>
        private async Task WhileWorkingAsync(string text, Func<Task> work)
        {
            _fileBusy = true;
            _fileWorking.Text = text;
            _fileWorking.IsVisible = true;
            await RebuildOwnAsync();

            try
            {
                await work();
            }
            finally
            {
                _fileBusy = false;
                _fileWorking.IsVisible = false;
                await RebuildOwnAsync();
            }
        }

        private async Task DeleteAllAsync(OwnMemoryStore store)
        {
            try
            {
                if (_fileBusy
                    || !await DisplayAlertAsync(
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
                await RebuildOwnAsync();
            }
            catch (Exception exception) when (IsStoreFailure(exception))
            {
                await DisplayAlertAsync("Delete all my memories", "Your memories could not be deleted. Try again.", "OK");
            }
            catch (Exception)
            {
                // Step 2 review N4: anything else from the refresh is reported, never the end of the app.
                try
                {
                    await DisplayAlertAsync("Delete all my memories", "Something went wrong. Open Settings again to see your memories.", "OK");
                }
                catch (Exception)
                {
                    // The page may be gone.
                }
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

        /// <summary>Import archive (CH-78): the archive closes and the start screen's import runs. Never throws.</summary>
        private async void OnImportClicked(object? sender, EventArgs e)
        {
            if (_importing || Navigation.NavigationStack.LastOrDefault() != this)
            {
                return;
            }

            _importing = true;

            try
            {
                await _importArchive();
            }
            catch (Exception)
            {
                // A button handler: nothing here may end the app.
            }
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
