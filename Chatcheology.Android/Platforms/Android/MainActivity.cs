using Android.App;
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

        /// <inheritdoc />
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            Current = this;

            // Registered before the activity starts, as the result API requires.
            _pickFolder = RegisterForActivityResult(new ActivityResultContracts.OpenDocumentTree(), new FolderPicked(this));
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
