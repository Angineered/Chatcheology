using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Registers a physical media directory in a workspace and records every file beneath it.
    /// </summary>
    /// <remarks>
    /// One call is one atomic inventory. The walk happens first and in full; only when the complete
    /// file list is in hand does a transaction open, and the <c>MediaSource</c> row and every
    /// <c>MediaFile</c> row it owns are then written together. A failure at any point leaves the
    /// workspace exactly as it was — never a source with some of its files, which would look
    /// afterwards like a directory that had shrunk.
    /// <para>
    /// Ordering the work this way costs nothing, because discovery reads only directory metadata.
    /// Hashing is the expensive part and is deliberately a separate, resumable operation:
    /// see <see cref="MediaHashingService"/>.
    /// </para>
    /// <para>
    /// The same responsibility split the importer follows applies here.
    /// <see cref="WorkspaceDatabase.Initialise(string)"/> creates and migrates a workspace; this
    /// service requires one that already exists at the current version and never creates, migrates
    /// or upgrades anything as a side effect of being called.
    /// </para>
    /// <para>
    /// The raw source is never written to. Nothing here creates, renames, moves, deletes or even
    /// opens a file inside the root it is inventorying.
    /// </para>
    /// </remarks>
    public sealed class MediaInventoryService
    {
        /// <summary>
        /// Walks <paramref name="rootPath"/> and reports what is there, touching no workspace.
        /// </summary>
        /// <param name="rootPath">The directory to walk. Read-only throughout.</param>
        /// <param name="sourceType">
        /// The layout to interpret the tree as, which decides whether direction and naming-derived
        /// dates are read. It affects the counts, not which files are found.
        /// </param>
        /// <remarks>
        /// The same walk <see cref="Inventory"/> performs, without the database half. It exists so a
        /// source can be examined before anything is committed — and so a preflight and the
        /// inventory it precedes cannot disagree about what a directory contains, which they could
        /// if the preflight counted files its own way.
        /// <para>
        /// An empty directory is reported here as a directory containing nothing. It is only
        /// <see cref="Inventory"/> that refuses it, because only there does it become a stored
        /// claim.
        /// </para>
        /// </remarks>
        /// <exception cref="DirectoryNotFoundException">
        /// <paramref name="rootPath"/> does not exist or is not a directory.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        /// Part of the tree could not be read. Nothing is skipped silently.
        /// </exception>
        public MediaDiscoverySummary Discover(
            string rootPath, string sourceType, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);

            var normalisedRoot = RequireExistingDirectory(rootPath);

            return MediaFileDiscovery.Summarise(
                MediaFileDiscovery.Discover(normalisedRoot, sourceType, cancellationToken));
        }

        /// <summary>
        /// Registers <paramref name="request"/> as a media source in the workspace at
        /// <paramref name="databasePath"/> and records every file beneath its root.
        /// </summary>
        /// <param name="databasePath">
        /// An existing workspace database already at schema version
        /// <see cref="WorkspaceDatabase.SchemaVersion"/>. No location is assumed here, and this
        /// method neither creates nor migrates a workspace.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// Required metadata is missing, or
        /// <see cref="MediaSourceRequest.ImportedDateTimeUtc"/> is not UTC.
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// There is no database at <paramref name="databasePath"/>. No file is created.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        /// <see cref="MediaSourceRequest.RootPath"/> does not exist or is not a directory.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version, the root overlaps a source already
        /// registered, or the root contains no files. Nothing is written in any of these cases.
        /// </exception>
        public MediaInventoryResult Inventory(
            string databasePath,
            MediaSourceRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ArgumentNullException.ThrowIfNull(request);

            ValidateRequest(request);

            var normalisedRoot = RequireExistingDirectory(request.RootPath);

            // Checked before a connection is built, because the workspace connection string opens
            // with ReadWriteCreate: opening a path that holds no database would create an empty one
            // purely to reject it a moment later, leaving a stray file the caller never asked for.
            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    "There is no workspace database at the supplied path. An inventory writes into " +
                    "a workspace that WorkspaceDatabase.Initialise has already created; it does " +
                    "not create one, and no file has been created here.",
                    databasePath);
            }

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            // Before the transaction, so a workspace that must not be written to is never written
            // to at all rather than being written to and rolled back.
            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "an inventory");

            RequireNonOverlappingRoot(connection, normalisedRoot);

            // Outside the transaction. The walk is the slow half of this operation, and holding a
            // write transaction open across it would lock the workspace for its whole duration
            // for no benefit: nothing is written until the file list is complete anyway.
            var discovery = MediaFileDiscovery.Discover(
                normalisedRoot, request.SourceType, cancellationToken);

            if (discovery.Files.Count == 0)
            {
                throw new InvalidOperationException(
                    "The selected media root contains no files, so no media source was created. " +
                    "An empty root is far more likely to be the wrong directory than a meaningful " +
                    "part of a reconstruction, and recording it would leave a source that " +
                    "permanently claims an archive holds nothing. The workspace is unchanged.");
            }

            using var transaction = connection.BeginTransaction();

            var mediaSourceID =
                InsertMediaSource(connection, transaction, request, normalisedRoot);

            InsertMediaFiles(connection, transaction, mediaSourceID, discovery);

            // Anything thrown above leaves this uncalled, and disposing an uncommitted transaction
            // rolls it back, so a failed inventory leaves no source and no files rather than a
            // partial record of one.
            transaction.Commit();

            return new MediaInventoryResult
            {
                MediaSourceID = mediaSourceID,
                Summary = MediaFileDiscovery.Summarise(discovery),
            };
        }

        /// <summary>
        /// Normalises a supplied root and proves it is a directory that exists.
        /// </summary>
        private static string RequireExistingDirectory(string rootPath)
        {
            var normalisedRoot = MediaSourcePath.Normalise(rootPath);

            if (!Directory.Exists(normalisedRoot))
            {
                throw new DirectoryNotFoundException(
                    File.Exists(normalisedRoot)
                        ? "The supplied media root is a file, not a directory. A media source is a " +
                          "directory tree to walk. Nothing has been written."
                        : "The supplied media root does not exist. Nothing has been written.");
            }

            return normalisedRoot;
        }

        private static void ValidateRequest(MediaSourceRequest request)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName, nameof(request.DisplayName));
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceType, nameof(request.SourceType));
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath, nameof(request.RootPath));

            if (request.DeviceDescription is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(
                    request.DeviceDescription, nameof(request.DeviceDescription));
            }

            if (request.ImportedDateTimeUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    $"ImportedDateTimeUtc has DateTimeKind.{request.ImportedDateTimeUtc.Kind}, but " +
                    $"workspace metadata records a real instant and must be DateTimeKind.Utc. The " +
                    $"value is not converted, because guessing which instant a non-UTC value meant " +
                    $"would silently record the wrong registration time.",
                    nameof(request));
            }
        }

        /// <summary>
        /// Refuses a root that is, contains, or sits inside a source already registered.
        /// </summary>
        /// <remarks>
        /// An application-level rule, not a schema constraint. Schema version 2 has no uniqueness
        /// on <c>MediaSource.RootPath</c>, and adding one would only catch exact string repeats
        /// while forcing a schema change to express something SQL cannot express well: overlap is a
        /// path-structure question, not a text-equality one.
        /// <para>
        /// The comparison is textual and Windows-shaped — see <see cref="MediaSourcePath"/> for
        /// what that does and does not cover. In particular it does not detect the same directory
        /// reached through a <c>subst</c> drive, a UNC alias, an 8.3 short name or a junction.
        /// </para>
        /// <para>
        /// An overlapping root is rejected outright rather than merged into or updated over the
        /// existing one. Merging would silently change what an earlier source means, and this
        /// service's job is to record what it is told, not to reconcile two accounts of it.
        /// </para>
        /// </remarks>
        private static void RequireNonOverlappingRoot(
            SqliteConnection connection, string normalisedRoot)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MediaSourceID, RootPath FROM MediaSource;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var existingID = reader.GetInt64(0);
                var existingRoot = MediaSourcePath.Normalise(reader.GetString(1));

                if (!MediaSourcePath.Overlaps(existingRoot, normalisedRoot))
                {
                    continue;
                }

                // The message names the colliding source by its identifier rather than by its
                // path, so a diagnostic about a private media location does not repeat that
                // location back.
                throw new InvalidOperationException(
                    $"The supplied media root overlaps MediaSource {existingID}, which is already " +
                    $"registered: the two are the same directory, or one lies inside the other. " +
                    $"Inventorying both would record the same physical files twice under different " +
                    $"sources. Separate, non-overlapping roots are what registering several media " +
                    $"sources is for. Nothing has been written.");
            }
        }

        private static long InsertMediaSource(
            SqliteConnection connection,
            SqliteTransaction transaction,
            MediaSourceRequest request,
            string normalisedRoot)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO MediaSource (
                    DisplayName,
                    SourceType,
                    RootPath,
                    DeviceDescription,
                    ImportedDateTimeUtc)
                VALUES (
                    $displayName,
                    $sourceType,
                    $rootPath,
                    $deviceDescription,
                    $importedDateTimeUtc)
                RETURNING MediaSourceID;
                """;

            command.Parameters.AddWithValue("$displayName", request.DisplayName);
            command.Parameters.AddWithValue("$sourceType", request.SourceType);

            // The normalised form, so what is stored is what the overlap guard will compare next
            // time and what the hashing pass will resolve files against.
            command.Parameters.AddWithValue("$rootPath", normalisedRoot);

            command.Parameters.AddWithValue(
                "$deviceDescription", (object?)request.DeviceDescription ?? DBNull.Value);

            command.Parameters.AddWithValue(
                "$importedDateTimeUtc",
                request.ImportedDateTimeUtc.ToString(
                    WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));

            return (long)command.ExecuteScalar()!;
        }

        /// <remarks>
        /// One prepared command with its parameters rebound per file, rather than a command built
        /// per row. A real source holds tens of thousands of files, and re-parsing the same
        /// statement for each of them would dominate the cost of an operation that is otherwise
        /// almost entirely a single sequential write.
        /// </remarks>
        private static void InsertMediaFiles(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long mediaSourceID,
            MediaDiscovery discovery)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO MediaFile (
                    MediaSourceID,
                    RelativePath,
                    FileName,
                    Extension,
                    SizeBytes,
                    SHA256,
                    MediaType,
                    FileDate,
                    IsSent,
                    DurationMS,
                    Width,
                    Height)
                VALUES (
                    $mediaSourceID,
                    $relativePath,
                    $fileName,
                    $extension,
                    $sizeBytes,
                    NULL,
                    $mediaType,
                    $fileDate,
                    $isSent,
                    NULL,
                    NULL,
                    NULL);
                """;

            command.Parameters.AddWithValue("$mediaSourceID", mediaSourceID);

            var relativePath = command.Parameters.Add("$relativePath", SqliteType.Text);
            var fileName = command.Parameters.Add("$fileName", SqliteType.Text);
            var extension = command.Parameters.Add("$extension", SqliteType.Text);
            var sizeBytes = command.Parameters.Add("$sizeBytes", SqliteType.Integer);
            var mediaType = command.Parameters.Add("$mediaType", SqliteType.Text);
            var fileDate = command.Parameters.Add("$fileDate", SqliteType.Text);
            var isSent = command.Parameters.Add("$isSent", SqliteType.Integer);

            command.Prepare();

            foreach (var file in discovery.Files)
            {
                relativePath.Value = file.RelativePath;
                fileName.Value = file.FileName;
                extension.Value = (object?)file.Extension ?? DBNull.Value;
                sizeBytes.Value = file.SizeBytes;
                mediaType.Value = MediaTypeText.Format(file.MediaType);

                fileDate.Value = file.FileDate is { } date
                    ? MediaClassification.FormatFileDate(date)
                    : DBNull.Value;

                isSent.Value = discovery.IsSent(file) is { } sent
                    ? (sent ? 1 : 0)
                    : (object)DBNull.Value;

                command.ExecuteNonQuery();
            }
        }
    }
}
