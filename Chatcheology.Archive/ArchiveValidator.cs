using Microsoft.Data.Sqlite;

namespace Chatcheology.Archive
{
    /// <summary>
    /// Checks an archive folder against itself: needs nothing but the folder, so a viewer can run it
    /// after copying an archive.
    /// </summary>
    /// <remarks>
    /// <para><strong>Read-only.</strong> The files are hashed through <see cref="ArchiveContents"/>
    /// and <see cref="ArchiveHashing"/>, and the database is read through <see cref="ArchiveReader"/>.
    /// Nothing is written.</para>
    /// <para><strong>The checks</strong>, one <see cref="ArchiveCheck"/> each: every file matches
    /// <c>CONTENTS.sha256</c> with none missing or extra; <c>archive.db</c> opens as this format's
    /// version and passes <c>PRAGMA integrity_check</c>; <c>manifest.json</c> agrees with the database;
    /// every Restored attachment has its Media row and a media file with its hash, nothing else names
    /// media, and every Media row and media file is used; every low-quality link and every attachment
    /// points at a row that exists, and exactly the Restored attachments have a basis line; sequence
    /// numbers run 1..n without a gap, every sender is a participant, and exactly the recorded local
    /// participant is local.</para>
    /// <para>A database that cannot be opened or read stops the checks that need it; those are
    /// reported as not run, and the archive fails.</para>
    /// <para><strong>Counts only.</strong> The result never carries message text, a name, a file name
    /// or a path; a problem is identified at most by its message's sequence number or its
    /// attachment's identifier.</para>
    /// </remarks>
    public static class ArchiveValidator
    {
        /// <summary>How many problems the result lists individually; the rest are only counted.</summary>
        public const int MaximumListedProblems = 10;

        private const string MediaPrefix = ArchiveFormat.MediaFolderName + "/";

        /// <summary>The check each kind of problem belongs to.</summary>
        public static ArchiveCheck CheckOf(ArchiveProblemKind kind) => kind switch
        {
            ArchiveProblemKind.ContentsListUnusable
                or ArchiveProblemKind.ContentsFileChanged
                or ArchiveProblemKind.ContentsFileMissing
                or ArchiveProblemKind.ContentsFileNotListed => ArchiveCheck.Contents,

            ArchiveProblemKind.DatabaseUnreadable
                or ArchiveProblemKind.DatabaseIntegrityFailed => ArchiveCheck.Database,

            ArchiveProblemKind.ManifestUnreadable
                or ArchiveProblemKind.ManifestHeaderDiffers
                or ArchiveProblemKind.ManifestCountDiffers => ArchiveCheck.Manifest,

            ArchiveProblemKind.RestoredWithoutMediaRow
                or ArchiveProblemKind.MediaFileMissing
                or ArchiveProblemKind.MediaFileDiffers
                or ArchiveProblemKind.UnrestoredWithMedia
                or ArchiveProblemKind.MediaRowUnused
                or ArchiveProblemKind.MediaFileUnused => ArchiveCheck.Media,

            ArchiveProblemKind.LowQualityLinkDangling
                or ArchiveProblemKind.AttachmentWithoutMessage
                or ArchiveProblemKind.RestoredWithoutBasis
                or ArchiveProblemKind.BasisOnUnrestored => ArchiveCheck.LinksAndBasis,

            ArchiveProblemKind.SequenceGap
                or ArchiveProblemKind.SenderNotParticipant
                or ArchiveProblemKind.LocalParticipantWrong => ArchiveCheck.SequenceAndSenders,

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a problem kind of the validator."),
        };

        /// <summary>Runs every check on the archive in <paramref name="archiveFolder"/>.</summary>
        /// <exception cref="ArgumentException">There is no folder at the path.</exception>
        public static ArchiveValidationResult Validate(string archiveFolder)
        {
            // Refuses a missing folder with a fixed message, before anything else is looked at.
            var contents = ArchiveContents.Check(archiveFolder);
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archiveFolder));

            var record = new Recorder();

            CheckContents(contents, record);

            record.Examined(ArchiveCheck.Database, 1);

            try
            {
                using var reader = ArchiveReader.Open(folder);

                CheckDatabase(reader, record);

                // A check is marked as run only when it finishes, so a read that fails part-way leaves
                // it, and every check after it, not run.
                CheckManifest(folder, reader, record);
                CheckRows(folder, reader, record);
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException)
            {
                record.Ran(ArchiveCheck.Database);
                record.Add(ArchiveProblemKind.DatabaseUnreadable);
            }

            return record.Result(contents);
        }

        // -------------------------------------------------------------------------------------
        // The checks.
        // -------------------------------------------------------------------------------------

        private static void CheckContents(ArchiveContentsCheck contents, Recorder record)
        {
            record.Examined(ArchiveCheck.Contents, contents.Listed);

            record.AddCount(ArchiveProblemKind.ContentsListUnusable, contents.Problems.Count);
            record.AddCount(ArchiveProblemKind.ContentsFileChanged, contents.Changed);
            record.AddCount(ArchiveProblemKind.ContentsFileMissing, contents.Missing);
            record.AddCount(ArchiveProblemKind.ContentsFileNotListed, contents.NotListed);

            record.Ran(ArchiveCheck.Contents);
        }

        private static void CheckDatabase(ArchiveReader reader, Recorder record)
        {
            // Open refuses version 0 and any higher version, so the version is this format's.
            if (reader.ReadIntegrityCheck() != "ok")
            {
                record.Add(ArchiveProblemKind.DatabaseIntegrityFailed);
            }

            record.Ran(ArchiveCheck.Database);
        }

        private static void CheckManifest(string folder, ArchiveReader reader, Recorder record)
        {
            ArchiveManifest manifest;

            try
            {
                manifest = ArchiveManifest.Parse(File.ReadAllBytes(Path.Combine(folder, ArchiveFormat.ManifestFileName)));
            }
            catch (Exception exception) when (exception is ArchiveFormatException or IOException or UnauthorizedAccessException)
            {
                record.Examined(ArchiveCheck.Manifest, 1);
                record.Add(ArchiveProblemKind.ManifestUnreadable);
                record.Ran(ArchiveCheck.Manifest);

                return;
            }

            var header = new[]
            {
                manifest.FormatVersion == ArchiveFormat.FormatVersion,
                manifest.FormatVersion == reader.Info.FormatVersion,
                manifest.CreatedUtc.ToUniversalTime() == reader.Info.CreatedUtc,
            };

            var counted = manifest.Counts;

            var counts = new (int Manifest, string Sql)[]
            {
                (counted.Participants, "SELECT COUNT(*) FROM Participant;"),
                (counted.Messages, "SELECT COUNT(*) FROM Message;"),
                (counted.Attachments, "SELECT COUNT(*) FROM Attachment;"),
                (counted.Restored, "SELECT COUNT(*) FROM Attachment WHERE State = 'Restored';"),
                (counted.Missing, "SELECT COUNT(*) FROM Attachment WHERE State = 'Missing';"),
                (counted.Unmatched, "SELECT COUNT(*) FROM Attachment WHERE State = 'Unmatched';"),
                (counted.MediaFiles, "SELECT COUNT(*) FROM Media;"),
            };

            record.Examined(ArchiveCheck.Manifest, header.Length + counts.Length);
            record.AddCount(ArchiveProblemKind.ManifestHeaderDiffers, header.Count(same => !same));
            record.AddCount(ArchiveProblemKind.ManifestCountDiffers, counts.Count(c => c.Manifest != reader.ReadCount(c.Sql)));
            record.Ran(ArchiveCheck.Manifest);
        }

        /// <summary>
        /// The media, link, basis, sequence and sender checks, in one pass over the conversation, page
        /// by page, then a few whole-table questions.
        /// </summary>
        private static void CheckRows(string folder, ArchiveReader reader, Recorder record)
        {
            var participants = reader.ReadParticipants();
            var participantIDs = participants.Select(p => p.ParticipantID).ToHashSet();

            var attachmentIDs = new HashSet<long>();
            var links = new List<(long AttachmentID, int SequenceNumber, long Target)>();
            var usedMediaPaths = new HashSet<string>(StringComparer.Ordinal);
            var fileHashes = new Dictionary<string, string?>(StringComparer.Ordinal);

            int restored = 0, messages = 0, previous = 0;

            var page = reader.ReadFirstPage(ArchiveReader.MaximumPageSize);

            while (true)
            {
                foreach (var message in page.Messages)
                {
                    messages++;

                    if (message.SequenceNumber != previous + 1)
                    {
                        record.Add(ArchiveProblemKind.SequenceGap, message.SequenceNumber);
                    }

                    previous = message.SequenceNumber;

                    if (message.SenderParticipantID is { } sender && !participantIDs.Contains(sender))
                    {
                        record.Add(ArchiveProblemKind.SenderNotParticipant, message.SequenceNumber);
                    }

                    foreach (var attachment in message.Attachments)
                    {
                        attachmentIDs.Add(attachment.AttachmentID);

                        if (attachment.LowQualityCopyOfAttachmentID is { } target)
                        {
                            links.Add((attachment.AttachmentID, message.SequenceNumber, target));
                        }

                        if (attachment.State != ArchiveAttachmentState.Restored)
                        {
                            if (attachment.MediaSha256 is not null || attachment.OriginalFileName is not null)
                            {
                                record.Add(ArchiveProblemKind.UnrestoredWithMedia, message.SequenceNumber, attachment.AttachmentID);
                            }

                            continue;
                        }

                        restored++;

                        if (attachment.Basis is null)
                        {
                            record.Add(ArchiveProblemKind.RestoredWithoutBasis, message.SequenceNumber, attachment.AttachmentID);
                        }

                        CheckMedia(reader, page, message, attachment, usedMediaPaths, fileHashes, record);
                    }
                }

                if (!page.HasLater || page.LastSequenceNumber is not { } last)
                {
                    break;
                }

                page = reader.ReadPageFrom(last + 1, ArchiveReader.MaximumPageSize);
            }

            foreach (var (attachmentID, sequenceNumber, target) in links)
            {
                if (!attachmentIDs.Contains(target))
                {
                    record.Add(ArchiveProblemKind.LowQualityLinkDangling, sequenceNumber, attachmentID);
                }
            }

            foreach (var orphan in reader.ReadIdentifiers(
                         """
                         SELECT a.AttachmentID FROM Attachment AS a
                         LEFT JOIN Message AS m ON m.MessageID = a.MessageID
                         WHERE m.MessageID IS NULL ORDER BY a.AttachmentID;
                         """))
            {
                record.Add(ArchiveProblemKind.AttachmentWithoutMessage, attachmentID: orphan);
            }

            foreach (var extra in reader.ReadIdentifiers(
                         """
                         SELECT b.AttachmentID FROM AttachmentBasis AS b
                         LEFT JOIN Attachment AS a ON a.AttachmentID = b.AttachmentID
                         WHERE a.AttachmentID IS NULL OR a.State <> 'Restored' ORDER BY b.AttachmentID;
                         """))
            {
                record.Add(ArchiveProblemKind.BasisOnUnrestored, attachmentID: extra);
            }

            record.AddCount(
                ArchiveProblemKind.MediaRowUnused,
                (int)reader.ReadCount(
                    """
                    SELECT COUNT(*) FROM Media WHERE Sha256 NOT IN (
                        SELECT MediaSha256 FROM Attachment WHERE State = 'Restored' AND MediaSha256 IS NOT NULL);
                    """));

            var mediaFiles = ArchiveContents.RelativeFiles(folder)
                .Where(r => r.StartsWith(MediaPrefix, StringComparison.Ordinal))
                .ToList();

            record.AddCount(ArchiveProblemKind.MediaFileUnused, mediaFiles.Count(r => !usedMediaPaths.Contains(r)));

            var locals = participants.Where(p => p.IsLocal).ToList();

            if (locals.Count != 1 || locals[0].ParticipantID != reader.Info.LocalParticipantID)
            {
                record.Add(ArchiveProblemKind.LocalParticipantWrong);
            }

            record.Examined(ArchiveCheck.Media, restored + mediaFiles.Count);
            record.Examined(ArchiveCheck.LinksAndBasis, (int)reader.ReadCount("SELECT COUNT(*) FROM Attachment;"));
            record.Examined(ArchiveCheck.SequenceAndSenders, messages);

            record.Ran(ArchiveCheck.Media);
            record.Ran(ArchiveCheck.LinksAndBasis);
            record.Ran(ArchiveCheck.SequenceAndSenders);
        }

        /// <summary>One Restored attachment: its Media row, and its file's presence and hash.</summary>
        private static void CheckMedia(
            ArchiveReader reader,
            ArchivePage page,
            ArchiveMessage message,
            ArchiveAttachment attachment,
            HashSet<string> usedMediaPaths,
            Dictionary<string, string?> fileHashes,
            Recorder record)
        {
            // The schema forbids a Restored row without both; a damaged one names no row and no file.
            if (attachment.MediaSha256 is not { } sha || attachment.MediaRelativePath is not { } relative)
            {
                record.Add(ArchiveProblemKind.RestoredWithoutMediaRow, message.SequenceNumber, attachment.AttachmentID);
                record.Add(ArchiveProblemKind.MediaFileMissing, message.SequenceNumber, attachment.AttachmentID);

                return;
            }

            if (!page.Media.ContainsKey(sha))
            {
                record.Add(ArchiveProblemKind.RestoredWithoutMediaRow, message.SequenceNumber, attachment.AttachmentID);
            }

            usedMediaPaths.Add(relative);

            if (!fileHashes.TryGetValue(relative, out var actual))
            {
                actual = HashOrNull(reader.MediaFullPath(attachment)!);
                fileHashes[relative] = actual;
            }

            // Hashed once per file, however many attachments share it; counted per attachment.
            if (actual is null)
            {
                record.Add(ArchiveProblemKind.MediaFileMissing, message.SequenceNumber, attachment.AttachmentID);
            }
            else if (actual != sha)
            {
                record.Add(ArchiveProblemKind.MediaFileDiffers, message.SequenceNumber, attachment.AttachmentID);
            }
        }

        /// <summary>The file's hash, or <see langword="null"/> when it is not there or cannot be read.</summary>
        private static string? HashOrNull(string path)
        {
            try
            {
                return File.Exists(path) ? ArchiveHashing.Sha256OfFile(path) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // -------------------------------------------------------------------------------------
        // Collecting the result.
        // -------------------------------------------------------------------------------------

        private sealed class Recorder
        {
            private readonly Dictionary<ArchiveProblemKind, int> _counts =
                Enum.GetValues<ArchiveProblemKind>().ToDictionary(kind => kind, _ => 0);

            private readonly Dictionary<ArchiveCheck, int> _examined = [];
            private readonly HashSet<ArchiveCheck> _ran = [];
            private readonly List<ArchiveProblem> _first = [];

            internal void Ran(ArchiveCheck check) => _ran.Add(check);

            internal void Examined(ArchiveCheck check, int count) =>
                _examined[check] = _examined.GetValueOrDefault(check) + count;

            /// <summary>Problems counted without an identifier.</summary>
            internal void AddCount(ArchiveProblemKind kind, int count)
            {
                _counts[kind] += count;

                for (var index = 0; index < count && _first.Count < MaximumListedProblems; index++)
                {
                    _first.Add(new ArchiveProblem(kind, null, null));
                }
            }

            internal void Add(ArchiveProblemKind kind, int? sequenceNumber = null, long? attachmentID = null)
            {
                _counts[kind]++;

                if (_first.Count < MaximumListedProblems)
                {
                    _first.Add(new ArchiveProblem(kind, sequenceNumber, attachmentID));
                }
            }

            internal ArchiveValidationResult Result(ArchiveContentsCheck contents) => new()
            {
                Checks = Enum.GetValues<ArchiveCheck>()
                    .Select(check => new ArchiveCheckOutcome
                    {
                        Check = check,
                        Ran = _ran.Contains(check),
                        Examined = _examined.GetValueOrDefault(check),
                        Problems = _counts.Where(pair => CheckOf(pair.Key) == check).Sum(pair => pair.Value),
                    })
                    .ToList(),
                ProblemCounts = new Dictionary<ArchiveProblemKind, int>(_counts),
                FirstProblems = _first.ToList(),
                Contents = contents,
            };
        }
    }

    /// <summary>One of the self-checks <see cref="ArchiveValidator"/> runs.</summary>
    public enum ArchiveCheck
    {
        /// <summary>Every file matches <c>CONTENTS.sha256</c>; none is missing or extra.</summary>
        Contents,

        /// <summary><c>archive.db</c> opens as this format's version and passes its integrity check.</summary>
        Database,

        /// <summary><c>manifest.json</c> records this format, the database's creation time and its counts.</summary>
        Manifest,

        /// <summary>Restored attachments have their media; nothing else does; every media row and file is used.</summary>
        Media,

        /// <summary>Links and attachments point at rows that exist; exactly the Restored ones have a basis.</summary>
        LinksAndBasis,

        /// <summary>Sequence numbers run 1..n; every sender is a participant; the local participant is right.</summary>
        SequenceAndSenders,
    }

    /// <summary>A kind of problem the self-check counts. Each belongs to one <see cref="ArchiveCheck"/>.</summary>
    public enum ArchiveProblemKind
    {
        /// <summary><c>CONTENTS.sha256</c> is missing or not in its format.</summary>
        ContentsListUnusable,

        /// <summary>A listed file's bytes differ from its listed hash.</summary>
        ContentsFileChanged,

        /// <summary>A listed file is not there.</summary>
        ContentsFileMissing,

        /// <summary>A file in the folder is not listed.</summary>
        ContentsFileNotListed,

        /// <summary><c>archive.db</c> is missing, not this format's version, or could not be read.</summary>
        DatabaseUnreadable,

        /// <summary><c>PRAGMA integrity_check</c> did not report <c>ok</c>.</summary>
        DatabaseIntegrityFailed,

        /// <summary><c>manifest.json</c> is missing or not this format's manifest.</summary>
        ManifestUnreadable,

        /// <summary>One of the manifest's format version or creation time differs from the database's.</summary>
        ManifestHeaderDiffers,

        /// <summary>One of the manifest's counts differs from the rows in the database.</summary>
        ManifestCountDiffers,

        /// <summary>A Restored attachment names a hash with no Media row.</summary>
        RestoredWithoutMediaRow,

        /// <summary>A Restored attachment's media file is not there or cannot be read.</summary>
        MediaFileMissing,

        /// <summary>A Restored attachment's media file does not hash to its recorded hash.</summary>
        MediaFileDiffers,

        /// <summary>A Missing or Unmatched attachment names media.</summary>
        UnrestoredWithMedia,

        /// <summary>A Media row no Restored attachment names.</summary>
        MediaRowUnused,

        /// <summary>A file in the media folder no Restored attachment names.</summary>
        MediaFileUnused,

        /// <summary>A low-quality link names an attachment the archive does not hold.</summary>
        LowQualityLinkDangling,

        /// <summary>An attachment's message is not in the archive.</summary>
        AttachmentWithoutMessage,

        /// <summary>A Restored attachment has no basis line.</summary>
        RestoredWithoutBasis,

        /// <summary>A basis line belongs to an attachment that is not Restored, or to none.</summary>
        BasisOnUnrestored,

        /// <summary>A message's sequence number is not one more than the one before it (the first is 1).</summary>
        SequenceGap,

        /// <summary>A message's sender is not a participant.</summary>
        SenderNotParticipant,

        /// <summary>Not exactly one participant is local, or it is not the recorded local participant.</summary>
        LocalParticipantWrong,
    }

    /// <summary>
    /// One listed problem: its kind, and at most the sequence number of its message and the identifier
    /// of its attachment. Never text, a name or a path.
    /// </summary>
    /// <param name="Kind">What is wrong.</param>
    /// <param name="SequenceNumber">The message involved, when there is one.</param>
    /// <param name="AttachmentID">The attachment involved, when there is one.</param>
    public sealed record ArchiveProblem(ArchiveProblemKind Kind, int? SequenceNumber, long? AttachmentID);

    /// <summary>How one check went: whether it ran, how much it looked at, how many problems it found.</summary>
    public sealed class ArchiveCheckOutcome
    {
        /// <summary>The check.</summary>
        public required ArchiveCheck Check { get; init; }

        /// <summary>False when the database could not be read far enough to run it.</summary>
        public required bool Ran { get; init; }

        /// <summary>
        /// What it looked at: listed files; the database; manifest fields; Restored attachments plus
        /// media files; attachments; messages.
        /// </summary>
        public required int Examined { get; init; }

        /// <summary>Problems found, over every kind of the check.</summary>
        public required int Problems { get; init; }

        /// <summary>Ran and found nothing wrong.</summary>
        public bool Passed => Ran && Problems == 0;
    }

    /// <summary>The outcome of <see cref="ArchiveValidator.Validate"/>. Counts and identifiers only.</summary>
    public sealed class ArchiveValidationResult
    {
        /// <summary>Every check, in <see cref="ArchiveCheck"/> order.</summary>
        public required IReadOnlyList<ArchiveCheckOutcome> Checks { get; init; }

        /// <summary>How many problems of each kind; every kind is present, most with zero.</summary>
        public required IReadOnlyDictionary<ArchiveProblemKind, int> ProblemCounts { get; init; }

        /// <summary>The first <see cref="ArchiveValidator.MaximumListedProblems"/> problems found.</summary>
        public required IReadOnlyList<ArchiveProblem> FirstProblems { get; init; }

        /// <summary>The contents check's own counts.</summary>
        public required ArchiveContentsCheck Contents { get; init; }

        /// <summary>Every check ran and none found a problem.</summary>
        public bool Passed => Checks.All(check => check.Passed);
    }
}
