using System.Diagnostics;
using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
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

            // Only when the app starts: coming back from the conversation shows this screen.
            if (starting && opening is { Screen: StartScreen.OpenConversation, Archive: { IsReadable: true } only })
            {
                await OpenAsync(only);
            }
        }

        /// <summary>Opens the installed archive with folder name <paramref name="id"/>, after an import.</summary>
        internal async Task OpenByIdAsync(string id)
        {
            var opening = await RefreshAsync();

            if (opening?.Archives.FirstOrDefault(archive => archive.Id == id) is { IsReadable: true } archive)
            {
                await OpenAsync(archive);
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
            var resources = Application.Current!.Resources;
            var layout = new VerticalStackLayout { Spacing = 2 };

            if (archive.IsReadable)
            {
                var created = archive.CreatedUtc?.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture) ?? "unknown";

                layout.Children.Add(new Label { Text = archive.ConversationTitle, FontSize = 17, FontAttributes = FontAttributes.Bold });
                layout.Children.Add(new Label
                {
                    Text = $"Created {created} · {archive.MessageCount.ToString("N0", CultureInfo.CurrentCulture)} messages",
                    FontSize = 13,
                    TextColor = (Color)resources["MutedText"],
                });
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
                BackgroundColor = (Color)resources["CardBackground"],
                StrokeThickness = 0,
                Padding = new Thickness(12, 10),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Content = layout,
            };

            if (archive.IsReadable)
            {
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) => await OpenAsync(archive);
                item.GestureRecognizers.Add(tap);
            }

            return item;
        }

        private async Task OpenAsync(InstalledArchive archive)
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
                    opened = await Task.Run(() => ArchiveSession.OpenConversation(archive, _state.Settings));
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

                await Navigation.PushAsync(new ConversationPage(_state, opened, watch));
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

                var page = new ImportPage(_state, new DocumentTreeSource(activity.ContentResolver!, tree), this);

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

            await RefreshAsync();
        }

#if DEBUG
        // -------------------------------------------------------------------------------------
        // Debug builds only.
        // -------------------------------------------------------------------------------------

        private void AddDevelopmentTools()
        {
            DevelopmentTools.Children.Add(new Label { Text = "Development (Debug build only)", FontAttributes = FontAttributes.Bold });

            var install = new Button { Text = "Install synthetic archive" };
            install.Clicked += async (_, _) => await InstallSyntheticAsync(new SyntheticArchiveOptions(
                Audio: 2, Documents: 1, BrokenImages: 1, LowQualityCopies: 3, Forwarded: 30, Links: 20));
            DevelopmentTools.Children.Add(install);

            // 130 days for 3,000 messages: about 23 a day, so 150-message pages join inside a day.
            var midDay = new Button { Text = "Install synthetic archive (mid-day joins)" };
            midDay.Clicked += async (_, _) => await InstallSyntheticAsync(new SyntheticArchiveOptions(Days: 130));
            DevelopmentTools.Children.Add(midDay);

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

            var text = $"{(outcome.Installed ? "Installed" : "Not installed")} in {seconds:F1} s. {DescribeValidation(outcome.Validation)}";
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
