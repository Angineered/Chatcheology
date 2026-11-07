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
        private ActivityResultLauncher? _pickPhoto;
        private TaskCompletionSource<AndroidUri?>? _pickingPhoto;
        private ActivityResultLauncher? _document;
        private TaskCompletionSource<AndroidUri?>? _pickingDocument;

        /// <summary>
        /// Opens the system Save dialog (<c>ACTION_CREATE_DOCUMENT</c>) for a new file of <paramref name="mimeType"/>, suggesting
        /// <paramref name="name"/> (CH-54 step 3), and returns its address, or <see langword="null"/> when cancelled. No
        /// permission: the app may write that one file.
        /// </summary>
        internal Task<AndroidUri?> CreateDocumentAsync(string mimeType, string name)
        {
            var intent = new Intent(Intent.ActionCreateDocument)
                .AddCategory(Intent.CategoryOpenable)
                .SetType(mimeType)
                .PutExtra(Intent.ExtraTitle, name);

            return LaunchDocument(intent);
        }

        /// <summary>
        /// Opens the system Open dialog (<c>ACTION_OPEN_DOCUMENT</c>) for one file of <paramref name="mimeTypes"/> (CH-54 step 3)
        /// and returns its address, or <see langword="null"/> when cancelled. No permission: the app may read that one file.
        /// </summary>
        internal Task<AndroidUri?> OpenDocumentAsync(string[] mimeTypes)
        {
            var intent = new Intent(Intent.ActionOpenDocument)
                .AddCategory(Intent.CategoryOpenable)
                .SetType(mimeTypes.Length == 1 ? mimeTypes[0] : "*/*")
                .PutExtra(Intent.ExtraMimeTypes, mimeTypes);

            return LaunchDocument(intent);
        }

        private Task<AndroidUri?> LaunchDocument(Intent intent)
        {
            if (_document is null)
            {
                return Task.FromResult<AndroidUri?>(null);
            }

            _pickingDocument?.TrySetResult(null);
            _pickingDocument = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _document.Launch(intent);

            return _pickingDocument.Task;
        }

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
        /// Opens the system photo picker for one picture (CH-54: AndroidX <c>PickVisualMedia</c>, images only) and returns
        /// its content address, or <see langword="null"/> when nothing was picked. No permission is involved: the app may
        /// read that one picture. Where the system picker is missing (before Android 11 without the SDK extension), the
        /// contract falls back to the document picker; there is no Play-services backport, so nothing is added to the
        /// manifest.
        /// </summary>
        internal Task<AndroidUri?> PickPhotoAsync()
        {
            if (_pickPhoto is null)
            {
                return Task.FromResult<AndroidUri?>(null);
            }

            _pickingPhoto?.TrySetResult(null);
            _pickingPhoto = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var request = new PickVisualMediaRequest.Builder()
                .SetMediaType(ActivityResultContracts.PickVisualMedia.ImageOnly.Instance)
                .Build();

            _pickPhoto.Launch(request);

            return _pickingPhoto.Task;
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
            _pickPhoto = RegisterForActivityResult(new ActivityResultContracts.PickVisualMedia(), new PhotoPicked(this));
            _document = RegisterForActivityResult(new ActivityResultContracts.StartActivityForResult(), new DocumentPicked(this));
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
            _pickingPhoto?.TrySetResult(null);
            _pickingDocument?.TrySetResult(null);
            base.OnDestroy();
        }

        private sealed class DocumentPicked(MainActivity activity) : Java.Lang.Object, IActivityResultCallback
        {
            public void OnActivityResult(Java.Lang.Object? result) =>
                activity._pickingDocument?.TrySetResult(
                    result is ActivityResult { ResultCode: (int)Result.Ok } done ? done.Data?.Data : null);
        }

        private sealed class FolderPicked(MainActivity activity) : Java.Lang.Object, IActivityResultCallback
        {
            public void OnActivityResult(Java.Lang.Object? result) =>
                activity._picking?.TrySetResult(result as AndroidUri);
        }

        private sealed class PhotoPicked(MainActivity activity) : Java.Lang.Object, IActivityResultCallback
        {
            public void OnActivityResult(Java.Lang.Object? result) =>
                activity._pickingPhoto?.TrySetResult(result as AndroidUri);
        }
    }
}
