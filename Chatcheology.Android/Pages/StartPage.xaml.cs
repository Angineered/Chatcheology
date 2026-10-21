using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Library;
#if DEBUG
using Chatcheology.Mobile.Synthetic;
using AndroidLog = global::Android.Util.Log;
#endif

namespace Chatcheology.Android.Pages
{
    /// <summary>Which list shows the conversation.</summary>
    internal enum ConversationListKind
    {
        /// <summary>MAUI CollectionView, with its default <see cref="ItemsUpdatingScrollMode.KeepItemsInView"/>.</summary>
        CollectionViewKeepItemsInView,

        /// <summary>MAUI CollectionView with <see cref="ItemsUpdatingScrollMode.KeepScrollOffset"/>.</summary>
        CollectionViewKeepScrollOffset,

        /// <summary>A native Android RecyclerView.</summary>
        RecyclerView,
    }

    /// <summary>The start screen.</summary>
    public partial class StartPage : ContentPage
    {
        /// <summary>
        /// The list a Release build uses. The CH-26 trial found MAUI's CollectionView steady when a page
        /// is added above, so the design's default stands; the other lists stay selectable in Debug.
        /// </summary>
        internal const ConversationListKind ReleaseList = ConversationListKind.CollectionViewKeepItemsInView;

        private readonly ArchiveLibrary _library;

        internal StartPage(ArchiveLibrary library)
        {
            InitializeComponent();

            _library = library;

#if DEBUG
            AddDevelopmentTools();
#endif
        }

        /// <inheritdoc />
        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            Archives.Children.Clear();

            var installed = await Task.Run(() => _library.ListInstalled()
                .Select(folder => (Folder: folder, Summary: Describe(folder)))
                .ToList());

            Summary.Text = installed.Count == 0
                ? "No archive is installed yet."
                : installed.Count == 1 ? "One archive is installed." : $"{installed.Count} archives are installed.";

            foreach (var (folder, summary) in installed)
            {
                Archives.Children.Add(new Label { Text = summary, FontAttributes = FontAttributes.Bold });

#if DEBUG
                foreach (var kind in Enum.GetValues<ConversationListKind>())
                {
                    Archives.Children.Add(OpenButton(folder, kind));
                }
#else
                Archives.Children.Add(OpenButton(folder, ReleaseList));
#endif
            }
        }

        private Button OpenButton(string folder, ConversationListKind kind)
        {
#if DEBUG
            var text = $"Open: {kind}";
#else
            var text = "Open";
#endif
            var button = new Button { Text = text };
            button.Clicked += async (_, _) => await OpenAsync(folder, kind);

            return button;
        }

        private async Task OpenAsync(string folder, ConversationListKind kind)
        {
            ArchiveReader reader;

            try
            {
                reader = await Task.Run(() => ArchiveReader.Open(folder));
            }
            catch (ArchiveFormatException exception)
            {
                Status.Text = exception.Message;
                return;
            }

            Page page = kind switch
            {
                ConversationListKind.RecyclerView => new NativeConversationPage(reader),
                ConversationListKind.CollectionViewKeepScrollOffset => new ConversationPage(reader, ItemsUpdatingScrollMode.KeepScrollOffset),
                _ => new ConversationPage(reader, ItemsUpdatingScrollMode.KeepItemsInView),
            };

            await Navigation.PushAsync(page);
        }

        /// <summary>The conversation title and message count; never a path.</summary>
        private static string Describe(string folder)
        {
            try
            {
                using var reader = ArchiveReader.Open(folder);

                return $"{reader.Info.ConversationTitle} · {reader.ReadMessageCount().ToString("N0", CultureInfo.CurrentCulture)} messages";
            }
            catch (ArchiveFormatException)
            {
                return "An archive that cannot be opened";
            }
        }

        /// <summary>The validator's result by check name and count only.</summary>
        private static string DescribeValidation(ArchiveValidationResult result)
        {
            var passed = result.Checks.Count(check => check.Passed);
            var lines = result.Checks.Select(check =>
                $"{check.Check}: {(check.Passed ? "passed" : "FAILED")} ({check.Examined} examined, {check.Problems} problems)");

            return $"Validation {(result.Passed ? "passed" : "FAILED")}: {passed} of {result.Checks.Count} checks.\n" + string.Join("\n", lines);
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
            install.Clicked += async (_, _) => await InstallSyntheticAsync();
            DevelopmentTools.Children.Add(install);

            var validate = new Button { Text = "Validate installed archives" };
            validate.Clicked += async (_, _) => await ValidateInstalledAsync();
            DevelopmentTools.Children.Add(validate);
        }

        /// <summary>
        /// Writes the synthetic archive into staging with Microsoft.Data.Sqlite, validates it there and
        /// installs it, exactly as an import will once the copy is made.
        /// </summary>
        private async Task InstallSyntheticAsync()
        {
            Status.Text = "Writing the synthetic archive…";

            var (outcome, seconds) = await Task.Run(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var staging = _library.CreateStagingFolder();
                SyntheticArchive.Write(staging, new SyntheticArchiveOptions());

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
