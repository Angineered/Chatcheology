using Chatcheology.Mobile.Import;
using AndroidLog = global::Android.Util.Log;

namespace Chatcheology.Android
{
    /// <summary>
    /// CH-58: the photo-location permission (ACCESS_MEDIA_LOCATION). Without it Android 10+ blanks the
    /// location in photos read from the picked folder, and the copy fails its hash check. It gives no access
    /// to any file: the folder pick still decides what can be read.
    /// </summary>
    internal static class MediaLocationPermission
    {
        /// <summary>The device's API level.</summary>
        internal static int ApiLevel => (int)global::Android.OS.Build.VERSION.SdkInt;

        /// <summary>
        /// Before an import: on Android 10+ asks for the permission when it is not granted (Android itself
        /// stops showing the prompt once it has been refused for good). Returns whether it is granted now;
        /// the import goes ahead either way. Below Android 10 nothing is asked and it returns true.
        /// </summary>
        internal static async Task<bool> AskBeforeImportAsync()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(MediaLocationAccess.FirstRedactingApiLevel))
            {
                return true;
            }

            try
            {
                var granted = await Permissions.CheckStatusAsync<AccessMediaLocation>() == PermissionStatus.Granted;

                if (MediaLocationAccess.ShouldAsk(ApiLevel, granted))
                {
                    granted = await Permissions.RequestAsync<AccessMediaLocation>() == PermissionStatus.Granted;
                    AndroidLog.Info(AppState.LogTag, $"media-location permission={(granted ? "granted" : "refused")}");
                }

                return granted;
            }
            catch (Exception)
            {
                // A request that cannot be made counts as refused; the import still runs.
                return false;
            }
        }

        /// <summary>
        /// ACCESS_MEDIA_LOCATION as a MAUI permission (runtime, Android 10+; only asked for there). Spelled
        /// out because the binding's constant is marked Android 10+ while the app runs from Android 8.
        /// </summary>
        private sealed class AccessMediaLocation : Permissions.BasePlatformPermission
        {
            public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
                [("android.permission.ACCESS_MEDIA_LOCATION", true)];
        }
    }
}
