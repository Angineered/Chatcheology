using System.Diagnostics;
using Chatcheology.Mobile.OwnMemories;

namespace Chatcheology.Android
{
    /// <summary>What came of picking a photo for an own memory.</summary>
    /// <param name="FileName">The stored photo's name, when one was stored (or was there already).</param>
    /// <param name="Message">Why it was refused, in plain words, or none.</param>
    internal sealed record PickedPhoto(string? FileName, string? Message);

    /// <summary>
    /// Picks one picture with the system photo picker and streams it straight from its content address into
    /// <see cref="OwnPhotoIntake"/> (CH-54): no copy in the cache, no permission. The address and the picture's name are
    /// never logged; the log says the outcome only.
    /// </summary>
    internal static class OwnPhotoPicker
    {
        /// <summary>
        /// Opens the picker and stores the picked picture without its location. None when nothing was picked; a
        /// refused or unreadable picture gives its message.
        /// </summary>
        internal static async Task<PickedPhoto?> PickAsync(OwnMemoryStore store)
        {
            if (MainActivity.Current is not { } activity)
            {
                return null;
            }

            var uri = await activity.PickPhotoAsync();

            if (uri is null)
            {
                return null;
            }

            var watch = Stopwatch.StartNew();

            try
            {
                var resolver = activity.ContentResolver ?? throw new IOException();
                var result = await Task.Run(async () =>
                {
                    await using var input = resolver.OpenInputStream(uri) ?? throw new IOException();

                    return await new OwnPhotoIntake(store.PhotosFolder, new AndroidPhotoNormaliser()).AddAsync(input);
                });

                AppState.LogTiming("own-photo", watch.Elapsed, $"status={result.Status}");

                return new PickedPhoto(result.FileName, OwnPhotoIntake.MessageFor(result.Status));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Java.Lang.Exception)
            {
                AppState.LogTiming("own-photo", watch.Elapsed, "status=unreadable");

                return new PickedPhoto(null, "This photo couldn't be added: it could not be read.");
            }
        }
    }
}
