using Chatcheology.Android.Pages;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;

namespace Chatcheology.Android
{
    /// <summary>The application: one window, a navigation stack, the start page.</summary>
    public partial class App : Application
    {
        private readonly ArchiveLibrary _library;
        private readonly ArchiveSettings _settings;

        /// <summary>Creates the app and removes whatever an interrupted import left in staging.</summary>
        public App()
        {
            InitializeComponent();

            // The app's private files folder (Context.FilesDir). Only this app can read it.
            _library = new ArchiveLibrary(FileSystem.AppDataDirectory);
            _library.CleanStaging();

            _settings = new ArchiveSettings(new PreferencesStore());
        }

        /// <inheritdoc />
        protected override Window CreateWindow(IActivationState? activationState) =>
            new(new NavigationPage(new StartPage(_library, _settings))
            {
                BarBackgroundColor = (Color)Resources["Primary"],
                BarTextColor = Colors.White,
            });

        /// <summary>
        /// The app's private preferences (Android SharedPreferences), which the backup and
        /// device-transfer rules exclude with everything else.
        /// </summary>
        private sealed class PreferencesStore : ISettingsStore
        {
            public string? Get(string key) => Preferences.Default.Get<string?>(key, null);

            public void Set(string key, string? value)
            {
                if (value is null)
                {
                    Preferences.Default.Remove(key);
                }
                else
                {
                    Preferences.Default.Set(key, value);
                }
            }
        }
    }
}
