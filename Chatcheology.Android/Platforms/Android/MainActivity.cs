using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidUri = Android.Net.Uri;

namespace Chatcheology.Android
{
    /// <summary>The launcher activity: the only exported component of the app.</summary>
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        Exported = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
            | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        private static string? _pendingArchive;
        private ActivityResultLauncher? _pickFolder;
        private TaskCompletionSource<AndroidUri?>? _picking;

        /// <summary>The running activity, while it exists.</summary>
        internal static MainActivity? Current { get; private set; }

        /// <summary>
        /// Opens the system folder picker (<c>ACTION_OPEN_DOCUMENT_TREE</c>) and returns the picked
        /// folder's tree address, or <see langword="null"/> when nothing was picked. The app gets read
        /// access to that folder only, for as long as it runs; no storage permission is involved.
        /// </summary>
        internal Task<AndroidUri?> PickFolderAsync()
        {
            if (_pickFolder is null)
            {
                return Task.FromResult<AndroidUri?>(null);
            }

            _picking?.TrySetResult(null);
            _picking = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pickFolder.Launch(null);

            return _picking.Task;
        }

        /// <summary>
        /// The archive a noted-date notification asked to open (its folder name), taken once; or
        /// <see langword="null"/>. The start screen opens it only when it is installed.
        /// </summary>
        internal static string? TakePendingArchive() => Interlocked.Exchange(ref _pendingArchive, null);

        /// <inheritdoc />
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            // Before the start screen exists, so it finds the archive when it first appears. A recreated activity
            // carries its first intent again: only a fresh start reads it. A start without the extra (the launcher)
            // clears any archive left from an ignored tap.
            if (savedInstanceState is null)
            {
                TakeArchive(Intent);
            }

            base.OnCreate(savedInstanceState);

            Current = this;

            // Registered before the activity starts, as the result API requires.
            _pickFolder = RegisterForActivityResult(new ActivityResultContracts.OpenDocumentTree(), new FolderPicked(this));
        }

        /// <summary>
        /// Every theme the activity takes (the splash theme, then MAUI's main theme) gets the app's dialog themes
        /// merged in, so the system time and date pickers have readable buttons in light and dark mode
        /// (Resources/values/styles.xml).
        /// </summary>
        protected override void OnApplyThemeResource(global::Android.Content.Res.Resources.Theme? theme, int resid, bool first)
        {
            base.OnApplyThemeResource(theme, resid, first);
            theme?.ApplyStyle(Resource.Style.Chatcheology_DialogThemes, force: true);
        }

        /// <summary>A noted-date notification was tapped while the app was running: open that archive.</summary>
        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);

            if (TakeArchive(intent)
                && Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page is NavigationPage { RootPage: Pages.StartPage start })
            {
                _ = start.OpenFromNotificationAsync();
            }
        }

        /// <summary>
        /// Keeps the archive a notification's tap names, replacing any earlier one; an intent without it clears it.
        /// The value is only ever matched against installed folder names. An intent whose extras cannot be read
        /// (another app's crafted Parcelable throws on Android 12 and older) counts as one without it.
        /// </summary>
        private static bool TakeArchive(Intent? intent)
        {
            string? id;

            try
            {
                id = intent?.GetStringExtra(NotedDateNotifier.ArchiveExtra);

                if (id is not null)
                {
                    intent!.RemoveExtra(NotedDateNotifier.ArchiveExtra);
                }
            }
            catch (Exception)
            {
                // BadParcelableException and the like: the extra is ignored.
                id = null;
            }

            if (id is not { Length: > 0 and <= 64 })
            {
                _pendingArchive = null;
                return false;
            }

            _pendingArchive = id;

            return true;
        }

        /// <inheritdoc />
        protected override void OnDestroy()
        {
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }

            _picking?.TrySetResult(null);
            base.OnDestroy();
        }

        private sealed class FolderPicked(MainActivity activity) : Java.Lang.Object, IActivityResultCallback
        {
            public void OnActivityResult(Java.Lang.Object? result) =>
                activity._picking?.TrySetResult(result as AndroidUri);
        }
    }
}
