using Android.Media;
using Android.OS;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Android
{
    /// <summary>What happened when a memory photo was offered to the share sheet.</summary>
    internal enum PhotoShareOutcome
    {
        /// <summary>The share sheet was opened with the copy.</summary>
        Shared,

        /// <summary>No app on the phone receives it.</summary>
        NoApp,

        /// <summary>Not shared: its location cannot be removed (<see cref="PhotoShare.CannotShareText"/>).</summary>
        CannotRemoveLocation,

        /// <summary>Not shared: the file is not one the app can give out, or could not be copied.</summary>
        NotAllowed,
    }

    /// <summary>
    /// CH-59 (D-34, option A): shares a memory photo as a temporary copy with its location removed. The copy is
    /// written to the app's cache under <see cref="PhotoShare.CopiesFolderName"/>, its GPS tags are cleared with
    /// Android's <see cref="ExifInterface"/> (on the copy only), it is checked again, and only then handed out
    /// through <see cref="ArchiveFileProvider"/>. The archive file is opened for reading only.
    /// </summary>
    /// <remarks>
    /// Copies older than 24 hours are deleted when the app starts and before each share; a copy that failed or was
    /// refused is deleted at once. Nothing is logged.
    /// </remarks>
    internal static class PhotoShareCopies
    {
        /// <summary>The cache folder that holds shared copies.</summary>
        internal static string Folder => System.IO.Path.Combine(FileSystem.CacheDirectory, PhotoShare.CopiesFolderName);

        /// <summary>
        /// Deletes copies older than <see cref="PhotoShare.CopyLifetime"/>, so an app that reads a shared copy a
        /// little later still finds it. Never throws.
        /// </summary>
        internal static void Clean()
        {
            try
            {
                if (!Directory.Exists(Folder))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(Folder))
                {
                    try
                    {
                        if (!PhotoShare.IsExpired(File.GetLastWriteTimeUtc(file), DateTime.UtcNow))
                        {
                            continue;
                        }

                        File.Delete(file);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Tried again at the next share or start.
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // As above.
            }
        }

        /// <summary>
        /// Makes the copy without the location and offers it to the share sheet. Photo only: never a caption or
        /// title. <paramref name="sourcePath"/> must be inside <paramref name="archivesFolder"/>.
        /// </summary>
        internal static async Task<PhotoShareOutcome> ShareAsync(string sourcePath, string archivesFolder)
        {
            if (!PhotoShare.IsInside(sourcePath, archivesFolder))
            {
                return PhotoShareOutcome.NotAllowed;
            }

            var made = await Task.Run(() => MakeCopy(sourcePath));

            if (made.Outcome != PhotoShareOutcome.Shared || made.CopyPath is null)
            {
                return made.Outcome;
            }

            return ExternalOpener.ShareCopy(made.CopyPath, MediaMimeTypes.FromFileName(sourcePath), Folder) switch
            {
                OpenResult.Opened => PhotoShareOutcome.Shared,
                OpenResult.NoApp => PhotoShareOutcome.NoApp,
                _ => PhotoShareOutcome.NotAllowed,
            };
        }

        private static (PhotoShareOutcome Outcome, string? CopyPath) MakeCopy(string sourcePath)
        {
            Clean();

            string? copyPath = null;
            var outcome = PhotoShareOutcome.NotAllowed;

            try
            {
                outcome = Make(sourcePath, ref copyPath);
            }
            catch (Exception)
            {
                // Any failure, of any kind: nothing is shared.
                outcome = PhotoShareOutcome.NotAllowed;
            }
            finally
            {
                // A copy is kept only when it passed every check; a half-made or refused one may still hold a location.
                if (outcome != PhotoShareOutcome.Shared)
                {
                    Delete(copyPath);
                }
            }

            return outcome == PhotoShareOutcome.Shared ? (outcome, copyPath) : (outcome, null);
        }

        private static PhotoShareOutcome Make(string sourcePath, ref string? copyPath)
        {
            Directory.CreateDirectory(Folder);

            // The archive file: read only, and others may read it too.
            byte[] original;

            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                original = new byte[source.Length];
                source.ReadExactly(original);
            }

            var format = PhotoShare.Sniff(original.AsSpan(0, Math.Min(original.Length, PhotoShare.HeaderLength)));
            var method = PhotoShare.MethodFor(format, (int)Build.VERSION.SdkInt);

            if (method == PhotoShareMethod.Refuse)
            {
                return PhotoShareOutcome.CannotRemoveLocation;
            }

            copyPath = System.IO.Path.Combine(Folder, PhotoShare.CopyFileName(sourcePath, Guid.NewGuid().ToString("N")[..12]));

            using (var target = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (format == PhotoFormat.Jpeg)
                {
                    // The main image only: no XMP, MPF, IPTC, second image, gain map or motion-photo video.
                    if (!PhotoShare.WriteMainJpeg(original, target))
                    {
                        return PhotoShareOutcome.CannotRemoveLocation;
                    }
                }
                else
                {
                    target.Write(original);
                }
            }

            if (method == PhotoShareMethod.RemoveLocation && !RemoveLocation(copyPath))
            {
                return PhotoShareOutcome.CannotRemoveLocation;
            }

            using (var check = new FileStream(copyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (PhotoShare.MayStillCarryLocation(check, format, method))
                {
                    return PhotoShareOutcome.CannotRemoveLocation;
                }
            }

            return PhotoShareOutcome.Shared;
        }

        /// <summary>Clears every GPS tag on the copy and saves it, then reads it back: true only when none is left.</summary>
        private static bool RemoveLocation(string copyPath)
        {
            try
            {
                using var exif = new ExifInterface(copyPath);

                foreach (var tag in PhotoShare.LocationTags)
                {
                    exif.SetAttribute(tag, null);
                }

                exif.SaveAttributes();

                using var reread = new ExifInterface(copyPath);

                foreach (var tag in PhotoShare.LocationTags)
                {
                    if (reread.GetAttribute(tag) is not null)
                    {
                        return false;
                    }
                }

                return !reread.GetLatLong(new float[2]);
            }
            catch (Exception exception) when (exception is IOException or Java.Lang.Exception)
            {
                // The format cannot be written here (a HEIC named .jpg, say): refused, never shared as it is.
                return false;
            }
        }

        private static void Delete(string? path)
        {
            try
            {
                if (path is not null)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Cleaned at the next share or start.
            }
        }
    }
}
