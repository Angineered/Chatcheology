using System.Buffers;
using System.Security.Cryptography;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Hashes inventoried media files and deduplicates them by content into media assets.
    /// </summary>
    /// <remarks>
    /// Resumable by design, because the work is large: a real archive is tens of thousands of files
    /// and tens of gigabytes, and an operation of that size will be interrupted — by a closed
    /// application, a restart, a cancelled run or a single unreadable file. Progress is therefore
    /// committed in bounded batches, and what a run has committed survives whatever ends it.
    /// <para>
    /// Resuming needs no bookmark. A file is pending precisely when its <c>SHA256</c> is null, so
    /// the database itself records what is left; there is no cursor to keep, none to lose, and no
    /// way for a stored position to disagree with the rows it points at.
    /// </para>
    /// <para>
    /// Deduplication is exact-content only. Two files are one asset when their SHA-256 values
    /// match, and never because their names, sizes, dates or durations resemble each other.
    /// </para>
    /// <para>
    /// The raw source is read-only throughout. Files are opened for reading, with sharing that
    /// forbids anything else modifying or deleting them mid-read, and nothing beneath a source root
    /// is created, renamed, moved or written.
    /// </para>
    /// </remarks>
    public sealed class MediaHashingService
    {
        /// <summary>
        /// How many successfully hashed files are committed together by default.
        /// </summary>
        /// <remarks>
        /// A compromise between two costs. One transaction around the whole run would make an
        /// interruption throw away hours of reading; one transaction per file would pay a durable
        /// commit for every file in the archive. A hundred bounds the loss from an interruption to
        /// seconds of work while amortising commits over a batch.
        /// </remarks>
        public const int DefaultBatchSize = 100;

        /// <summary>
        /// How much of a file is read at a time.
        /// </summary>
        /// <remarks>
        /// A video may be gigabytes; it is hashed by streaming in chunks this size and never held
        /// in memory whole. Large enough that reading a big file is not dominated by syscalls,
        /// small enough that cancellation is noticed promptly and that the buffer is not itself a
        /// burden.
        /// </remarks>
        private const int ReadBufferSize = 1024 * 1024;

        /// <summary>
        /// The value <c>MediaSourceID</c> keyset pagination starts below.
        /// </summary>
        /// <remarks>
        /// Identifiers from a SQLite <c>INTEGER PRIMARY KEY</c> are positive, so nothing can sort
        /// at or before this.
        /// </remarks>
        private const long BeforeFirstMediaSourceID = -1;

        /// <summary>
        /// Hashes every file still waiting in the workspace at <paramref name="databasePath"/>,
        /// creating and linking media assets as it goes.
        /// </summary>
        /// <param name="databasePath">
        /// An existing workspace database already at schema version
        /// <see cref="WorkspaceDatabase.SchemaVersion"/>. This method neither creates nor migrates
        /// a workspace.
        /// </param>
        /// <param name="mediaSourceID">
        /// Restrict the run to one media source, or null for every source in the workspace.
        /// </param>
        /// <param name="batchSize">
        /// How many files to hash before committing. See <see cref="DefaultBatchSize"/>.
        /// </param>
        /// <remarks>
        /// Files are processed in <c>(MediaSourceID, RelativePath)</c> order rather than in whatever
        /// order the database or the filesystem offers them, so two runs over the same workspace do
        /// the same work in the same sequence and an interrupted run resumes where an uninterrupted
        /// one would have been.
        /// <para>
        /// Running this again after everything is hashed does nothing and reports nothing to do.
        /// It is safe to call at any time.
        /// </para>
        /// </remarks>
        /// <exception cref="FileNotFoundException">
        /// There is no database at <paramref name="databasePath"/>. No file is created.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version. Nothing is written.
        /// </exception>
        public MediaHashingResult HashPendingFiles(
            string databasePath,
            long? mediaSourceID = null,
            int batchSize = DefaultBatchSize,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    "There is no workspace database at the supplied path. Hashing works through a " +
                    "workspace that already holds an inventory; it does not create one, and no " +
                    "file has been created here.",
                    databasePath);
            }

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "hashing");

            var roots = ReadSourceRoots(connection);
            var run = new HashingRun { PendingAtStart = CountPending(connection, mediaSourceID) };

            var lastMediaSourceID = BeforeFirstMediaSourceID;
            var lastRelativePath = string.Empty;

            while (!run.WasCancelled)
            {
                // Read a whole page and close the reader before any writing begins. Holding a
                // reader open on this connection while the batch transaction below writes through
                // it is the one shape that reliably deadlocks or fails against SQLite, and it would
                // do so only once the data was large enough to matter.
                var page = ReadPendingPage(
                    connection, mediaSourceID, lastMediaSourceID, lastRelativePath, batchSize);

                if (page.Count == 0)
                {
                    break;
                }

                lastMediaSourceID = page[^1].MediaSourceID;
                lastRelativePath = page[^1].RelativePath;

                // Hashing is done entirely outside the transaction: reading a hundred files may
                // take a while, and no other writer should be locked out for the duration of it.
                var hashed = HashPage(page, roots, run, cancellationToken);

                if (hashed.Count > 0)
                {
                    CommitBatch(connection, hashed, run);
                }
            }

            return run.ToResult(CountPending(connection, mediaSourceID));
        }

        /// <summary>
        /// Reads each media source's normalised root, so a file's physical path can be rebuilt.
        /// </summary>
        private static Dictionary<long, string> ReadSourceRoots(SqliteConnection connection)
        {
            var roots = new Dictionary<long, string>();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MediaSourceID, RootPath FROM MediaSource;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                roots[reader.GetInt64(0)] = MediaSourcePath.Normalise(reader.GetString(1));
            }

            return roots;
        }

        private static int CountPending(SqliteConnection connection, long? mediaSourceID)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM MediaFile
                WHERE SHA256 IS NULL
                  AND ($mediaSourceID IS NULL OR MediaSourceID = $mediaSourceID);
                """;

            command.Parameters.AddWithValue("$mediaSourceID", (object?)mediaSourceID ?? DBNull.Value);

            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>
        /// Reads the next page of pending files, in the run's deterministic order.
        /// </summary>
        /// <remarks>
        /// Paged by the last key seen rather than by an offset, and deliberately so. A plain
        /// "first N pending" query would return the same rows forever once a page contained a file
        /// that cannot be hashed: the failures never stop being pending, so the run would loop on
        /// them and never reach the rest of the archive. Advancing past everything already looked
        /// at means each file is attempted once per run whatever the outcome.
        /// <para>
        /// <c>RelativePath</c> compares under the column's default BINARY collation, which is
        /// exactly the ordering the pagination assumes. A case-insensitive comparison here would
        /// let two paths tie and a page boundary skip a row.
        /// </para>
        /// </remarks>
        private static List<PendingMediaFile> ReadPendingPage(
            SqliteConnection connection,
            long? mediaSourceID,
            long lastMediaSourceID,
            string lastRelativePath,
            int batchSize)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT MediaFileID, MediaSourceID, RelativePath, SizeBytes, MediaType
                FROM MediaFile
                WHERE SHA256 IS NULL
                  AND ($mediaSourceID IS NULL OR MediaSourceID = $mediaSourceID)
                  AND (MediaSourceID > $lastMediaSourceID
                       OR (MediaSourceID = $lastMediaSourceID
                           AND RelativePath > $lastRelativePath))
                ORDER BY MediaSourceID, RelativePath
                LIMIT $batchSize;
                """;

            command.Parameters.AddWithValue("$mediaSourceID", (object?)mediaSourceID ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastMediaSourceID", lastMediaSourceID);
            command.Parameters.AddWithValue("$lastRelativePath", lastRelativePath);
            command.Parameters.AddWithValue("$batchSize", batchSize);

            var page = new List<PendingMediaFile>(batchSize);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                page.Add(new PendingMediaFile
                {
                    MediaFileID = reader.GetInt64(0),
                    MediaSourceID = reader.GetInt64(1),
                    RelativePath = reader.GetString(2),
                    SizeBytes = reader.GetInt64(3),
                    MediaType = MediaTypeText.Parse(reader.GetString(4)),
                });
            }

            return page;
        }

        /// <summary>
        /// Hashes one page of files, recording failures rather than raising them.
        /// </summary>
        private static List<HashedMediaFile> HashPage(
            List<PendingMediaFile> page,
            Dictionary<long, string> roots,
            HashingRun run,
            CancellationToken cancellationToken)
        {
            var hashed = new List<HashedMediaFile>(page.Count);

            foreach (var file in page)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    run.WasCancelled = true;
                    break;
                }

                if (!TryResolveReadableFile(file, roots, out var fullPath))
                {
                    run.Fail(file.MediaFileID);
                    continue;
                }

                string hash;
                long bytesRead;

                try
                {
                    if (!TryComputeHash(fullPath, cancellationToken, out hash, out bytesRead))
                    {
                        // Cancelled part-way through this file. It contributes nothing: no hash is
                        // recorded, no asset is linked, and it is simply pending again next run.
                        run.WasCancelled = true;
                        break;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Something else holds the file, or it went away between the checks above and
                    // the open. One locked file must not end a run over tens of thousands.
                    run.Fail(file.MediaFileID);
                    continue;
                }

                run.PhysicalBytesHashed += bytesRead;

                hashed.Add(new HashedMediaFile { File = file, SHA256 = hash });
            }

            return hashed;
        }

        /// <summary>
        /// Resolves a stored file back to a physical path and proves it is still the file that was
        /// inventoried.
        /// </summary>
        /// <remarks>
        /// The raw source is meant to be immutable, so a file that has moved, vanished or changed
        /// size is a fact about the archive rather than something to accommodate. The row is left
        /// exactly as it is and the file is reported as failed: quietly re-measuring it would
        /// overwrite the inventory's record of what was there, and hashing it anyway would attach
        /// one file's content identity to another file's record.
        /// </remarks>
        private static bool TryResolveReadableFile(
            PendingMediaFile file, Dictionary<long, string> roots, out string fullPath)
        {
            fullPath = string.Empty;

            if (!roots.TryGetValue(file.MediaSourceID, out var root))
            {
                return false;
            }

            if (!MediaSourcePath.TryResolveUnderRoot(root, file.RelativePath, out var resolved))
            {
                return false;
            }

            var info = new FileInfo(resolved);

            if (!info.Exists || info.Length != file.SizeBytes)
            {
                return false;
            }

            fullPath = resolved;

            return true;
        }

        /// <summary>
        /// Streams a file through SHA-256, or reports that cancellation stopped it part-way.
        /// </summary>
        /// <remarks>
        /// <see cref="FileShare.Read"/> lets other readers in but keeps writers and deleters out
        /// for the duration, so a hash cannot describe a file that changed while it was being
        /// computed.
        /// <para>
        /// Read in chunks through an <see cref="IncrementalHash"/> rather than handed to a
        /// whole-stream helper, for two reasons: a multi-gigabyte video is never held in memory,
        /// and cancellation is checked between chunks so a long read can be stopped promptly
        /// instead of only between files.
        /// </para>
        /// <para>
        /// The result is upper-case hexadecimal, the project's canonical form.
        /// <c>MediaAsset.SHA256</c> is <c>COLLATE NOCASE</c> as a safety net, but writing one
        /// spelling consistently is what the safety net is there to catch, not to replace.
        /// </para>
        /// </remarks>
        private static bool TryComputeHash(
            string fullPath, CancellationToken cancellationToken, out string hash, out long bytesRead)
        {
            hash = string.Empty;
            bytesRead = 0;

            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                ReadBufferSize,
                FileOptions.SequentialScan);

            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

            try
            {
                int read;

                while ((read = stream.Read(buffer, 0, ReadBufferSize)) > 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    incrementalHash.AppendData(buffer, 0, read);
                    bytesRead += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            hash = Convert.ToHexString(incrementalHash.GetHashAndReset());

            return true;
        }

        /// <summary>
        /// Writes one batch of hashed files: their hashes, any new assets, and the links between
        /// them.
        /// </summary>
        /// <remarks>
        /// One transaction per batch, so the invariant that matters cannot be broken by an
        /// interruption: a committed <c>MediaFile.SHA256</c> always arrives together with the
        /// <c>MediaAsset</c> it names and the <c>MediaAssetFile</c> row linking them. There is no
        /// window in which a file claims a content identity that nothing in the workspace holds.
        /// </remarks>
        private static void CommitBatch(
            SqliteConnection connection, List<HashedMediaFile> hashed, HashingRun run)
        {
            using var transaction = connection.BeginTransaction();

            foreach (var entry in hashed)
            {
                if (!IsStillUnhashed(connection, transaction, entry.File.MediaFileID))
                {
                    run.AlreadyHashed++;
                    continue;
                }

                var existing = FindAssetByHash(connection, transaction, entry.SHA256);
                long mediaAssetID;

                if (existing is null)
                {
                    mediaAssetID = InsertMediaAsset(connection, transaction, entry);
                    run.NewAssets++;
                }
                else
                {
                    if (existing.SizeBytes != entry.File.SizeBytes)
                    {
                        // Identical bytes of different lengths is not a thing that happens; it is a
                        // sign that something is wrong with the data rather than with the file.
                        // The existing asset is left untouched and the new file is left unhashed.
                        run.Fail(entry.File.MediaFileID);
                        continue;
                    }

                    if (existing.MediaType != entry.File.MediaType)
                    {
                        run.Conflict(entry.File.MediaFileID);
                        continue;
                    }

                    mediaAssetID = existing.MediaAssetID;
                    run.ExistingAssetLinks++;
                }

                UpdateMediaFileHash(connection, transaction, entry);
                InsertMediaAssetFile(connection, transaction, mediaAssetID, entry.File.MediaFileID);
            }

            transaction.Commit();
        }

        private static bool IsStillUnhashed(
            SqliteConnection connection, SqliteTransaction transaction, long mediaFileID)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT SHA256 IS NULL FROM MediaFile WHERE MediaFileID = $mediaFileID;";

            command.Parameters.AddWithValue("$mediaFileID", mediaFileID);

            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }

        /// <remarks>
        /// The column is <c>COLLATE NOCASE</c>, so this finds an asset whichever case its hash was
        /// written in and one payload cannot become two assets through spelling alone.
        /// </remarks>
        private static ExistingMediaAsset? FindAssetByHash(
            SqliteConnection connection, SqliteTransaction transaction, string sha256)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT MediaAssetID, MediaType, SizeBytes
                FROM MediaAsset
                WHERE SHA256 = $sha256;
                """;

            command.Parameters.AddWithValue("$sha256", sha256);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return null;
            }

            return new ExistingMediaAsset
            {
                MediaAssetID = reader.GetInt64(0),
                MediaType = MediaTypeText.Parse(reader.GetString(1)),
                SizeBytes = reader.GetInt64(2),
            };
        }

        /// <remarks>
        /// Duration, width and height stay null. This phase reads no media metadata, and inventing
        /// somewhere to put a value nothing has measured would make the column's emptiness look
        /// like a measurement of zero.
        /// </remarks>
        private static long InsertMediaAsset(
            SqliteConnection connection, SqliteTransaction transaction, HashedMediaFile entry)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes, DurationMS, Width, Height)
                VALUES ($sha256, $mediaType, $sizeBytes, NULL, NULL, NULL)
                RETURNING MediaAssetID;
                """;

            command.Parameters.AddWithValue("$sha256", entry.SHA256);
            command.Parameters.AddWithValue("$mediaType", MediaTypeText.Format(entry.File.MediaType));
            command.Parameters.AddWithValue("$sizeBytes", entry.File.SizeBytes);

            return (long)command.ExecuteScalar()!;
        }

        private static void UpdateMediaFileHash(
            SqliteConnection connection, SqliteTransaction transaction, HashedMediaFile entry)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE MediaFile SET SHA256 = $sha256 WHERE MediaFileID = $mediaFileID;";

            command.Parameters.AddWithValue("$sha256", entry.SHA256);
            command.Parameters.AddWithValue("$mediaFileID", entry.File.MediaFileID);

            command.ExecuteNonQuery();
        }

        private static void InsertMediaAssetFile(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long mediaAssetID,
            long mediaFileID)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
                VALUES ($mediaAssetID, $mediaFileID);
                """;

            command.Parameters.AddWithValue("$mediaAssetID", mediaAssetID);
            command.Parameters.AddWithValue("$mediaFileID", mediaFileID);

            command.ExecuteNonQuery();
        }

        /// <summary>One inventoried file waiting to be hashed.</summary>
        private sealed class PendingMediaFile
        {
            internal required long MediaFileID { get; init; }

            internal required long MediaSourceID { get; init; }

            internal required string RelativePath { get; init; }

            internal required long SizeBytes { get; init; }

            internal required MediaType MediaType { get; init; }
        }

        /// <summary>One file whose content has been read and hashed, not yet recorded.</summary>
        private sealed class HashedMediaFile
        {
            internal required PendingMediaFile File { get; init; }

            internal required string SHA256 { get; init; }
        }

        /// <summary>An asset already holding a payload, as far as the conflict rules need it.</summary>
        private sealed class ExistingMediaAsset
        {
            internal required long MediaAssetID { get; init; }

            internal required MediaType MediaType { get; init; }

            internal required long SizeBytes { get; init; }
        }

        /// <summary>The tallies one run accumulates before they become a result.</summary>
        private sealed class HashingRun
        {
            private readonly List<long> _failedMediaFileIDs = [];
            private readonly List<long> _conflictedMediaFileIDs = [];

            internal required int PendingAtStart { get; init; }

            internal int AlreadyHashed { get; set; }

            internal int NewAssets { get; set; }

            internal int ExistingAssetLinks { get; set; }

            internal long PhysicalBytesHashed { get; set; }

            internal bool WasCancelled { get; set; }

            internal void Fail(long mediaFileID) => _failedMediaFileIDs.Add(mediaFileID);

            internal void Conflict(long mediaFileID) => _conflictedMediaFileIDs.Add(mediaFileID);

            internal MediaHashingResult ToResult(int remainingUnhashed) => new()
            {
                PendingAtStart = PendingAtStart,
                SuccessfullyHashed = NewAssets + ExistingAssetLinks,
                AlreadyHashed = AlreadyHashed,
                NewAssets = NewAssets,
                ExistingAssetLinks = ExistingAssetLinks,
                FailedFiles = _failedMediaFileIDs.Count,
                ClassificationConflicts = _conflictedMediaFileIDs.Count,
                RemainingUnhashed = remainingUnhashed,
                PhysicalBytesHashed = PhysicalBytesHashed,
                WasCancelled = WasCancelled,
                FailedMediaFileIDs = _failedMediaFileIDs,
                ConflictedMediaFileIDs = _conflictedMediaFileIDs,
            };
        }
    }
}
