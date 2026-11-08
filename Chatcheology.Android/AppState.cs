using Chatcheology.Mobile.Import;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.OwnMemories;
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

        /// <summary>
        /// The phone owner's own memories (CH-54): one store per app, in <c>files/own/</c>, shared by every screen and the
        /// notification receivers (<see cref="OwnStore"/>).
        /// </summary>
        internal OwnMemoryStore OwnMemories => OwnStore;

        /// <summary>Where a memory photo may be shared from (CH-59, CH-54): the installed archives and the own photos.</summary>
        internal IReadOnlyList<string> ShareRoots => [Library.ArchivesFolder, OwnMemories.PhotosFolder];

        /// <summary>The one own memories store of the process, also for receivers that run without the app's screens.</summary>
        internal static OwnMemoryStore OwnStore => LazyOwnStore.Value;

        private static readonly Lazy<OwnMemoryStore> LazyOwnStore = new(() => new OwnMemoryStore(FileSystem.AppDataDirectory));

        /// <summary>Whether an import is under way: only one at a time.</summary>
        internal bool Importing { get; set; }

        /// <summary>Logs a timing, in milliseconds, with counts only.</summary>
        internal static void LogTiming(string what, TimeSpan elapsed, string counts = "") =>
            AndroidLog.Info(LogTag, $"timing {what} ms={elapsed.TotalMilliseconds:F0}{(counts.Length > 0 ? " " + counts : string.Empty)}");

        /// <summary>
        /// The app's private preferences (Android SharedPreferences), which the backup and
        /// device-transfer rules exclude with everything else.
        /// </summary>
        internal sealed class PreferencesStore : ISettingsStore
        {
            /// <summary>
            /// MAUI's default preferences file (Essentials: package name + <c>.microsoft.maui.essentials.preferences</c>),
            /// the one <see cref="Preferences.Default"/> writes to with <c>apply()</c>, which reaches disk later.
            /// </summary>
            private static string SharedName => AppInfo.PackageName + ".microsoft.maui.essentials.preferences";

            public string? Get(string key) => Preferences.Default.Get<string?>(key, null);

            /// <summary>
            /// CH-71 step 2: a synchronous <c>commit()</c> on the same file. Android writes a file's changes in order and
            /// <c>commit()</c> waits for its own write, so every earlier <c>apply()</c> is on disk when it returns true.
            /// </summary>
            public bool Flush()
            {
                try
                {
                    using var preferences = global::Android.App.Application.Context.GetSharedPreferences(SharedName, global::Android.Content.FileCreationMode.Private);
                    using var editor = preferences?.Edit();

                    return editor?.Commit() == true;
                }
                catch (Java.Lang.Exception)
                {
                    return false;
                }
            }

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
