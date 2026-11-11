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
    /// <para><strong>Refresh (CH-71 step 2).</strong> A version 4 archive whose archive id an installed archive has
    /// replaces it only when its messages up to the installed one's last sequence number give the same
    /// <see cref="MessageDigest"/>; otherwise it is refused and nothing changes. The replacement keeps every setting
    /// (<see cref="ArchiveSettings.CopyArchive"/>) except validation, which is the new copy's own. A version 4 archive
    /// whose earlier messages are exactly an archive installed before version 4 replaces it only when the owner answers
    /// Replace to <see cref="IImportQuestions.AskAdoption"/>.</para>
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
        /// <param name="source">The picked folder, read only.</param>
        /// <param name="progress">Progress for the screen, or none.</param>
        /// <param name="cancellationToken">Cancels the copy.</param>
        /// <param name="questions">
        /// Asks the phone owner whether a version 4 archive replaces an earlier copy installed before version 4 (CH-71
        /// step 2 adoption). <see langword="null"/>: never asked, so never adopted; the archive is imported as new.
        /// </param>
        public ImportResult Import(
            IImportSource source, IProgress<ImportProgress>? progress, CancellationToken cancellationToken, IImportQuestions? questions = null)
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
                return new ImportResult(ImportOutcome.AlreadyInstalled) { ContentsSha256 = hash, InstallId = Path.GetFileName(installed) };
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
                var result = CopyCheckAndInstall(source, listed, lengths, total, contents, staging, hash, required, available, progress, cancellationToken, questions);
                installedCopy = result.Outcome is ImportOutcome.Installed or ImportOutcome.Refreshed;

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
            CancellationToken cancellationToken,
            IImportQuestions? questions)
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

            ArchiveValidationResult validation;

            try
            {
                validation = _library.Validate(staging);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Refuse(ImportRefusal.CopyFailed);
            }

            var checks = validation.Checks;

            if (!validation.Passed)
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

            // CH-71 step 2: a version 4 archive whose id an installed archive has is a refresh of it; one whose earlier
            // messages are exactly an archive installed before version 4 may replace it, if the owner says so.
            var plan = new Placement(hash, required, available, checks.Count, checks.Count(check => check.Passed));

            try
            {
                return Place(staging, plan, questions, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new ImportResult(ImportOutcome.Cancelled) { ContentsSha256 = hash };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Refuse(ImportRefusal.CopyFailed);
            }
        }

        /// <summary>What a validated copy carries into installing it.</summary>
        private sealed record Placement(string Hash, long Required, long Available, int Checks, int Passed);

        /// <summary>
        /// Decides, for a validated copy in staging, between a new install, a refresh of an installed archive with the
        /// same archive id, and (asked) adopting an archive installed before version 4; then installs it.
        /// </summary>
        private ImportResult Place(string staging, Placement plan, IImportQuestions? questions, CancellationToken cancellationToken)
        {
            ImportResult Refuse(ImportRefusal refusal) =>
                new(ImportOutcome.Refused, refusal) { ContentsSha256 = plan.Hash, RequiredBytes = plan.Required, AvailableBytes = plan.Available };

            string? archiveId;

            using (var reader = ArchiveReader.Open(staging))
            {
                archiveId = reader.Info.ArchiveId;
            }

            // Below version 4 there is no archive id: always a new install, as before.
            if (archiveId is null)
            {
                return InstallNew(staging, plan, digest: null);
            }

            var installed = _library.ReadIdentities();
            var sameId = installed.Where(identity => string.Equals(identity.ArchiveId, archiveId, StringComparison.Ordinal)).ToList();

            if (sameId.Count > 1)
            {
                return Refuse(ImportRefusal.RefreshAmbiguous);
            }

            if (sameId.Count == 1)
            {
                var target = sameId[0];

                if (!target.IsReadable || InstalledDigest(target, useStored: true, cancellationToken) is not { } earlier)
                {
                    return Refuse(ImportRefusal.RefreshInstalledUnreadable);
                }

                var (whole, through) = DigestOfCopy(staging, earlier.LastSequenceNumber, cancellationToken);

                return MessageDigest.Continues(earlier, through)
                    ? Replace(staging, plan, target, earlier, whole, adopted: false)
                    : Refuse(ImportRefusal.RefreshMismatch);
            }

            // Adoption: archives installed before version 4 (no archive id) whose messages are exactly an earlier part of
            // this one. Never without the question; the largest first, and asked once: "Import as new" ends the asking.
            // Archives before version 4 that were compared and did not match: said in the outcome, so a missed adoption is
            // never silent (their stars stay with them). Not counted without questions: nothing is compared then.
            MessageDigest? newWhole = null;
            var unmatched = 0;

            if (questions is not null)
            {
                var candidates = installed
                    .Where(identity => identity.IsReadable && identity.ArchiveId is null)
                    .OrderByDescending(identity => identity.MessageCount)
                    .ThenBy(identity => identity.InstallId, StringComparer.Ordinal);

                foreach (var candidate in candidates)
                {
                    if (InstalledDigest(candidate, useStored: false, cancellationToken) is not { } earlier)
                    {
                        unmatched++;
                        continue;
                    }

                    var (whole, through) = DigestOfCopy(staging, earlier.LastSequenceNumber, cancellationToken);
                    newWhole = whole;

                    if (!MessageDigest.Continues(earlier, through))
                    {
                        unmatched++;
                        continue;
                    }

                    var question = new AdoptionQuestion(candidate.ConversationTitle, earlier.Count, whole.Count);

                    switch (questions.AskAdoption(question, cancellationToken))
                    {
                        case AdoptionAnswer.Replace:
                            return Replace(staging, plan, candidate, earlier, whole, adopted: true);
                        case AdoptionAnswer.ImportAsNew:
                            return InstallNew(staging, plan, whole);
                        default:
                            return new ImportResult(ImportOutcome.Cancelled) { ContentsSha256 = plan.Hash };
                    }
                }
            }

            return InstallNew(staging, plan, newWhole ?? DigestOfCopy(staging, null, cancellationToken).Whole, unmatched);
        }

        /// <summary>
        /// The installed archive's digest: the one stored at its import when <paramref name="useStored"/> (a version 4
        /// install by this app), otherwise worked out from its files. <see langword="null"/> when it cannot be read.
        /// </summary>
        private MessageDigest? InstalledDigest(InstalledIdentity identity, bool useStored, CancellationToken cancellationToken)
        {
            if (useStored && identity.ContentsSha256 is { } hash && _settings?.GetMessageDigest(hash) is { } stored)
            {
                return stored;
            }

            try
            {
                using var reader = ArchiveReader.Open(identity.Folder);

                return MessageDigest.Of(reader, cancellationToken);
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static (MessageDigest Whole, MessageDigest? Through) DigestOfCopy(string staging, int? through, CancellationToken cancellationToken)
        {
            using var reader = ArchiveReader.Open(staging);

            return MessageDigest.Compute(reader, through, cancellationToken);
        }

        private ImportResult InstallNew(string staging, Placement plan, MessageDigest? digest, int unmatchedOlder = 0)
        {
            var folder = _library.MoveIn(staging);

            _settings?.SetValidation(plan.Hash, _clock.GetUtcNow().UtcDateTime, plan.Passed, plan.Checks);
            _settings?.SetMessageDigest(plan.Hash, digest);

            return new ImportResult(ImportOutcome.Installed)
            {
                ContentsSha256 = plan.Hash,
                InstallId = Path.GetFileName(folder),
                UnmatchedOlderArchives = unmatchedOlder,
                RequiredBytes = plan.Required,
                AvailableBytes = plan.Available,
                CheckCount = plan.Checks,
            };
        }

        /// <summary>
        /// Replaces an installed archive with the validated copy, keeping all its settings (CH-71 step 2). Order: the
        /// settings are carried to the new hash and flushed to disk first (a failed flush refuses, before anything moves),
        /// then the copy is moved in, then the old archive is moved out and its keys removed. Cut short anywhere, nothing
        /// is lost: at worst both copies stay installed, each with the settings, until one is removed.
        /// </summary>
        private ImportResult Replace(
            string staging, Placement plan, InstalledIdentity target, MessageDigest earlier, MessageDigest whole, bool adopted)
        {
            if (_settings is not null)
            {
                if (target.ContentsSha256 is { } oldHash)
                {
                    _settings.CopyArchive(oldHash, plan.Hash);
                }

                // The new files are validated: their own result, never the old copy's.
                _settings.SetValidation(plan.Hash, _clock.GetUtcNow().UtcDateTime, plan.Passed, plan.Checks);
                _settings.SetMessageDigest(plan.Hash, whole);

                // On disk before any folder moves: otherwise a kill after the swap could lose the carried stars.
                if (!_settings.Flush())
                {
                    return new ImportResult(ImportOutcome.Refused, ImportRefusal.CopyFailed)
                    {
                        ContentsSha256 = plan.Hash, RequiredBytes = plan.Required, AvailableBytes = plan.Available,
                    };
                }
            }

            var folder = _library.MoveIn(staging);
            var retired = false;

            // From here the new copy is installed with the settings: nothing below may turn the outcome into a refusal.
            try
            {
                _library.Retire(target.Folder);
                retired = true;
            }
            catch (Exception)
            {
                // Any failure: the old copy stays installed with its settings; it can be removed from the start screen.
            }

            if (retired)
            {
                CarryThumbnails(target.ContentsSha256, plan.Hash, folder);
            }

            if (retired && target.ContentsSha256 is { } purged && _settings is not null)
            {
                try
                {
                    _settings.Purge(purged);
                    _settings.Flush();
                }
                catch (Exception)
                {
                    // Keys of an archive no longer installed: harmless leftovers.
                }
            }

            return new ImportResult(ImportOutcome.Refreshed)
            {
                ContentsSha256 = plan.Hash,
                InstallId = Path.GetFileName(folder),
                ReplacedInstallId = target.InstallId,
                ReplacedRemoved = retired,
                AddedMessages = whole.Count - earlier.Count,
                Adopted = adopted,
                RequiredBytes = plan.Required,
                AvailableBytes = plan.Available,
                CheckCount = plan.Checks,
            };
        }

        /// <summary>
        /// The replaced archive's saved thumbnails (CH-66): those whose attachment is unchanged in the new copy (same id,
        /// same media) move to the new archive, the rest are deleted. Never fails the refresh; if the new copy cannot be
        /// read here, the old thumbnails are deleted and made again when shown.
        /// </summary>
        private void CarryThumbnails(string? oldHash, string newHash, string newFolder)
        {
            try
            {
                HashSet<(long AttachmentID, string MediaSha256)> attachments;

                using (var reader = ArchiveReader.Open(newFolder))
                {
                    attachments = ThumbnailStore.AttachmentsOf(reader);
                }

                _library.Thumbnails.Carry(oldHash, newHash, (id, sha) => attachments.Contains((id, sha)));
            }
            catch (Exception)
            {
                _library.Thumbnails.RemoveArchive(oldHash);
            }
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
