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
            var result = Validate(stagingFolder);

            if (!result.Passed)
            {
                Directory.Delete(StagingPath(stagingFolder), recursive: true);

                return new InstallOutcome(result, null);
            }

            return new InstallOutcome(result, MoveIn(stagingFolder));
        }

        /// <summary>Validates a staging folder and leaves it where it is (CH-71 step 2: a refresh decides before the move).</summary>
        /// <exception cref="ArgumentException">The folder is not directly inside the staging area.</exception>
        public ArchiveValidationResult Validate(string stagingFolder) => ArchiveValidator.Validate(StagingPath(stagingFolder));

        /// <summary>Moves a validated staging folder into the installed archives, by one move, and returns its new folder.</summary>
        /// <exception cref="ArgumentException">The folder is not directly inside the staging area.</exception>
        public string MoveIn(string stagingFolder)
        {
            var folder = StagingPath(stagingFolder);

            Directory.CreateDirectory(ArchivesFolder);

            var installed = Path.Combine(ArchivesFolder, Path.GetFileName(folder));
            Directory.Move(folder, installed);

            return installed;
        }

        /// <summary>
        /// Takes an installed archive out after a refresh replaced it (CH-71 step 2): one move into the staging area,
        /// then deleted there, so a delete cut short is finished by <see cref="CleanStaging"/> and never leaves a
        /// half-deleted archive installed. Settings are the caller's (<see cref="ArchiveSettings.Purge"/>).
        /// </summary>
        /// <exception cref="ArgumentException">The folder is not directly inside the installed archives.</exception>
        public void Retire(string installedFolder)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installedFolder);

            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installedFolder));

            if (!string.Equals(Path.GetDirectoryName(folder), ArchivesFolder, StringComparison.Ordinal) || !Directory.Exists(folder))
            {
                throw new ArgumentException("Only a folder directly inside the installed archives can be retired.", nameof(installedFolder));
            }

            SqliteConnection.ClearAllPools();
            Directory.CreateDirectory(StagingFolder);

            var retired = Path.Combine(StagingFolder, "retired-" + Guid.NewGuid().ToString("N"));
            Directory.Move(folder, retired);

            try
            {
                Directory.Delete(retired, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Out of the installed archives already; CleanStaging finishes it on the next start.
            }
        }

        /// <summary>
        /// Each installed archive's identity for a refresh (CH-71 step 2): folder, contents hash, the format's archive id
        /// (version 4; <see langword="null"/> below), title and message count. Reads each database; off the UI thread.
        /// </summary>
        /// <remarks>An archive that cannot be read is listed with <see cref="InstalledIdentity.IsReadable"/> false.</remarks>
        public IReadOnlyList<InstalledIdentity> ReadIdentities() =>
            ListInstalled().Select(folder =>
            {
                var hash = ContentsSha256Of(folder);

                try
                {
                    using var reader = ArchiveReader.Open(folder);

                    return new InstalledIdentity(
                        folder, hash, reader.Info.ArchiveId, reader.Info.FormatVersion, reader.Info.ConversationTitle, reader.ReadMessageCount(),
                        IsReadable: hash is not null);
                }
                catch (Exception exception) when (exception is ArchiveFormatException or SqliteException or IOException or UnauthorizedAccessException)
                {
                    return new InstalledIdentity(folder, hash, null, 0, null, 0, IsReadable: false);
                }
            }).ToList();

        private string StagingPath(string stagingFolder)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stagingFolder);

            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingFolder));

            if (!string.Equals(Path.GetDirectoryName(folder), StagingFolder, StringComparison.Ordinal)
                || !Directory.Exists(folder))
            {
                throw new ArgumentException("Only a folder directly inside the staging area can be installed.", nameof(stagingFolder));
            }

            return folder;
        }
    }

    /// <summary>An installed archive's identity, for a refresh (CH-71 step 2). No message text.</summary>
    /// <param name="Folder">Its folder under <c>archives/</c>.</param>
    /// <param name="ContentsSha256">Its contents hash (the settings key), or <see langword="null"/> when unreadable.</param>
    /// <param name="ArchiveId">The format's fixed archive id (version 4), or <see langword="null"/> below version 4.</param>
    /// <param name="FormatVersion">Its format version; 0 when unreadable.</param>
    /// <param name="ConversationTitle">Its title, for the adoption question; <see langword="null"/> when unreadable.</param>
    /// <param name="MessageCount">How many messages it holds.</param>
    /// <param name="IsReadable">Whether its database opens and its contents hash is known.</param>
    public sealed record InstalledIdentity(
        string Folder, string? ContentsSha256, string? ArchiveId, int FormatVersion, string? ConversationTitle, int MessageCount, bool IsReadable)
    {
        /// <summary>The folder name under <c>archives/</c>.</summary>
        public string InstallId => Path.GetFileName(Folder);
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
