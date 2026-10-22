using Android.App;
using Android.Content;
using Chatcheology.Mobile.Library;

namespace Chatcheology.Android
{
    /// <summary>
    /// The app's own FileProvider, through which a restored media file is handed to another app. Not
    /// exported; a link works only with the one-file read grant that comes with it; and only files under
    /// <c>files/archives/</c> can be given out (<c>Resources/xml/archive_file_paths.xml</c>).
    /// </summary>
    [ContentProvider(
        ["${applicationId}.archivefiles"],
        Name = "org.chatcheology.viewer.ArchiveFileProvider",
        Exported = false,
        GrantUriPermissions = true)]
    [MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/archive_file_paths")]
    public sealed class ArchiveFileProvider : AndroidX.Core.Content.FileProvider
    {
        /// <summary>The provider's authority for this app.</summary>
        internal static string Authority(Context context) => context.PackageName + ".archivefiles";
    }

    /// <summary>What happened when a file or address was handed to another app.</summary>
    internal enum OpenResult
    {
        /// <summary>Another app was started.</summary>
        Opened,

        /// <summary>No app on the phone opens it.</summary>
        NoApp,

        /// <summary>The file is not one the app can give out.</summary>
        NotAllowed,
    }

    /// <summary>Hands restored media and tapped addresses to other apps.</summary>
    internal static class ExternalOpener
    {
        /// <summary>
        /// Opens a restored media file in another app through <see cref="ArchiveFileProvider"/>, with a
        /// read grant for this one file and the MIME type from its extension.
        /// </summary>
        internal static OpenResult OpenFile(string fullPath, string mimeType, string archivesFolder)
        {
            var activity = Platform.CurrentActivity;

            if (activity is null)
            {
                return OpenResult.NotAllowed;
            }

            // Only files inside the installed archives, whatever the provider's own paths say.
            var full = Path.GetFullPath(fullPath);

            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(archivesFolder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return OpenResult.NotAllowed;
            }

            global::Android.Net.Uri uri;

            try
            {
                uri = AndroidX.Core.Content.FileProvider.GetUriForFile(activity, ArchiveFileProvider.Authority(activity), new Java.IO.File(full))!;
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                return OpenResult.NotAllowed;
            }

            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, mimeType);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);

            return Start(activity, intent);
        }

        /// <summary>Opens a tapped web or email address in the app that handles it.</summary>
        internal static OpenResult OpenAddress(string target)
        {
            var activity = Platform.CurrentActivity;

            if (activity is null || !Mobile.Presentation.LinkScanner.IsFollowable(target))
            {
                return OpenResult.NotAllowed;
            }

            var uri = global::Android.Net.Uri.Parse(target);
            var intent = target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                ? new Intent(Intent.ActionSendto, uri)
                : new Intent(Intent.ActionView, uri);

            intent.AddCategory(Intent.CategoryBrowsable);

            return Start(activity, intent);
        }

        private static OpenResult Start(Activity activity, Intent intent)
        {
            try
            {
                activity.StartActivity(intent);
                return OpenResult.Opened;
            }
            catch (ActivityNotFoundException)
            {
                return OpenResult.NoApp;
            }
        }
    }
}
