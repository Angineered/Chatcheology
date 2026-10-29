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

    /// <summary>Hands restored media, shared text and tapped addresses to other apps.</summary>
    internal static class ExternalOpener
    {
        /// <summary>
        /// Opens a restored media file in another app through <see cref="ArchiveFileProvider"/>, with a
        /// read grant for this one file and the MIME type from its extension.
        /// </summary>
        internal static OpenResult OpenFile(string fullPath, string mimeType, string archivesFolder)
        {
            var activity = Platform.CurrentActivity;

            if (activity is null || ArchiveUri(activity, fullPath, archivesFolder) is not { } uri)
            {
                return OpenResult.NotAllowed;
            }

            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, mimeType);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);

            return Start(activity, intent);
        }

        /// <summary>
        /// Offers one restored media file to the share sheet (<c>ACTION_SEND</c>) through
        /// <see cref="ArchiveFileProvider"/>: a read grant for this one URI, not persistable, and the MIME
        /// type from its extension. The person chooses the app; nothing is sent until she does.
        /// </summary>
        internal static OpenResult ShareFile(string fullPath, string mimeType, string archivesFolder)
        {
            var activity = Platform.CurrentActivity;

            if (activity is null || ArchiveUri(activity, fullPath, archivesFolder) is not { } uri)
            {
                return OpenResult.NotAllowed;
            }

            var intent = new Intent(Intent.ActionSend);
            intent.SetType(mimeType);
            intent.PutExtra(Intent.ExtraStream, uri);

            // The grant travels with the clip, so the chooser passes it to the chosen app (and only it).
            intent.ClipData = ClipData.NewRawUri(string.Empty, uri);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);

            return Start(activity, Intent.CreateChooser(intent, "Share")!);
        }

        /// <summary>Offers a message's text to the share sheet (<c>ACTION_SEND</c>, plain text).</summary>
        internal static OpenResult ShareText(string text)
        {
            var activity = Platform.CurrentActivity;

            if (activity is null || string.IsNullOrEmpty(text))
            {
                return OpenResult.NotAllowed;
            }

            var intent = new Intent(Intent.ActionSend);
            intent.SetType("text/plain");
            intent.PutExtra(Intent.ExtraText, text);

            return Start(activity, Intent.CreateChooser(intent, "Share")!);
        }

        /// <summary>
        /// The provider's URI for a file inside the installed archives, or <see langword="null"/> for any
        /// other file, whatever the provider's own paths say.
        /// </summary>
        private static global::Android.Net.Uri? ArchiveUri(Activity activity, string fullPath, string archivesFolder)
        {
            var full = Path.GetFullPath(fullPath);

            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(archivesFolder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                return AndroidX.Core.Content.FileProvider.GetUriForFile(activity, ArchiveFileProvider.Authority(activity), new Java.IO.File(full));
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                return null;
            }
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

        /// <summary>
        /// Opens an external gallery link (CH-48) in the app that handles it: <c>ACTION_VIEW</c>, category
        /// <c>BROWSABLE</c>, no flags. <paramref name="launchUri"/> must be <c>ExternalLinkRules.LaunchUri</c> of the
        /// stored address, computed just before (<c>GalleryLinks.Open</c> does that); anything not starting with
        /// <c>https://</c> is refused here as well. Needs no permission and no <c>&lt;queries&gt;</c> entry: the
        /// app starts the activity and never asks which apps could take it.
        /// </summary>
        internal static OpenResult OpenWebLink(string launchUri)
        {
            var activity = Platform.CurrentActivity;

            if (activity is null || !launchUri.StartsWith("https://", StringComparison.Ordinal))
            {
                return OpenResult.NotAllowed;
            }

            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(launchUri));
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
