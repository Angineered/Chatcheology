using Chatcheology.Mobile.Import;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using AndroidLog = Android.Util.Log;

namespace Chatcheology.Android
{
    /// <summary>What every screen shares: the installed archives, the app's own settings, the importer.</summary>
    internal sealed class AppState
    {
        /// <summary>The log tag for timings. Numbers only: never message text, names, file names or paths.</summary>
        internal const string LogTag = "Chatcheology";

        internal AppState(string filesFolder)
        {
            Library = new ArchiveLibrary(filesFolder);
            Settings = new ArchiveSettings(new PreferencesStore());
            Importer = new ArchiveImporter(Library, new AppStorageSpace(filesFolder), Settings);
        }

        /// <summary>The installed archives and the staging area.</summary>
        internal ArchiveLibrary Library { get; }

        /// <summary>Me, positions, last opened, last validation.</summary>
        internal ArchiveSettings Settings { get; }

        /// <summary>Copies a picked folder in, checks it and installs it.</summary>
        internal ArchiveImporter Importer { get; }

        /// <summary>Whether an import is under way: only one at a time.</summary>
        internal bool Importing { get; set; }

        /// <summary>Logs a timing, in milliseconds, with counts only.</summary>
        internal static void LogTiming(string what, TimeSpan elapsed, string counts = "") =>
            AndroidLog.Info(LogTag, $"timing {what} ms={elapsed.TotalMilliseconds:F0}{(counts.Length > 0 ? " " + counts : string.Empty)}");

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
