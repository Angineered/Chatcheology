using System.Security.Cryptography;
using System.Text.Json;
using Chatcheology.Archive;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.Import
{
    /// <summary>
    /// Imports an archive into the app's own storage (design "Import" 1-12): reads the source's contents
    /// list first, refuses a duplicate or a newer format before copying, checks the free space, copies
    /// only the listed files into staging, validates the copy and installs it.
    /// </summary>
    /// <remarks>
    /// <para><strong>The source is only read.</strong> Anything in it the contents list does not name
    /// (<c>desktop.ini</c>, <c>Thumbs.db</c>, <c>.nomedia</c>) is never opened.</para>
    /// <para><strong>Nothing is left behind.</strong> A refusal after copying began, a failed check and a
    /// cancel all delete the staging folder; an import killed part-way is cleaned up by
    /// <see cref="ArchiveLibrary.CleanStaging"/> on the next start.</para>
    /// <para><strong>Synchronous.</strong> The caller runs it off the UI thread. Cancellation applies to
    /// the copy only: the validator has neither progress nor cancel, so once checking starts it runs to
    /// the end.</para>
    /// </remarks>
    public sealed class ArchiveImporter
    {
        /// <summary>The largest contents list read; a real one is far smaller (about 100 bytes a file).</summary>
        public const int MaximumContentsBytes = 64 * 1024 * 1024;

        /// <summary>The smallest free-space margin kept beyond the archive's own size.</summary>
        public const long MinimumMarginBytes = 100L * 1024 * 1024;

        private const int MaximumManifestBytes = 1024 * 1024;

        private const int BufferSize = 1 << 20;

        private readonly ArchiveLibrary _library;
        private readonly IStorageSpace _space;
        private readonly ArchiveSettings? _settings;
        private readonly TimeProvider _clock;

        /// <summary>An importer into <paramref name="library"/>.</summary>
        /// <param name="library">Where archives are staged and installed.</param>
        /// <param name="space">The free space of the app's storage.</param>
        /// <param name="settings">Where the validation result and date are kept, or <see langword="null"/>.</param>
        /// <param name="clock">The clock for the validation date; the system's by default.</param>
        public ArchiveImporter(ArchiveLibrary library, IStorageSpace space, ArchiveSettings? settings = null, TimeProvider? clock = null)
        {
            ArgumentNullException.ThrowIfNull(library);
            ArgumentNullException.ThrowIfNull(space);

            _library = library;
            _space = space;
            _settings = settings;
            _clock = clock ?? TimeProvider.System;
        }

        /// <summary>The margin kept beyond an archive of <paramref name="archiveBytes"/>: 5%, at least 100 MB.</summary>
        public static long MarginFor(long archiveBytes) => Math.Max(MinimumMarginBytes, archiveBytes / 20);

        /// <summary>Imports the archive in <paramref name="source"/>.</summary>
        public ImportResult Import(IImportSource source, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);

            IReadOnlyList<ImportSourceFile> files;

            try
            {
                files = source.ListFiles(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new ImportResult(ImportOutcome.Cancelled);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new ImportResult(ImportOutcome.Refused, ImportRefusal.SourceUnreadable);
            }

            var lengths = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var file in files)
            {
                lengths.TryAdd(file.RelativePath, file.Length);
            }

            // 2. The contents list first: the files to copy, their size, and the archive's identity.
            if (!lengths.TryGetValue(ArchiveContents.FileName, out var contentsLength) || contentsLength > MaximumContentsBytes)
            {
                return new ImportResult(ImportOutcome.Refused, contentsLength > MaximumContentsBytes
                    ? ImportRefusal.ContentsListUnusable
                    : ImportRefusal.NoContentsList);
            }

            byte[] contents;

            try
            {
                contents = ReadSmall(source, ArchiveContents.FileName, MaximumContentsBytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new ImportResult(ImportOutcome.Refused, ImportRefusal.NoContentsList);
            }

            var hash = ArchiveHashing.Hex(SHA256.HashData(contents));

            IReadOnlyList<(string Sha256, string Path)> listed;

            try
            {
                listed = ArchiveContents.Parse(contents);
            }
            catch (FormatException)
            {
                return new ImportResult(ImportOutcome.Refused, ImportRefusal.ContentsListUnusable) { ContentsSha256 = hash };
            }

            // Belt and braces after the parser's own checks: nothing listed may land outside the copy, and
            // nothing may overwrite the contents list itself.
            var probe = Path.Combine(Path.GetTempPath(), "probe");

            if (listed.Any(item => item.Path == ArchiveContents.FileName
                                   || !ArchivePaths.IsWithin(ArchiveContents.FullPath(probe, item.Path), probe)
                                   || ArchivePaths.IsWithin(probe, ArchiveContents.FullPath(probe, item.Path))))
            {
                return new ImportResult(ImportOutcome.Refused, ImportRefusal.ContentsListUnusable) { ContentsSha256 = hash };
            }

            // 3. Already installed: refused before anything is copied.
            if (_library.FindInstalled(hash) is { } installed)
            {
                return new ImportResult(ImportOutcome.AlreadyInstalled) { ContentsSha256 = hash, ArchiveId = Path.GetFileName(installed) };
            }

            var missing = listed.Count(item => !lengths.ContainsKey(item.Path));

            if (missing > 0)
            {
                return new ImportResult(ImportOutcome.Refused, ImportRefusal.ListedFilesMissing) { ContentsSha256 = hash, MissingFiles = missing };
            }

            // 12. A newer format, from the manifest, before copying.
            if (lengths.ContainsKey(ArchiveFormat.ManifestFileName) && IsNewerManifest(source))
            {
                return new ImportResult(ImportOutcome.Refused, ImportRefusal.NewerFormat) { ContentsSha256 = hash };
            }

            // 4. Free space.
            var total = listed.Sum(item => lengths[item.Path]) + contents.Length;
            var required = total + MarginFor(total);
            var available = _space.AvailableBytes();

            if (available < required)
            {
                return new ImportResult(ImportOutcome.NotEnoughSpace) { ContentsSha256 = hash, RequiredBytes = required, AvailableBytes = available };
            }

            // 5. The copy. Whatever ends the import from here on, staging is deleted unless the copy was
            // installed: an exception this method does not expect (a source adapter's own exception type,
            // say) still propagates, but leaves nothing behind.
            var staging = _library.CreateStagingFolder();
            var installedCopy = false;

            try
            {
                var result = CopyCheckAndInstall(source, listed, lengths, total, contents, staging, hash, required, available, progress, cancellationToken);
                installedCopy = result.Outcome == ImportOutcome.Installed;

                return result;
            }
            finally
            {
                if (!installedCopy)
                {
                    DeleteQuietly(staging);
                }
            }
        }

        private ImportResult CopyCheckAndInstall(
            IImportSource source,
            IReadOnlyList<(string Sha256, string Path)> listed,
            IReadOnlyDictionary<string, long> lengths,
            long total,
            byte[] contents,
            string staging,
            string hash,
            long required,
            long available,
            IProgress<ImportProgress>? progress,
            CancellationToken cancellationToken)
        {
            ImportResult Refuse(ImportRefusal refusal) =>
                new(ImportOutcome.Refused, refusal) { ContentsSha256 = hash, RequiredBytes = required, AvailableBytes = available };

            try
            {
                Copy(source, listed.Select(item => item.Path).ToList(), lengths, total, contents, staging, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new ImportResult(ImportOutcome.Cancelled) { ContentsSha256 = hash };
            }
            catch (DestinationException)
            {
                return Refuse(ImportRefusal.CopyFailed);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Refuse(ImportRefusal.SourceUnreadable);
            }

            // 12, again on the copy: the database's own version is what the reader refuses on.
            if (IsNewerDatabase(staging))
            {
                return Refuse(ImportRefusal.NewerFormat);
            }

            // 6-7. Checked, then moved into the installed archives, or deleted.
            progress?.Report(new ImportProgress(ImportStage.Checking, total, total, listed.Count + 1, listed.Count + 1));

            InstallOutcome outcome;

            try
            {
                outcome = _library.Install(staging);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Refuse(ImportRefusal.CopyFailed);
            }

            var checks = outcome.Validation.Checks;

            if (!outcome.Installed)
            {
                return new ImportResult(ImportOutcome.Refused, ImportRefusal.FailedValidation)
                {
                    ContentsSha256 = hash,
                    RequiredBytes = required,
                    AvailableBytes = available,
                    CheckCount = checks.Count,
                    FailedChecks = checks
                        .Where(check => !check.Passed)
                        .Select(check => new ImportCheck(check.Check, ImportCheck.NameOf(check.Check), check.Ran, check.Examined, check.Problems))
                        .ToList(),
                };
            }

            _settings?.SetValidation(hash, _clock.GetUtcNow().UtcDateTime, checks.Count(check => check.Passed), checks.Count);

            return new ImportResult(ImportOutcome.Installed)
            {
                ContentsSha256 = hash,
                ArchiveId = Path.GetFileName(outcome.InstalledFolder),
                RequiredBytes = required,
                AvailableBytes = available,
                CheckCount = checks.Count,
            };
        }

        private static void Copy(
            IImportSource source,
            IReadOnlyList<string> paths,
            IReadOnlyDictionary<string, long> lengths,
            long total,
            byte[] contents,
            string staging,
            IProgress<ImportProgress>? progress,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[BufferSize];
            var copied = 0L;
            var files = paths.Count + 1;

            progress?.Report(new ImportProgress(ImportStage.Copying, 0, total, 0, files));

            for (var index = 0; index < paths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destination = ArchiveContents.FullPath(staging, paths[index]);
                Destination(() => Directory.CreateDirectory(Path.GetDirectoryName(destination)!));

                using var input = source.OpenRead(paths[index]);
                using var output = Destination(() => new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize));

                int read;

                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Destination(() => output.Write(buffer, 0, read));
                    copied += read;

                    progress?.Report(new ImportProgress(ImportStage.Copying, copied, total, index, files));
                }

                Destination(() => output.Flush(flushToDisk: true));
            }

            // The contents list as it was read and hashed, so the identity is exactly what is checked.
            Destination(() => File.WriteAllBytes(Path.Combine(staging, ArchiveContents.FileName), contents));
            copied += contents.Length;

            progress?.Report(new ImportProgress(ImportStage.Copying, copied, total, files, files));
        }

        private static byte[] ReadSmall(IImportSource source, string relativePath, int maximum)
        {
            using var input = source.OpenRead(relativePath);
            using var memory = new MemoryStream();

            var buffer = new byte[81920];
            int read;

            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (memory.Length + read > maximum)
                {
                    throw new InvalidDataException("The file is larger than expected.");
                }

                memory.Write(buffer, 0, read);
            }

            return memory.ToArray();
        }

        /// <summary>
        /// Whether the source's manifest records a newer format version. Read loosely (only the version
        /// number), because a newer format's manifest may not parse as this one's.
        /// </summary>
        private static bool IsNewerManifest(IImportSource source)
        {
            try
            {
                using var document = JsonDocument.Parse(ReadSmall(source, ArchiveFormat.ManifestFileName, MaximumManifestBytes));

                return document.RootElement.ValueKind == JsonValueKind.Object
                       && document.RootElement.TryGetProperty("formatVersion", out var version)
                       && version.ValueKind == JsonValueKind.Number
                       && (!version.TryGetInt32(out var number) || number > ArchiveFormat.FormatVersion);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // Not readable here: the validator reports the manifest on the copy.
                return false;
            }
        }

        /// <summary>Whether the copied database records a newer format version.</summary>
        private static bool IsNewerDatabase(string staging)
        {
            var path = Path.Combine(staging, ArchiveFormat.DatabaseFileName);

            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());

                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";

                return command.ExecuteScalar() is long version && version > ArchiveFormat.FormatVersion;
            }
            catch (SqliteException)
            {
                // Not a database this can read: the validator reports it.
                return false;
            }
        }

        private static void DeleteQuietly(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    SqliteConnection.ClearAllPools();
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for CleanStaging on the next start.
            }
        }

        private static void Destination(Action write) => Destination(() =>
        {
            write();
            return 0;
        });

        private static T Destination<T>(Func<T> write)
        {
            try
            {
                return write();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new DestinationException(exception);
            }
        }

        /// <summary>Writing into staging failed: kept apart from a failure to read the source.</summary>
        private sealed class DestinationException(Exception inner) : Exception("A file could not be written into staging.", inner);
    }
}
