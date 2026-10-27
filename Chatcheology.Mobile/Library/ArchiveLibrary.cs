using System.Security.Cryptography;
using Chatcheology.Archive;
using Chatcheology.Mobile.Settings;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.Library
{
    /// <summary>
    /// The archives installed in the app's own storage, and the staging area an import is copied into
    /// before it counts as installed.
    /// </summary>
    /// <remarks>
    /// <para>Layout under the app's private files folder: <c>staging/&lt;id&gt;/</c> for a copy being
    /// checked, <c>archives/&lt;id&gt;/</c> for an installed archive. Only folders in
    /// <c>archives/</c> count as installed, and nothing writes inside one after it has passed
    /// validation.</para>
    /// <para>A staging folder becomes installed by one move on the same volume, so an archive is
    /// either fully installed or not at all. Anything left in <c>staging/</c> by an import that was
    /// killed part-way is deleted by <see cref="CleanStaging"/>, which the app runs on every start.</para>
    /// </remarks>
    public sealed class ArchiveLibrary
    {
        /// <summary>The folder name, under the files folder, of the installed archives.</summary>
        public const string ArchivesFolderName = "archives";

        /// <summary>The folder name, under the files folder, of imports being checked.</summary>
        public const string StagingFolderName = "staging";

        /// <summary>A library rooted at the app's private files folder.</summary>
        public ArchiveLibrary(string filesFolder)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filesFolder);

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(filesFolder));

            ArchivesFolder = Path.Combine(root, ArchivesFolderName);
            StagingFolder = Path.Combine(root, StagingFolderName);
        }

        /// <summary>Where installed archives live.</summary>
        public string ArchivesFolder { get; }

        /// <summary>Where imports are copied and checked.</summary>
        public string StagingFolder { get; }

        /// <summary>Deletes everything in the staging area.</summary>
        public void CleanStaging()
        {
            if (Directory.Exists(StagingFolder))
            {
                Directory.Delete(StagingFolder, recursive: true);
            }
        }

        /// <summary>Creates an empty staging folder with a new identifier and returns its path.</summary>
        public string CreateStagingFolder()
        {
            var folder = Path.Combine(StagingFolder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            return folder;
        }

        /// <summary>The installed archives' folders, in ordinal order of their identifiers.</summary>
        public IReadOnlyList<string> ListInstalled() =>
            Directory.Exists(ArchivesFolder)
                ? Directory.EnumerateDirectories(ArchivesFolder).Order(StringComparer.Ordinal).ToList()
                : [];

        /// <summary>
        /// The installed archives with what the start screen shows: title, created date, message count
        /// and when each was last opened. Reads each archive's database; call it off the UI thread.
        /// </summary>
        /// <remarks>An archive that cannot be read is still listed, so it can be removed.</remarks>
        public IReadOnlyList<InstalledArchive> ListArchives(ArchiveSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            return ListInstalled().Select(folder => Describe(folder, settings)).ToList();
        }

        /// <summary>The installed archive whose <c>CONTENTS.sha256</c> hashes to <paramref name="contentsSha256"/>, or <see langword="null"/>.</summary>
        public string? FindInstalled(string contentsSha256)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contentsSha256);

            return ListInstalled().FirstOrDefault(folder =>
                string.Equals(ContentsSha256Of(folder), contentsSha256, StringComparison.Ordinal));
        }

        /// <summary>
        /// Deletes the app's copy of an installed archive and forgets what the app stored about it, except its
        /// stars (<see cref="ArchiveSettings.Forget"/>). The
        /// source the archive was imported from is not touched. The caller closes any reader on it first.
        /// </summary>
        /// <exception cref="ArgumentException">The folder is not directly inside the installed archives.</exception>
        public void Remove(InstalledArchive archive, ArchiveSettings settings)
        {
            ArgumentNullException.ThrowIfNull(archive);
            ArgumentNullException.ThrowIfNull(settings);

            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archive.Folder));

            if (!string.Equals(Path.GetDirectoryName(folder), ArchivesFolder, StringComparison.Ordinal))
            {
                throw new ArgumentException("Only a folder directly inside the installed archives can be removed.", nameof(archive));
            }

            // A reader's pooled handle would keep the database open on Windows; the reader does not pool,
            // and this makes sure nothing else does either.
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            if (archive.ContentsSha256 is { } hash)
            {
                settings.Forget(hash);
            }
        }

        /// <summary>
        /// The archive's identity: the SHA-256 (lower-case hex) of its <c>CONTENTS.sha256</c> file's bytes,
        /// or <see langword="null"/> when the file cannot be read.
        /// </summary>
        public static string? ContentsSha256Of(string archiveFolder)
        {
            try
            {
                return ArchiveHashing.Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(archiveFolder, ArchiveContents.FileName))));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static InstalledArchive Describe(string folder, ArchiveSettings settings)
        {
            var hash = ContentsSha256Of(folder);
            var lastOpened = hash is null ? null : settings.GetLastOpenedUtc(hash);

            try
            {
                using var reader = ArchiveReader.Open(folder);

                return new InstalledArchive(
                    Path.GetFileName(folder), folder, hash, reader.Info.ConversationTitle, reader.Info.CreatedUtc,
                    reader.ReadMessageCount(), lastOpened, IsReadable: hash is not null);
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException or IOException or UnauthorizedAccessException)
            {
                return new InstalledArchive(Path.GetFileName(folder), folder, hash, null, null, 0, lastOpened, IsReadable: false);
            }
        }

        /// <summary>
        /// Validates a staging folder. When it passes, it is moved into the installed archives under
        /// the same identifier; when it fails, it is deleted.
        /// </summary>
        /// <exception cref="ArgumentException">The folder is not directly inside the staging area.</exception>
        public InstallOutcome Install(string stagingFolder)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stagingFolder);

            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingFolder));

            if (!string.Equals(Path.GetDirectoryName(folder), StagingFolder, StringComparison.Ordinal)
                || !Directory.Exists(folder))
            {
                throw new ArgumentException("Only a folder directly inside the staging area can be installed.", nameof(stagingFolder));
            }

            var result = ArchiveValidator.Validate(folder);

            if (!result.Passed)
            {
                Directory.Delete(folder, recursive: true);

                return new InstallOutcome(result, null);
            }

            Directory.CreateDirectory(ArchivesFolder);

            var installed = Path.Combine(ArchivesFolder, Path.GetFileName(folder));
            Directory.Move(folder, installed);

            return new InstallOutcome(result, installed);
        }
    }

    /// <summary>What installing a staging folder did.</summary>
    /// <param name="Validation">The validator's result on the staging copy.</param>
    /// <param name="InstalledFolder">The installed archive's folder, or <see langword="null"/> when it failed.</param>
    public sealed record InstallOutcome(ArchiveValidationResult Validation, string? InstalledFolder)
    {
        /// <summary>Whether the archive is now installed.</summary>
        public bool Installed => InstalledFolder is not null;
    }
}
