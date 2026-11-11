using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Import;

namespace Chatcheology.Mobile.Library
{
    /// <summary>
    /// The gallery's saved thumbnails (CH-66 saved thumbnails): one small cover-cropped JPEG per photo and cell size,
    /// made the first time the photo is shown and read instead of the photo afterwards, also after a restart.
    /// </summary>
    /// <remarks>
    /// <para>Layout under the app's private files folder: <c>thumbnails/&lt;contents hash&gt;/&lt;attachment id&gt;-&lt;media
    /// sha256&gt;-&lt;width&gt;x&lt;height&gt;.jpg</c>, beside the installed archives and never inside one (nothing writes in
    /// <c>archives/</c>). The contents hash is the installed archive's identity (<see cref="ArchiveLibrary.ContentsSha256Of"/>).</para>
    /// <para>Only a cache: a missing, unreadable or unwritable thumbnail means the photo is decoded as before. Nothing here
    /// throws to its caller. Removing an archive removes its folder (<see cref="RemoveArchive"/>); a refresh carries the
    /// thumbnails whose attachment is unchanged to the new hash and drops the rest (<see cref="Carry"/>); folders of
    /// archives no longer installed and unfinished writes are deleted at start (<see cref="RemoveOrphans"/>).</para>
    /// </remarks>
    public sealed class ThumbnailStore
    {
        /// <summary>The folder name, under the files folder, of the saved thumbnails.</summary>
        public const string FolderName = "thumbnails";

        /// <summary>Free space below which no thumbnail is saved, so the cache never fills the phone.</summary>
        public const long MinimumFreeBytes = 64L * 1024 * 1024;

        private const string Extension = ".jpg";
        private const string TemporaryPrefix = ".tmp-";

        private readonly IStorageSpace? _space;

        /// <summary>A store rooted at the app's private files folder; <paramref name="space"/> reports the free space, if known.</summary>
        public ThumbnailStore(string filesFolder, IStorageSpace? space = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filesFolder);

            Root = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(filesFolder)), FolderName);
            _space = space;
        }

        /// <summary>Where the thumbnails live.</summary>
        public string Root { get; }

        /// <summary>The saved thumbnail's file, or <see langword="null"/> when there is none (or the key is not valid).</summary>
        public string? Find(ThumbnailKey key)
        {
            if (PathOf(key) is not { } path)
            {
                return null;
            }

            try
            {
                var file = new FileInfo(path);

                return file.Exists && file.Length > 0 ? path : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Saves a thumbnail: <paramref name="write"/> writes the JPEG bytes to a temporary file, which then replaces the
        /// saved one by one move, so a half-written file is never found. Whether it was saved; <see langword="false"/> for an
        /// invalid key, too little free space (<see cref="MinimumFreeBytes"/>), a write that failed or wrote nothing. Never
        /// throws; nothing is left behind on failure.
        /// </summary>
        public bool Save(ThumbnailKey key, Func<Stream, bool> write)
        {
            ArgumentNullException.ThrowIfNull(write);

            if (PathOf(key) is not { } path)
            {
                return false;
            }

            string? temporary = null;

            try
            {
                if (_space is not null && _space.AvailableBytes() < MinimumFreeBytes)
                {
                    return false;
                }

                var folder = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(folder);
                temporary = Path.Combine(folder, TemporaryPrefix + Guid.NewGuid().ToString("N"));

                bool written;

                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    written = write(stream) && stream.Length > 0;
                }

                if (!written)
                {
                    Delete(temporary);
                    return false;
                }

                File.Move(temporary, path, overwrite: true);

                return true;
            }
            catch (Exception)
            {
                // Full storage, a failed write or encoder: the photo is decoded as before.
                if (temporary is not null)
                {
                    Delete(temporary);
                }

                return false;
            }
        }

        /// <summary>Deletes a saved thumbnail (one that would not decode). Never throws.</summary>
        public void Remove(ThumbnailKey key)
        {
            if (PathOf(key) is { } path)
            {
                Delete(path);
            }
        }

        /// <summary>Deletes every thumbnail of the archive with <paramref name="contentsSha256"/> (the archive was removed). Never throws.</summary>
        public void RemoveArchive(string? contentsSha256)
        {
            if (ArchiveFormat.IsSha256(contentsSha256))
            {
                DeleteFolder(Path.Combine(Root, contentsSha256!));
            }
        }

        /// <summary>
        /// A refresh replaced the archive <paramref name="fromSha256"/> with <paramref name="toSha256"/>: thumbnails whose
        /// attachment is unchanged (<paramref name="keep"/>: same attachment id with the same media) move to the new archive;
        /// the rest, and the old folder, are deleted. Never throws; whatever is not carried is simply made again.
        /// </summary>
        public void Carry(string? fromSha256, string? toSha256, Func<long, string, bool> keep)
        {
            ArgumentNullException.ThrowIfNull(keep);

            if (!ArchiveFormat.IsSha256(fromSha256) || string.Equals(fromSha256, toSha256, StringComparison.Ordinal))
            {
                return;
            }

            var from = Path.Combine(Root, fromSha256!);

            try
            {
                if (ArchiveFormat.IsSha256(toSha256) && Directory.Exists(from))
                {
                    var to = Path.Combine(Root, toSha256!);
                    Directory.CreateDirectory(to);

                    foreach (var file in Directory.EnumerateFiles(from))
                    {
                        var name = Path.GetFileName(file);

                        if (TryParse(name, out var attachmentID, out var mediaSha256, out _, out _) && keep(attachmentID, mediaSha256))
                        {
                            File.Move(file, Path.Combine(to, name), overwrite: true);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Not carried: made again when shown.
            }

            DeleteFolder(from);
        }

        /// <summary>
        /// Deletes the folders of archives not in <paramref name="installedSha256"/> (removed or replaced while a delete
        /// was cut short) and any unfinished write. Never throws.
        /// </summary>
        public void RemoveOrphans(IEnumerable<string?> installedSha256)
        {
            ArgumentNullException.ThrowIfNull(installedSha256);

            var installed = installedSha256.OfType<string>().ToHashSet(StringComparer.Ordinal);

            try
            {
                if (!Directory.Exists(Root))
                {
                    return;
                }

                foreach (var folder in Directory.EnumerateDirectories(Root))
                {
                    if (!installed.Contains(Path.GetFileName(folder)))
                    {
                        DeleteFolder(folder);
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(folder, TemporaryPrefix + "*"))
                    {
                        Delete(file);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(Root))
                {
                    Delete(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Tried again at the next start.
            }
        }

        /// <summary>
        /// The attachment ids and media of an installed archive's attachments, for <see cref="Carry"/>. Reads the whole
        /// archive; call it off the UI thread.
        /// </summary>
        public static HashSet<(long AttachmentID, string MediaSha256)> AttachmentsOf(ArchiveReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            var attachments = new HashSet<(long, string)>();

            foreach (var page in reader.ReadAllPages())
            {
                foreach (var message in page.Messages)
                {
                    foreach (var attachment in message.Attachments)
                    {
                        if (attachment.MediaSha256 is { } sha)
                        {
                            attachments.Add((attachment.AttachmentID, sha));
                        }
                    }
                }
            }

            return attachments;
        }

        /// <summary>The file name of a thumbnail (without folder).</summary>
        internal static string FileNameOf(ThumbnailKey key) =>
            string.Create(CultureInfo.InvariantCulture, $"{key.AttachmentID}-{key.MediaSha256}-{key.Width}x{key.Height}{Extension}");

        private string? PathOf(ThumbnailKey key) =>
            key.IsValid ? Path.Combine(Root, key.ContentsSha256, FileNameOf(key)) : null;

        private static bool TryParse(string name, out long attachmentID, out string mediaSha256, out int width, out int height)
        {
            attachmentID = 0;
            mediaSha256 = string.Empty;
            width = height = 0;

            if (!name.EndsWith(Extension, StringComparison.Ordinal))
            {
                return false;
            }

            var parts = name[..^Extension.Length].Split('-');

            if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out attachmentID)
                || !ArchiveFormat.IsSha256(parts[1]))
            {
                return false;
            }

            var size = parts[2].Split('x');

            if (size.Length != 2
                || !int.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
                || !int.TryParse(size[1], NumberStyles.None, CultureInfo.InvariantCulture, out height))
            {
                return false;
            }

            mediaSha256 = parts[1];

            return true;
        }

        private static void Delete(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for RemoveOrphans.
            }
        }

        private static void DeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for RemoveOrphans at the next start.
            }
        }
    }

    /// <summary>One saved thumbnail: the archive (contents hash), the photo's attachment and media, and the size in pixels.</summary>
    /// <param name="ContentsSha256">The installed archive's contents hash.</param>
    /// <param name="AttachmentID">The photo's attachment.</param>
    /// <param name="MediaSha256">The photo's media file hash.</param>
    /// <param name="Width">The cell's width in pixels.</param>
    /// <param name="Height">The cell's height in pixels.</param>
    public readonly record struct ThumbnailKey(string ContentsSha256, long AttachmentID, string MediaSha256, int Width, int Height)
    {
        /// <summary>Whether every part can name a file: both hashes are SHA-256 hex, the id and size positive.</summary>
        public bool IsValid =>
            ArchiveFormat.IsSha256(ContentsSha256) && ArchiveFormat.IsSha256(MediaSha256) && AttachmentID > 0 && Width > 0 && Height > 0;
    }
}
