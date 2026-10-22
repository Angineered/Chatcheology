using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Microsoft.Data.Sqlite;
#if DEBUG
using Chatcheology.Mobile.Synthetic;
using AndroidLog = global::Android.Util.Log;
#endif

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The start screen, following the opening rule: no archive, a note (import comes in step 3);
    /// exactly one, straight into its conversation; several, the list with the last opened first.
    /// </summary>
    public partial class StartPage : ContentPage
    {
        private readonly ArchiveLibrary _library;
        private readonly ArchiveSettings _settings;
        private bool _started;

        internal StartPage(ArchiveLibrary library, ArchiveSettings settings)
        {
            InitializeComponent();

            _library = library;
            _settings = settings;

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

        private async Task<Opening?> RefreshAsync()
        {
            Archives.Children.Clear();

            Opening opening;

            try
            {
                opening = await Task.Run(() => OpeningRule.Decide(_library.ListArchives(_settings)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Summary.Text = "The installed archives could not be listed.";
                return null;
            }

            Summary.Text = opening.Screen switch
            {
                StartScreen.NoArchive => "No archive is installed yet.",
                StartScreen.OpenConversation => "One archive is installed.",
                _ => $"{opening.Archives.Count.ToString(CultureInfo.CurrentCulture)} archives are installed.",
            };

            foreach (var archive in opening.Archives)
            {
                Archives.Children.Add(new Label { Text = Describe(archive), FontAttributes = FontAttributes.Bold });

                if (archive.IsReadable)
                {
                    var button = new Button { Text = "Open" };
                    button.Clicked += async (_, _) => await OpenAsync(archive);
                    Archives.Children.Add(button);
                }
            }

            return opening;
        }

        private async Task OpenAsync(InstalledArchive archive)
        {
            ArchiveSession session;

            try
            {
                // Opening reads the whole conversation once (its low-quality links): off the UI thread.
                session = await Task.Run(() => ArchiveSession.Open(archive, _settings));
            }
            catch (ArchiveFormatException exception)
            {
                Status.Text = exception.Message;
                return;
            }
            catch (SqliteException)
            {
                Status.Text = "The archive database could not be read.";
                return;
            }

            await Navigation.PushAsync(new ConversationPage(session));
        }

        /// <summary>The conversation title, created date and message count; never a path.</summary>
        private static string Describe(InstalledArchive archive)
        {
            if (!archive.IsReadable)
            {
                return "An archive that cannot be opened";
            }

            var created = archive.CreatedUtc?.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

            return $"{archive.ConversationTitle} · {archive.MessageCount.ToString("N0", CultureInfo.CurrentCulture)} messages · made {created}";
        }

#if DEBUG
        // -------------------------------------------------------------------------------------
        // Debug builds only.
        // -------------------------------------------------------------------------------------

        private const string LogTag = "Chatcheology";

        private void AddDevelopmentTools()
        {
            DevelopmentTools.Children.Add(new Label { Text = "Development (Debug build only)", FontAttributes = FontAttributes.Bold });

            var install = new Button { Text = "Install synthetic archive" };
            install.Clicked += async (_, _) => await InstallSyntheticAsync(new SyntheticArchiveOptions());
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
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var staging = _library.CreateStagingFolder();
                SyntheticArchive.Write(staging, options);

                return (_library.Install(staging), watch.Elapsed.TotalSeconds);
            });

            var text = $"{(outcome.Installed ? "Installed" : "Not installed")} in {seconds:F1} s. {DescribeValidation(outcome.Validation)}";
            Status.Text = text;

            AndroidLog.Info(LogTag, $"sqlite={SQLitePCL.raw.sqlite3_libversion().utf8_to_string()} " +
                $"privateStorage={outcome.InstalledFolder?.StartsWith(FileSystem.AppDataDirectory, StringComparison.Ordinal)}");
            AndroidLog.Info(LogTag, text.Replace('\n', ' '));

            await RefreshAsync();
        }

        private async Task ValidateInstalledAsync()
        {
            var results = await Task.Run(() => _library.ListInstalled()
                .Select(folder => (Count: ReadCount(folder), Result: ArchiveValidator.Validate(folder)))
                .ToList());

            var text = results.Count == 0
                ? "Nothing installed."
                : string.Join("\n\n", results.Select(r => $"{r.Count} messages read. {DescribeValidation(r.Result)}"));

            Status.Text = text;
            AndroidLog.Info(LogTag, text.Replace('\n', ' '));
        }

        private static int ReadCount(string folder)
        {
            using var reader = ArchiveReader.Open(folder);

            return reader.ReadMessageCount();
        }
#endif
    }
}
