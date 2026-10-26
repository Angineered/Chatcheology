using System.Globalization;
using Chatcheology.Core.Models;
using Chatcheology.Data.Media;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// Imports one older WhatsApp export into the older-exports file as one conversation.
    /// </summary>
    /// <remarks>
    /// One call is one transaction. The import source, conversation, participants and messages are
    /// written by the existing <see cref="WorkspaceImporter"/> code, inside the same transaction as
    /// the rows this importer adds: the sender roles, the timeline order, and the attachments of
    /// <c>(file attached)</c> messages with their names and any exact-name media. A failure
    /// anywhere leaves no row behind.
    /// <para>
    /// Everything that can be refused without the database — the sender count, the roles, the
    /// position, the folders — is refused before the file is opened. What needs the database — a
    /// duplicate chat file, a taken position, a period overlapping a shown export — is refused
    /// inside the transaction before anything is written.
    /// </para>
    /// <para>
    /// Media resolution follows the approved rule and nothing more. A <c>(file attached)</c> name
    /// resolves only to a file of exactly that name, compared ordinally (so case matters even on a
    /// file system that ignores it), directly in the export's own folder, or, for the export given a
    /// <see cref="OlderExportImportRequest.FallbackMediaFolder"/>, directly in that folder. A resolved
    /// file is referenced, not copied: a <c>MediaSource</c> for the folder, a <c>MediaFile</c> with
    /// its name, size and SHA-256, and the <c>MediaAsset</c> for those bytes. Source files are only
    /// read. Every other attachment, including every <c>&lt;Media omitted&gt;</c> one, is
    /// <c>Unresolved</c>.
    /// </para>
    /// <para>
    /// Exception messages carry counts and reasons only: no sender name, message text, file name or
    /// path.
    /// </para>
    /// </remarks>
    public sealed class OlderExportImporter
    {
        /// <summary>The <c>ImportSource.SourceType</c> of every older export.</summary>
        public const string ImportSourceType = "WhatsAppAndroidOlderExport";

        /// <summary>The <c>MediaSource.SourceType</c> of an export folder used for media.</summary>
        public const string MediaSourceType = "WhatsAppExportFolder";

        /// <summary>The number of distinct sender names an older export must have.</summary>
        public const int RequiredSenderCount = 2;

        /// <summary>
        /// The stored <c>Attachment.ResolutionStatus</c> of an attachment linked to an asset. Schema
        /// vocabulary, matching the table's <c>CHECK</c>.
        /// </summary>
        private const string ResolvedAttachmentStatus = "Resolved";

        /// <summary>
        /// Imports <paramref name="request"/> into the older-exports file at
        /// <paramref name="databasePath"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The request is unusable: no messages, not exactly two senders, roles that do not name
        /// exactly those senders with one <c>Local</c> and one <c>Other</c>, a position below 1, a
        /// non-UTC import time, or a fallback folder that is missing or is the export's own folder.
        /// Nothing has been opened.
        /// </exception>
        /// <exception cref="FileNotFoundException">There is no older-exports file at the path.</exception>
        /// <exception cref="InvalidOperationException">
        /// The file is not an older-exports file, the same chat file (by SHA-256) is already
        /// imported, the position is taken, or the export is to be shown and its period overlaps a
        /// shown export. Nothing has been written.
        /// </exception>
        /// <exception cref="SqliteException">
        /// The database rejected a row. The whole import is rolled back.
        /// </exception>
        public OlderExportImportResult Import(string databasePath, OlderExportImportRequest request)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ArgumentNullException.ThrowIfNull(request);

            ValidateRequest(request);

            var export = request.Export;
            var exportFolder = MediaSourcePath.Normalise(export.FolderPath);
            var fallbackFolder = request.FallbackMediaFolder is null
                ? null
                : MediaSourcePath.Normalise(request.FallbackMediaFolder);

            using var connection = OlderExportsDatabase.OpenConnection(databasePath);
            using var transaction = connection.BeginTransaction();

            RefuseDuplicate(connection, transaction, export.SHA256);
            RefuseTakenPosition(connection, transaction, request.Position);

            if (request.Shown)
            {
                RefuseOverlapWithShownExports(connection, transaction, export.Messages);
            }

            var rows = WorkspaceImporter.ImportWithin(
                connection,
                transaction,
                new WorkspaceImportRequest
                {
                    SourceType = ImportSourceType,
                    SourceDisplayName = $"Older export {request.Position.ToString(CultureInfo.InvariantCulture)}",
                    OriginalFileName = export.ChatFileName,
                    SHA256 = export.SHA256,
                    ImportedDateTimeUtc = request.ImportedDateTimeUtc,
                    SourceTimeZoneID = request.SourceTimeZoneID,
                    ConversationTitle = $"Older export {request.Position.ToString(CultureInfo.InvariantCulture)}",
                    Messages = export.Messages,
                });

            var conversationID = rows.Result.ConversationID;

            InsertRoles(connection, transaction, conversationID, rows.ParticipantIDsBySender, request.Roles);
            InsertOrder(connection, transaction, conversationID, request.Position, request.Shown);

            var media = new MediaWriter(connection, transaction, request.ImportedDateTimeUtc);
            var fallbackNames = fallbackFolder is null
                ? null
                : new HashSet<string>(OlderExportSource.ListFileNames(fallbackFolder), StringComparer.Ordinal);

            int fileAttached = 0, ownFolder = 0, fallback = 0, unresolved = 0, mediaOmitted = 0;

            foreach (var message in export.Messages)
            {
                if (message.IsMediaPlaceholder)
                {
                    // Written by the workspace importer, unresolved, exactly as for the main workspace.
                    mediaOmitted++;
                    continue;
                }

                var name = message.AttachedFileName;

                if (name is null)
                {
                    continue;
                }

                fileAttached++;

                long? assetID = null;

                if (export.MediaFileNames.Contains(name))
                {
                    assetID = media.Resolve(exportFolder, name);
                    ownFolder++;
                }
                else if (fallbackFolder is not null && fallbackNames!.Contains(name))
                {
                    assetID = media.Resolve(fallbackFolder, name);
                    fallback++;
                }
                else
                {
                    unresolved++;
                }

                var attachmentID = InsertAttachment(
                    connection,
                    transaction,
                    rows.MessageIDsBySequenceNumber[message.SequenceNumber],
                    assetID);

                InsertAttachmentName(connection, transaction, attachmentID, name);
            }

            transaction.Commit();

            return new OlderExportImportResult
            {
                ImportSourceID = rows.Result.ImportSourceID,
                ConversationID = conversationID,
                MessageCount = rows.Result.MessageCount,
                MediaOmittedAttachmentCount = mediaOmitted,
                FileAttachedAttachmentCount = fileAttached,
                ResolvedInOwnFolderCount = ownFolder,
                ResolvedInFallbackFolderCount = fallback,
                UnresolvedFileAttachedCount = unresolved,
            };
        }

        /// <summary>
        /// The distinct user-message sender names of <paramref name="messages"/>, compared
        /// ordinally, as the workspace importer distinguishes participants.
        /// </summary>
        public static IReadOnlySet<string> DistinctSenders(IReadOnlyList<ParsedMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);

            var senders = new HashSet<string>(StringComparer.Ordinal);

            foreach (var message in messages)
            {
                if (message.MessageType == MessageType.User && message.Sender is not null)
                {
                    senders.Add(message.Sender);
                }
            }

            return senders;
        }

        private static void ValidateRequest(OlderExportImportRequest request)
        {
            ArgumentNullException.ThrowIfNull(request.Export, nameof(request.Export));
            ArgumentNullException.ThrowIfNull(request.Roles, nameof(request.Roles));

            if (request.Export.Messages.Count == 0)
            {
                throw new ArgumentException(
                    "The export contains no messages, so there is nothing to import.", nameof(request));
            }

            if (request.ImportedDateTimeUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "ImportedDateTimeUtc must be DateTimeKind.Utc.", nameof(request));
            }

            if (request.Position < 1)
            {
                throw new ArgumentException("Position must be 1 or more.", nameof(request));
            }

            var senders = DistinctSenders(request.Export.Messages);

            if (senders.Count != RequiredSenderCount)
            {
                throw new ArgumentException(
                    $"The export has {senders.Count} distinct sender names; exactly " +
                    $"{RequiredSenderCount} are required, each given a role. Nothing has been written.",
                    nameof(request));
            }

            var unknownNames = request.Roles.Keys.Count(name => name is null || !senders.Contains(name));
            var missingRoles = senders.Count(name => !request.Roles.ContainsKey(name));

            if (unknownNames > 0 || missingRoles > 0)
            {
                throw new ArgumentException(
                    $"The roles do not match the export's senders: {missingRoles} sender name(s) " +
                    $"without a role and {unknownNames} role(s) for a name the export does not " +
                    $"contain. Names are compared exactly. Nothing has been written.",
                    nameof(request));
            }

            var roleValues = senders.Select(name => request.Roles[name]).ToList();

            if (roleValues.Any(role => !Enum.IsDefined(role))
                || roleValues.Count(role => role == OlderExportRole.Local) != 1
                || roleValues.Count(role => role == OlderExportRole.Other) != 1)
            {
                throw new ArgumentException(
                    "The two senders must be given one Local role and one Other role. Nothing has " +
                    "been written.",
                    nameof(request));
            }

            if (request.FallbackMediaFolder is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(
                    request.FallbackMediaFolder, nameof(request.FallbackMediaFolder));

                var fallback = MediaSourcePath.Normalise(request.FallbackMediaFolder);

                if (!Directory.Exists(fallback))
                {
                    throw new ArgumentException(
                        "The fallback media folder does not exist. Nothing has been written.",
                        nameof(request));
                }

                if (string.Equals(
                        fallback,
                        MediaSourcePath.Normalise(request.Export.FolderPath),
                        MediaSourcePath.PathComparison))
                {
                    throw new ArgumentException(
                        "The fallback media folder is the export's own folder. Nothing has been " +
                        "written.",
                        nameof(request));
                }
            }
        }

        private static void RefuseDuplicate(
            SqliteConnection connection, SqliteTransaction transaction, string sha256)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT COUNT(*) FROM ImportSource WHERE SHA256 = $sha256 COLLATE NOCASE;";
            command.Parameters.AddWithValue("$sha256", sha256);

            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException(
                    "This chat file (same SHA-256) has already been imported into the older-exports " +
                    "file. It is not imported twice. Nothing has been written.");
            }
        }

        private static void RefuseTakenPosition(
            SqliteConnection connection, SqliteTransaction transaction, int position)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM ExportOrder WHERE Position = $position;";
            command.Parameters.AddWithValue("$position", position);

            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException(
                    $"Position {position} is already taken by another older export. Nothing has " +
                    $"been written.");
            }
        }

        /// <summary>
        /// Refuses a shown export whose period, first to last message timestamp inclusive, meets
        /// the period of any export already registered as shown.
        /// </summary>
        /// <remarks>
        /// Inclusive at both ends: with minute-precision timestamps, two exports sharing a minute
        /// cannot be put in order without a guess. Compared on the stored local wall-clock text,
        /// whose fixed-width format sorts in time order.
        /// </remarks>
        private static void RefuseOverlapWithShownExports(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyList<ParsedMessage> messages)
        {
            var first = WorkspaceDateFormats.FormatMessageDateTimeLocal(
                messages.Min(message => message.MessageDateTime));
            var last = WorkspaceDateFormats.FormatMessageDateTimeLocal(
                messages.Max(message => message.MessageDateTime));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM (
                    SELECT MIN(m.MessageDateTimeLocal) AS FirstLocal,
                           MAX(m.MessageDateTimeLocal) AS LastLocal
                    FROM ExportOrder AS o
                    JOIN Message AS m ON m.ConversationID = o.ConversationID
                    WHERE o.Shown = 1
                    GROUP BY o.ConversationID
                )
                WHERE FirstLocal <= $last AND $first <= LastLocal;
                """;
            command.Parameters.AddWithValue("$first", first);
            command.Parameters.AddWithValue("$last", last);

            var overlapping = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

            if (overlapping > 0)
            {
                throw new InvalidOperationException(
                    $"The export's period overlaps {overlapping} export(s) already registered as " +
                    $"shown. Shown exports must not overlap; register this one as not shown to keep " +
                    $"it as evidence. Nothing has been written.");
            }
        }

        private static void InsertRoles(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long conversationID,
            IReadOnlyDictionary<string, long> participantIDsBySender,
            IReadOnlyDictionary<string, OlderExportRole> roles)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO ExportRole (ConversationID, ParticipantID, Role)
                VALUES ($conversationID, $participantID, $role);
                """;
            command.Parameters.AddWithValue("$conversationID", conversationID);
            var participantID = command.Parameters.Add("$participantID", SqliteType.Integer);
            var role = command.Parameters.Add("$role", SqliteType.Text);

            foreach (var (sender, id) in participantIDsBySender)
            {
                participantID.Value = id;
                role.Value = roles[sender].ToString();
                command.ExecuteNonQuery();
            }
        }

        private static void InsertOrder(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long conversationID,
            int position,
            bool shown)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO ExportOrder (ConversationID, Position, Shown)
                VALUES ($conversationID, $position, $shown);
                """;
            command.Parameters.AddWithValue("$conversationID", conversationID);
            command.Parameters.AddWithValue("$position", position);
            command.Parameters.AddWithValue("$shown", shown ? 1 : 0);
            command.ExecuteNonQuery();
        }

        private static long InsertAttachment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long messageID,
            long? resolvedMediaAssetID)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"""
                INSERT INTO Attachment (
                    MessageID,
                    Ordinal,
                    ExpectedMediaType,
                    ResolutionStatus,
                    ResolvedMediaAssetID)
                VALUES (
                    $messageID,
                    {WorkspaceDatabase.MediaPlaceholderAttachmentOrdinal},
                    NULL,
                    $status,
                    $assetID)
                RETURNING AttachmentID;
                """;
            command.Parameters.AddWithValue("$messageID", messageID);
            command.Parameters.AddWithValue(
                "$status",
                resolvedMediaAssetID is null
                    ? WorkspaceDatabase.UnresolvedAttachmentStatus
                    : ResolvedAttachmentStatus);
            command.Parameters.AddWithValue("$assetID", (object?)resolvedMediaAssetID ?? DBNull.Value);

            return (long)command.ExecuteScalar()!;
        }

        private static void InsertAttachmentName(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long attachmentID,
            string fileName)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO ExportAttachmentName (AttachmentID, FileName)
                VALUES ($attachmentID, $fileName);
                """;
            command.Parameters.AddWithValue("$attachmentID", attachmentID);
            command.Parameters.AddWithValue("$fileName", fileName);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Records resolved media files inside the import's transaction, reusing a folder's
        /// <c>MediaSource</c>, a file's <c>MediaFile</c> and a payload's <c>MediaAsset</c> when they
        /// already exist, so a folder used by two imports or a file named by two messages is still
        /// one row.
        /// </summary>
        private sealed class MediaWriter(
            SqliteConnection connection,
            SqliteTransaction transaction,
            DateTime importedDateTimeUtc)
        {
            private readonly Dictionary<string, long> _sourceIDsByFolder = new(StringComparer.Ordinal);
            private readonly Dictionary<(long, string), long> _assetIDsByFile = [];

            /// <summary>
            /// Returns the asset of the file named <paramref name="fileName"/> directly in
            /// <paramref name="folder"/>, recording what is not yet recorded.
            /// </summary>
            /// <remarks>
            /// <paramref name="fileName"/> is a name the folder listing itself returned, so joining
            /// it to the folder cannot reach outside the folder.
            /// </remarks>
            internal long Resolve(string folder, string fileName)
            {
                var sourceID = GetOrCreateSource(folder);

                if (_assetIDsByFile.TryGetValue((sourceID, fileName), out var cached))
                {
                    return cached;
                }

                var (sha256, sizeBytes) = HashFile(Path.Combine(folder, fileName));

                long assetID;
                var existing = FindMediaFile(sourceID, fileName);

                if (existing is { } found)
                {
                    // Recorded by an earlier import from the same folder. It must still be the
                    // same bytes; a file that changed since is not silently re-pointed.
                    if (!string.Equals(found.SHA256, sha256, StringComparison.OrdinalIgnoreCase)
                        || found.AssetID is null)
                    {
                        throw new InvalidOperationException(
                            "A media file already recorded for this folder no longer has the bytes " +
                            "it was recorded with. Nothing has been written.");
                    }

                    assetID = found.AssetID.Value;
                }
                else
                {
                    var extension = MediaClassification.NormaliseExtension(fileName);
                    var mediaType = MediaTypeText.Format(MediaClassification.Classify(extension, sizeBytes));

                    assetID = GetOrCreateAsset(sha256, mediaType, sizeBytes);
                    var mediaFileID = InsertMediaFile(sourceID, fileName, extension, sizeBytes, sha256, mediaType);
                    InsertAssetFile(assetID, mediaFileID);
                }

                _assetIDsByFile.Add((sourceID, fileName), assetID);

                return assetID;
            }

            private long GetOrCreateSource(string folder)
            {
                if (_sourceIDsByFolder.TryGetValue(folder, out var cached))
                {
                    return cached;
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    SELECT MediaSourceID FROM MediaSource
                    WHERE SourceType = $sourceType AND RootPath = $rootPath
                    ORDER BY MediaSourceID
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$sourceType", MediaSourceType);
                command.Parameters.AddWithValue("$rootPath", folder);

                var id = command.ExecuteScalar() is long existing
                    ? existing
                    : InsertSource(folder);

                _sourceIDsByFolder.Add(folder, id);

                return id;
            }

            private long InsertSource(string folder)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO MediaSource (
                        DisplayName, SourceType, RootPath, DeviceDescription, ImportedDateTimeUtc)
                    VALUES ($displayName, $sourceType, $rootPath, NULL, $importedDateTimeUtc)
                    RETURNING MediaSourceID;
                    """;
                command.Parameters.AddWithValue("$displayName", "Older export folder");
                command.Parameters.AddWithValue("$sourceType", MediaSourceType);
                command.Parameters.AddWithValue("$rootPath", folder);
                command.Parameters.AddWithValue(
                    "$importedDateTimeUtc",
                    importedDateTimeUtc.ToString(
                        WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));

                return (long)command.ExecuteScalar()!;
            }

            private (string? SHA256, long? AssetID)? FindMediaFile(long sourceID, string fileName)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    SELECT f.SHA256, af.MediaAssetID
                    FROM MediaFile AS f
                    LEFT JOIN MediaAssetFile AS af ON af.MediaFileID = f.MediaFileID
                    WHERE f.MediaSourceID = $sourceID AND f.RelativePath = $relativePath;
                    """;
                command.Parameters.AddWithValue("$sourceID", sourceID);
                command.Parameters.AddWithValue("$relativePath", fileName);

                using var reader = command.ExecuteReader();

                if (!reader.Read())
                {
                    return null;
                }

                return (
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1));
            }

            /// <remarks>
            /// An asset is identified by its bytes alone. When the same bytes are already an asset
            /// (another file, another folder), that asset is used as it stands.
            /// </remarks>
            private long GetOrCreateAsset(string sha256, string mediaType, long sizeBytes)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT MediaAssetID FROM MediaAsset WHERE SHA256 = $sha256;";
                command.Parameters.AddWithValue("$sha256", sha256);

                if (command.ExecuteScalar() is long existing)
                {
                    return existing;
                }

                command.CommandText =
                    """
                    INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes, DurationMS, Width, Height)
                    VALUES ($sha256, $mediaType, $sizeBytes, NULL, NULL, NULL)
                    RETURNING MediaAssetID;
                    """;
                command.Parameters.AddWithValue("$mediaType", mediaType);
                command.Parameters.AddWithValue("$sizeBytes", sizeBytes);

                return (long)command.ExecuteScalar()!;
            }

            private long InsertMediaFile(
                long sourceID,
                string fileName,
                string? extension,
                long sizeBytes,
                string sha256,
                string mediaType)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO MediaFile (
                        MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256,
                        MediaType, FileDate, IsSent, DurationMS, Width, Height)
                    VALUES (
                        $sourceID, $relativePath, $fileName, $extension, $sizeBytes, $sha256,
                        $mediaType, NULL, NULL, NULL, NULL, NULL)
                    RETURNING MediaFileID;
                    """;
                command.Parameters.AddWithValue("$sourceID", sourceID);

                // Directly in the folder, so the canonical relative path is the name itself.
                command.Parameters.AddWithValue("$relativePath", fileName);
                command.Parameters.AddWithValue("$fileName", fileName);
                command.Parameters.AddWithValue("$extension", (object?)extension ?? DBNull.Value);
                command.Parameters.AddWithValue("$sizeBytes", sizeBytes);
                command.Parameters.AddWithValue("$sha256", sha256);
                command.Parameters.AddWithValue("$mediaType", mediaType);

                return (long)command.ExecuteScalar()!;
            }

            private void InsertAssetFile(long assetID, long mediaFileID)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
                    VALUES ($assetID, $mediaFileID);
                    """;
                command.Parameters.AddWithValue("$assetID", assetID);
                command.Parameters.AddWithValue("$mediaFileID", mediaFileID);
                command.ExecuteNonQuery();
            }

            /// <summary>
            /// Hashes a file opened read-only, sharing reads, and returns the upper-case SHA-256 and
            /// the number of bytes read.
            /// </summary>
            /// <remarks>
            /// A read failure is reported without the framework's message, which carries the path.
            /// </remarks>
            private static (string SHA256, long SizeBytes) HashFile(string path)
            {
                try
                {
                    using var stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.Read);

                    var hash = System.Security.Cryptography.SHA256.HashData(stream);

                    return (Convert.ToHexString(hash), stream.Length);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        $"A media file named by the export could not be read " +
                        $"({exception.GetType().Name}). The import is rolled back; nothing has been " +
                        $"written.");
                }
            }
        }
    }
}
