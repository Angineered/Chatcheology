using Chatcheology.Archive;

namespace Chatcheology.Mobile.Import
{
    /// <summary>How a picked file is opened for the import.</summary>
    public enum ImportReadPath
    {
        /// <summary>By its document address from the folder pick, as before CH-58.</summary>
        Document,

        /// <summary>
        /// Through the media store's address for the same file first, which returns a photo's location
        /// bytes as they are; the document address when that fails.
        /// </summary>
        MediaUri,
    }

    /// <summary>
    /// CH-58: Android 10 and later blank the location in photos read through a folder pick unless the app
    /// holds the photo-location permission (ACCESS_MEDIA_LOCATION). The copy then fails its hash check,
    /// which stays as it is: a photo that still comes back changed is missing, never wrong. These are the
    /// decisions around that permission, kept free of Android so they can be tested on the host.
    /// </summary>
    public static class MediaLocationAccess
    {
        /// <summary>The first Android API level that blanks photo locations (Android 10).</summary>
        public const int FirstRedactingApiLevel = 29;

        /// <summary>The line the failure page adds when the permission was refused.</summary>
        public const string RefusedLine =
            "Some photos may have been changed by Android because photo-location access was refused. Allow it in the app's settings and import again.";

        /// <summary>True when the permission is asked for before an import: Android 10+ and not yet granted.</summary>
        /// <param name="apiLevel">The device's API level.</param>
        /// <param name="granted">Whether the app holds the permission now.</param>
        public static bool ShouldAsk(int apiLevel, bool granted) => apiLevel >= FirstRedactingApiLevel && !granted;

        /// <summary>
        /// True when Android may have blanked photo locations on this import: Android 10+ without the
        /// permission. Below Android 10 nothing is blanked, so nothing was refused.
        /// </summary>
        /// <param name="apiLevel">The device's API level.</param>
        /// <param name="granted">Whether the app held the permission when the import started.</param>
        public static bool IsRefused(int apiLevel, bool granted) => apiLevel >= FirstRedactingApiLevel && !granted;

        /// <summary>
        /// Which address a picked file is read through: the media store's, only on Android 10+ with the
        /// permission granted; otherwise the document address, unchanged.
        /// </summary>
        /// <param name="apiLevel">The device's API level.</param>
        /// <param name="granted">Whether the app held the permission when the import started.</param>
        public static ImportReadPath ChooseReadPath(int apiLevel, bool granted) =>
            apiLevel >= FirstRedactingApiLevel && granted ? ImportReadPath.MediaUri : ImportReadPath.Document;

        /// <summary>
        /// True when the failure page adds <see cref="RefusedLine"/>: the permission was refused and the
        /// copy failed the contents or the media check (where a changed photo shows).
        /// </summary>
        /// <param name="refused">See <see cref="IsRefused"/>.</param>
        /// <param name="result">How the import ended.</param>
        public static bool ShowsRefusedLine(bool refused, ImportResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            return refused
                && result.Refusal == ImportRefusal.FailedValidation
                && result.FailedChecks.Any(check => check.Check is ArchiveCheck.Contents or ArchiveCheck.Media);
        }
    }

    /// <summary>
    /// How many files were opened through each address, for the import's log line. Counts only: never a
    /// URI, a name or a path. Safe to update from the copy's thread.
    /// </summary>
    public sealed class ImportReadCounts
    {
        private int _mediaUri;
        private int _fallback;

        /// <summary>Files opened through the media store's address.</summary>
        public int MediaUri => Volatile.Read(ref _mediaUri);

        /// <summary>Files opened through the document address although the media store's was tried.</summary>
        public int Fallback => Volatile.Read(ref _fallback);

        /// <summary>Counts one file opened through the media store's address.</summary>
        public void CountMediaUri() => Interlocked.Increment(ref _mediaUri);

        /// <summary>Counts one file that fell back to its document address.</summary>
        public void CountFallback() => Interlocked.Increment(ref _fallback);

        /// <summary>The log line's counts: <c>mediaUri=N fallback=N</c>.</summary>
        public override string ToString() => $"mediaUri={MediaUri} fallback={Fallback}";
    }
}
