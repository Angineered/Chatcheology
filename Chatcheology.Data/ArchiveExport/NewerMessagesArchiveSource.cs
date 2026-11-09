using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Data.Media;
using Chatcheology.Data.NewerMessages;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.ArchiveExport
{
    /// <summary>
    /// What an archive export needs from the newer-messages file (CH-71 step 3b), read before anything is written.
    /// </summary>
    /// <remarks>
    /// <para>The file is found beside the main workspace by its fixed name (<see cref="NewerMessagesDatabase.FileName"/>);
    /// no file means no newer messages (0, not an error). A file that is there must prove it is a newer-messages file
    /// (its marker and <c>user_version</c>), pass SQLite's <c>quick_check</c> and read back consistently, or the export
    /// is refused before anything is written: a damaged file never gives a shorter archive.</para>
    /// <para><strong>Restored files.</strong> Each Restored attachment's file must be under its registered media root
    /// (the relative path cannot leave it) and present when the file is read; its size is taken from the file and its
    /// media type from its extension and size (<see cref="MediaClassification.Classify"/>, the workspace's own rule).
    /// Its bytes are hashed again when the exporter copies them, and a different hash stops the export.</para>
    /// <para>Read only: the file is opened read-only and the media files are only opened for reading.</para>
    /// </remarks>
    internal sealed class NewerMessagesArchiveSource
    {
        /// <summary>The basis type of a newer attachment restored by its msgstore file hash.</summary>
        internal const string BasisType = "MsgstoreFileHash";

        /// <summary>The basis statement of a newer attachment restored by its msgstore file hash. No path, no name.</summary>
        internal const string BasisText =
            "Restored by file hash: WhatsApp's message store records this file's SHA-256, and a file with exactly that " +
            "SHA-256 was in the copy of the phone's WhatsApp folder when the newer messages were added. Its bytes were " +
            "checked against the hash again when the archive was written.";

        private NewerMessagesArchiveSource(
            NewerMessagesContents contents, IReadOnlyDictionary<long, NewerMediaFile> files)
        {
            Info = contents.Info;
            Messages = contents.Messages;
            RunTimes = contents.Runs.ToDictionary(r => r.RunID, r => r.RunDateTimeUtc);
            Files = files;
            MediaRoots = contents.Runs.Select(r => r.MediaRootPath)
                .Concat(contents.Messages.Select(m => m.Attachment?.MediaRootPath))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal NewerMessagesInfo Info { get; }

        /// <summary>Every newer message in timeline order (by <c>NewerMessageID</c>).</summary>
        internal IReadOnlyList<NewerMessageRow> Messages { get; }

        /// <summary>Each run's time, by run: the confirmation time of the attachments it restored.</summary>
        internal IReadOnlyDictionary<long, DateTime> RunTimes { get; }

        /// <summary>Each Restored attachment's file, by <c>NewerAttachmentID</c>.</summary>
        internal IReadOnlyDictionary<long, NewerMediaFile> Files { get; }

        /// <summary>Every registered media root: the output may sit inside none of them.</summary>
        internal IReadOnlyList<string> MediaRoots { get; }

        /// <summary>Reads the file beside <paramref name="workspacePath"/>, or returns <see langword="null"/> when there is none.</summary>
        /// <exception cref="ArchiveExportRefusedException">The file is there but damaged or unusable. Nothing was written.</exception>
        internal static NewerMessagesArchiveSource? ReadBeside(string workspacePath, CancellationToken cancellationToken)
        {
            var path = NewerMessagesDatabase.PathBeside(workspacePath);

            if (!File.Exists(path))
            {
                return null;
            }

            NewerMessagesContents contents;

            try
            {
                using (var connection = NewerMessagesDatabase.OpenReadOnly(path))
                {
                    QuickCheck(connection);
                }

                contents = NewerMessagesReader.Read(path);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                throw new ArchiveExportRefusedException(
                    "The newer-messages file is busy: another program holds a lock on it. Nothing was written.");
            }
            catch (Exception exception) when (
                exception is SqliteException or InvalidOperationException or KeyNotFoundException or FormatException
                    or InvalidCastException or OverflowException)
            {
                // Its own message may carry a path; it stays on the inner exception.
                throw new ArchiveExportRefusedException(
                    "The newer-messages file beside the workspace is damaged or not a usable newer-messages file. " +
                    "Restore it from a backup. Nothing was written.",
                    exception);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }

            cancellationToken.ThrowIfCancellationRequested();

            RefuseDamagedRows(contents);

            return new NewerMessagesArchiveSource(contents, ReadFiles(contents, cancellationToken));
        }

        private static void QuickCheck(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";

            using var reader = command.ExecuteReader();
            var lines = new List<string>();

            while (reader.Read())
            {
                lines.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            }

            if (lines is not ["ok"])
            {
                throw new InvalidOperationException("The newer-messages file fails SQLite's quick_check.");
            }
        }

        /// <summary>The rules the file's own CHECKs state, checked again on what was read, plus what the archive needs.</summary>
        private static void RefuseDamagedRows(NewerMessagesContents contents)
        {
            var attachmentIDs = contents.Messages
                .Select(m => m.Attachment?.NewerAttachmentID)
                .OfType<long>()
                .ToHashSet();

            var runIDs = contents.Runs.Select(r => r.RunID).ToHashSet();
            var damaged = 0;

            foreach (var message in contents.Messages)
            {
                var ok = runIDs.Contains(message.RunID)
                         && TryParseLocal(message.DateTimeLocal, out _)
                         && message.Kind switch
                         {
                             NewerMessagesDatabase.KindUser =>
                                 message.SenderParticipantID is not null
                                 && (message.Attachment is not null || message.Text is not null),
                             NewerMessagesDatabase.KindSystem =>
                                 message.SenderParticipantID is null && message.Attachment is null
                                 && !string.IsNullOrEmpty(message.Text) && message.Caption is null,
                             NewerMessagesDatabase.KindPlaceholder =>
                                 message.SenderParticipantID is not null && message.Attachment is null
                                 && message.Text is { } wording && NewerPlaceholderWordings.All.Contains(wording)
                                 && message.Caption is null,
                             _ => false,
                         }
                         && (message.Caption is null || message.Caption.Length > 0)
                         && (message.ForwardScore is null || message.IsForwarded == true);

                if (message.Attachment is { } attachment)
                {
                    ok &= attachment.State switch
                    {
                        NewerMessagesDatabase.StateRestored =>
                            ArchiveFormat.IsSha256(attachment.Sha256)
                            && attachment.RelativePath is { Length: > 0 }
                            && attachment.MediaRootPath is { Length: > 0 }
                            && IsStorableName(LastSegment(attachment.RelativePath)),
                        NewerMessagesDatabase.StateUnmatched => attachment.Sha256 is null,
                        _ => false,
                    };

                    ok &= attachment.LowQualityCopyOfAttachmentID is not { } target
                          || (target != attachment.NewerAttachmentID && attachmentIDs.Contains(target));
                }

                if (!ok)
                {
                    damaged++;
                }
            }

            if (damaged > 0)
            {
                throw new ArchiveExportRefusedException(
                    $"{Count(damaged)} row(s) of the newer-messages file break its own rules, so the file is treated " +
                    "as damaged. Restore it from a backup. Nothing was written.");
            }
        }

        /// <summary>Each Restored attachment's file under its media root, with its size and media type.</summary>
        private static Dictionary<long, NewerMediaFile> ReadFiles(NewerMessagesContents contents, CancellationToken cancellationToken)
        {
            var files = new Dictionary<long, NewerMediaFile>();
            int outside = 0, absent = 0;

            foreach (var attachment in contents.Messages
                         .Select(m => m.Attachment)
                         .OfType<NewerAttachmentRow>()
                         .Where(a => a.State == NewerMessagesDatabase.StateRestored))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fullPath = FullPathUnder(attachment.MediaRootPath!, attachment.RelativePath!);

                if (fullPath is null)
                {
                    outside++;
                    continue;
                }

                long size;

                try
                {
                    var info = new FileInfo(fullPath);

                    if (!info.Exists)
                    {
                        absent++;
                        continue;
                    }

                    size = info.Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    absent++;
                    continue;
                }

                var fileName = LastSegment(attachment.RelativePath!);
                var mediaType = MediaTypeText.Format(
                    MediaClassification.Classify(MediaClassification.NormaliseExtension(fileName), size));

                files[attachment.NewerAttachmentID] = new NewerMediaFile(fullPath, fileName, size, mediaType);
            }

            if (outside > 0)
            {
                throw new ArchiveExportRefusedException(
                    $"{Count(outside)} restored newer-message file(s) have a recorded path that leaves the registered " +
                    "media folder, so the newer-messages file is treated as damaged. Nothing was written.");
            }

            if (absent > 0)
            {
                throw new ArchiveExportRefusedException(
                    $"{Count(absent)} restored newer-message file(s) are not in the registered media folder (the copy of " +
                    "the phone's WhatsApp folder the newer messages were added from). Put that folder back where it was. " +
                    "Nothing was written.");
            }

            return files;
        }

        /// <summary>The full path of <paramref name="relativePath"/> under <paramref name="root"/>, or null when it would leave it.</summary>
        /// <remarks>A drive root (<c>F:\</c>) is a media root like any other folder (CH-71 step 5).</remarks>
        internal static string? FullPathUnder(string root, string relativePath)
        {
            try
            {
                var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

                if (relativePath.Split('/').Any(s => s.Length == 0 || s == "." || s == "..")
                    || relativePath.Contains(':') || relativePath.Contains('\\') || relativePath.Any(char.IsControl))
                {
                    return null;
                }

                var full = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));

                return ArchivePaths.IsBeneath(full, rootFull) ? full : null;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        internal static bool TryParseLocal(string text, out DateTime local) =>
            DateTime.TryParseExact(
                text, "yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out local);

        private static string LastSegment(string relativePath) =>
            relativePath[(relativePath.LastIndexOf('/') + 1)..];

        private static bool IsStorableName(string name) =>
            name.Length > 0 && name.IndexOfAny(['/', '\\', ':']) < 0 && !name.Any(char.IsControl);

        private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>A Restored newer attachment's file: where it is, the name it is stored under, its size and media type.</summary>
    internal sealed record NewerMediaFile(string FullPath, string FileName, long SizeBytes, string MediaType);
}
