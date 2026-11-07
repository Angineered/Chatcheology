using System.Diagnostics;
using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Import;
using Chatcheology.Mobile.Library;
using Microsoft.Data.Sqlite;
#if DEBUG
using Chatcheology.Mobile.Synthetic;
using AndroidLog = global::Android.Util.Log;
#endif

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The start screen, following the opening rule: no archive, a note and "Import archive…"; exactly
    /// one, straight into its conversation; several, the list with the last opened first.
    /// </summary>
    public partial class StartPage : ContentPage
    {
        private readonly AppState _state;
        private bool _started;
        private bool _opening;

        internal StartPage(AppState state)
        {
            InitializeComponent();

            _state = state;

#if DEBUG
            AddDevelopmentTools();
#endif
        }

        /// <inheritdoc />
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            var opening = await RefreshAsync();
            var starting = !_started;
            _started = true;

            if (!starting)
            {
                // Coming back from the conversation shows this screen.
                return;
            }

            // Noted-date notifications (CH-47): scheduled again on every start.
            _ = NotedDateNotifier.UpdateAsync();

            // Started from a noted-date notification: that archive, with its On this day card.
            if (MainActivity.TakePendingArchive() is { } pending
                && opening?.Archives.FirstOrDefault(archive => archive.Id == pending) is { IsReadable: true } notified)
            {
                await OpenAsync(notified, showOnThisDay: true);
            }
            else if (opening is { Screen: StartScreen.OpenConversation, Archive: { IsReadable: true } only })
            {
                await OpenAsync(only);
            }
        }

        /// <summary>Opens the installed archive with folder name <paramref name="id"/>, after an import.</summary>
        internal async Task OpenByIdAsync(string id, bool showOnThisDay = false)
        {
            var opening = await RefreshAsync();

            if (opening?.Archives.FirstOrDefault(archive => archive.Id == id) is { IsReadable: true } archive)
            {
                await OpenAsync(archive, showOnThisDay);
            }
        }

        /// <summary>
        /// A noted-date notification was tapped while the app was running: back to this screen, then that archive
        /// with its On this day card. Ignored during an import. Never throws.
        /// </summary>
        internal async Task OpenFromNotificationAsync()
        {
            try
            {
                // Taken first, so an ignored tap does not open later from a launcher start.
                if (MainActivity.TakePendingArchive() is not { } id || !_started || _state.Importing)
                {
                    return;
                }

                await Navigation.PopToRootAsync(animated: false);
                await OpenByIdAsync(id, showOnThisDay: true);
            }
            catch (Exception)
            {
                // The start screen shows what it can; nothing here may end the app.
            }
        }

        private async Task<Opening?> RefreshAsync()
        {
            Archives.Children.Clear();
            ImportButton.IsEnabled = !_state.Importing;

            Opening opening;

            try
            {
                opening = await Task.Run(() => OpeningRule.Decide(_state.Library.ListArchives(_state.Settings)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Summary.Text = "The installed archives could not be listed.";
                return null;
            }

            Summary.Text = opening.Screen switch
            {
                StartScreen.NoArchive => "No archive is installed yet. Import one to read it here.",
                StartScreen.OpenConversation => "One archive is installed.",
                _ => $"{opening.Archives.Count.ToString(CultureInfo.CurrentCulture)} archives are installed.",
            };

            foreach (var archive in opening.Archives)
            {
                Archives.Children.Add(ArchiveItem(archive));
            }

            return opening;
        }

        /// <summary>One archive in the list: title, created date and message count; never a path.</summary>
        private View ArchiveItem(InstalledArchive archive)
        {
            var layout = new VerticalStackLayout { Spacing = 2 };

            if (archive.IsReadable)
            {
                var created = archive.CreatedUtc?.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture) ?? "unknown";

                layout.Children.Add(new Label { Text = archive.ConversationTitle, FontSize = 17, FontAttributes = FontAttributes.Bold });
                layout.Children.Add(new Label
                {
                    Text = $"Created {created} · {archive.MessageCount.ToString("N0", CultureInfo.CurrentCulture)} messages",
                    FontSize = 13,
                }.TextIn("MutedText"));
            }
            else
            {
                layout.Children.Add(new Label { Text = "An archive that cannot be opened", FontSize = 17, FontAttributes = FontAttributes.Bold });

                var remove = new Button { Text = "Remove it", HorizontalOptions = LayoutOptions.Start };
                remove.Clicked += async (_, _) => await RemoveUnreadableAsync(archive);
                layout.Children.Add(remove);
            }

            var item = new Border
            {
                StrokeThickness = 0,
                Padding = new Thickness(12, 10),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Content = layout,
            }.Themed(VisualElement.BackgroundColorProperty, "CardBackground");

            if (archive.IsReadable)
            {
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) => await OpenAsync(archive);
                item.GestureRecognizers.Add(tap);
            }

            return item;
        }

        private async Task OpenAsync(InstalledArchive archive, bool showOnThisDay = false)
        {
            if (_opening)
            {
                return;
            }

            _opening = true;
            Status.Text = "Opening…";

            try
            {
                var watch = Stopwatch.StartNew();

                // Opening reads the whole conversation once (its low-quality links) and builds the first
                // rows: all off the UI thread. A failure closes the session again.
                OpenedConversation opened;

                try
                {
                    // CH-54: with the phone owner's own memories, one store for the whole app.
                    opened = await Task.Run(() => ArchiveSession.OpenConversation(archive, _state.Settings, ownStore: _state.OwnMemories));
                }
                catch (Exception exception) when (exception is ArchiveFormatException or ArgumentException)
                {
                    Status.Text = "This archive could not be opened.";
                    return;
                }
                catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
                {
                    Status.Text = "The archive could not be read.";
                    return;
                }

                AppState.LogTiming("open-read", watch.Elapsed, $"messages={opened.Session.MessageCount}");
                Status.Text = string.Empty;

                try
                {
                    await Navigation.PushAsync(new ConversationPage(_state, opened, watch, showOnThisDay));
                }
                catch (Exception)
                {
                    // The page never took the session over: close it here.
                    opened.Session.Dispose();
                    Status.Text = "This archive could not be opened.";
                }
            }
            finally
            {
                _opening = false;
            }
        }

        private async void OnImportClicked(object? sender, EventArgs e)
        {
            if (_state.Importing || MainActivity.Current is not { } activity)
            {
                return;
            }

            _state.Importing = true;
            ImportButton.IsEnabled = false;

            try
            {
                var tree = await activity.PickFolderAsync();

                if (tree is null)
                {
                    return;
                }

                // CH-58: Android 10+ asks for photo-location access once the folder is picked and before the
                // copy starts. Granted or refused, the import goes ahead; refused, photos may come back changed.
                var granted = await MediaLocationPermission.AskBeforeImportAsync();
                var apiLevel = MediaLocationPermission.ApiLevel;
                var source = new DocumentTreeSource(activity, tree, MediaLocationAccess.ChooseReadPath(apiLevel, granted));
                var page = new ImportPage(_state, source, this, source.ReadCounts, MediaLocationAccess.IsRefused(apiLevel, granted));

                // The import page clears the flag when it is done.
                await Navigation.PushAsync(page);
                page.Start();
            }
            finally
            {
                if (Navigation.NavigationStack.LastOrDefault() is not ImportPage)
                {
                    _state.Importing = false;
                    ImportButton.IsEnabled = true;
                }
            }
        }

        private async Task RemoveUnreadableAsync(InstalledArchive archive)
        {
            if (!await DisplayAlertAsync("Remove archive", "Remove this archive from the app? Only the app's copy is deleted.", "Remove", "Cancel"))
            {
                return;
            }

            try
            {
                await Task.Run(() => _state.Library.Remove(archive, _state.Settings));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Status.Text = "The archive could not be removed.";
            }

            // Its noted dates no longer notify (CH-47).
            NotedDateNotifier.Cancel(archive.Id);
            _ = NotedDateNotifier.UpdateAsync();

            await RefreshAsync();
        }

#if DEBUG
        // -------------------------------------------------------------------------------------
        // Debug builds only.
        // -------------------------------------------------------------------------------------

        private void AddDevelopmentTools()
        {
            DevelopmentTools.Children.Add(new Label { Text = "Development (Debug build only)", FontAttributes = FontAttributes.Bold });

            // Every button writes the current archive format with the three invented noted dates (CH-47); the
            // options are SyntheticDebugInstalls, which the tests check.
            var install = new Button { Text = "Install synthetic archive" };
            install.Clicked += async (_, _) => await InstallSyntheticAsync(SyntheticDebugInstalls.Standard);
            DevelopmentTools.Children.Add(install);

            // 130 days for 3,000 messages: about 23 a day, so 150-message pages join inside a day.
            var midDay = new Button { Text = "Install synthetic archive (mid-day joins)" };
            midDay.Clicked += async (_, _) => await InstallSyntheticAsync(SyntheticDebugInstalls.MidDay);
            DevelopmentTools.Children.Add(midDay);

            // CH-42: Shared gaming (off until switched on in Settings) and Holidays memory layers.
            var memories = new Button { Text = "Install synthetic archive with memories" };
            memories.Clicked += async (_, _) => await InstallSyntheticAsync(SyntheticDebugInstalls.WithMemories);
            DevelopmentTools.Children.Add(memories);

            var validate = new Button { Text = "Validate installed archives" };
            validate.Clicked += async (_, _) => await ValidateInstalledAsync();
            DevelopmentTools.Children.Add(validate);
        }

        /// <summary>The validator's result by check name and count only.</summary>
        private static string DescribeValidation(ArchiveValidationResult result)
        {
            var passed = result.Checks.Count(check => check.Passed);
            var lines = result.Checks.Select(check =>
                $"{check.Check}: {(check.Passed ? "passed" : "FAILED")} ({check.Examined} examined, {check.Problems} problems)");

            return $"Validation {(result.Passed ? "passed" : "FAILED")}: {passed} of {result.Checks.Count} checks.\n" + string.Join("\n", lines);
        }

        /// <summary>
        /// Writes the synthetic archive into staging with Microsoft.Data.Sqlite, validates it there and
        /// installs it, exactly as an import does once the copy is made.
        /// </summary>
        private async Task InstallSyntheticAsync(SyntheticArchiveOptions options)
        {
            Status.Text = "Writing the synthetic archive…";

            var (outcome, seconds) = await Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var staging = _state.Library.CreateStagingFolder();
                SyntheticArchive.Write(staging, options);

                var installed = _state.Library.Install(staging);

                if (installed.InstalledFolder is { } folder && ArchiveLibrary.ContentsSha256Of(folder) is { } hash)
                {
                    _state.Settings.SetValidation(
                        hash, DateTime.UtcNow, installed.Validation.Checks.Count(check => check.Passed), installed.Validation.Checks.Count);
                }

                return (installed, watch.Elapsed.TotalSeconds);
            });

            // CH-54 step 2: invented own memories (one yearly on today's date, one with photos, a duplicate), once.
            var own = 0;

            try
            {
                own = await Task.Run(() => SyntheticOwnMemories.SeedAsync(_state.OwnMemories, DateOnly.FromDateTime(DateTime.Today), new AndroidPhotoNormaliser()));
            }
            catch (Exception exception)
            {
                AndroidLog.Warn(AppState.LogTag, $"synthetic own memories failed: {exception.GetType().Name}");
            }

            _ = NotedDateNotifier.UpdateAsync();

            var text = $"{(outcome.Installed ? "Installed" : "Not installed")} in {seconds:F1} s. Own memories added: {own}. {DescribeValidation(outcome.Validation)}";
            Status.Text = text;

            AndroidLog.Info(AppState.LogTag, $"sqlite={SQLitePCL.raw.sqlite3_libversion().utf8_to_string()} " +
                $"privateStorage={outcome.InstalledFolder?.StartsWith(FileSystem.AppDataDirectory, StringComparison.Ordinal)}");
            AndroidLog.Info(AppState.LogTag, text.Replace('\n', ' '));

            await RefreshAsync();
        }

        private async Task ValidateInstalledAsync()
        {
            var results = await Task.Run(() => _state.Library.ListInstalled()
                .Select(folder => (Count: ReadCount(folder), Result: ArchiveValidator.Validate(folder)))
                .ToList());

            var text = results.Count == 0
                ? "Nothing installed."
                : string.Join("\n\n", results.Select(r => $"{r.Count} messages read. {DescribeValidation(r.Result)}"));

            Status.Text = text;
            AndroidLog.Info(AppState.LogTag, text.Replace('\n', ' '));
        }

        private static int ReadCount(string folder)
        {
            using var reader = ArchiveReader.Open(folder);

            return reader.ReadMessageCount();
        }
#endif
    }
}
