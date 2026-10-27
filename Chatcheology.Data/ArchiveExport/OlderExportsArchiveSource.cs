using System.Globalization;
using Chatcheology.Core.Models;
using Chatcheology.Data.OlderExports;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.ArchiveExport
{
    /// <summary>
    /// What an archive export needs from the older-exports file (CH-33 3.4), read on one read-only
    /// connection before anything is written.
    /// </summary>
    /// <remarks>
    /// <para>The file is found beside the main workspace by its fixed name
    /// (<see cref="OlderExportsDatabase.FileName"/>); no file means no older exports. It must prove it
    /// is an older-exports file (its marker) or the export is refused.</para>
    /// <para>Every export is read, shown or not, so the counts can be proved: an export's shown part
    /// is every message from its <c>ShownFromSequenceNumber</c> on (all of them when there is no cut),
    /// and nothing of an export that is not shown. The shown exports are put in <c>Position</c>
    /// order.</para>
    /// <para>Attachments: a <c>(file attached)</c> attachment the import resolved by exact name is
    /// Restored, with the referenced name; every other attachment, <c>&lt;Media omitted&gt;</c>
    /// included, is Unmatched. Missing is never used: none of these was reviewed.</para>
    /// </remarks>
    internal sealed class OlderExportsArchiveSource
    {
        /// <summary>The basis type of an older attachment restored by exact file name.</summary>
        internal const string BasisType = "ExportFileAttached";

        /// <summary>The basis statement of an older attachment restored by exact file name. No path, no name.</summary>
        internal const string BasisText =
            "Restored by exact file name: the older export's line names this file as attached, and a file of " +
            "exactly that name was in the export's folder (or, for an export that continues an earlier one, in " +
            "that export's folder). Its SHA-256 was recorded when the export was imported and checked when the " +
            "archive was written.";

        /// <summary>Every export, in <c>Position</c> order.</summary>
        internal required IReadOnlyList<OlderExport> Exports { get; init; }

        /// <summary>Each resolved payload's SHA-256 (lower-case hex) and recorded description, by asset.</summary>
        internal required IReadOnlyDictionary<long, OlderAsset> Assets { get; init; }

        /// <summary>Each resolved payload's recorded copies, in the location reader's order.</summary>
        internal required IReadOnlyDictionary<long, IReadOnlyList<MediaAssetLocation>> Locations { get; init; }

        /// <summary>The recorded root of every media source in the file: the output may sit inside none of them.</summary>
        internal required IReadOnlyList<string> MediaRoots { get; init; }

        /// <summary>The shown exports' shown messages, in timeline order.</summary>
        internal IEnumerable<(OlderExport Export, OlderMessage Message)> ShownTimeline =>
            Exports.Where(e => e.Shown).SelectMany(e => e.ShownMessages.Select(m => (e, m)));

        /// <summary>
        /// Reads the file beside <paramref name="workspacePath"/>, or returns <see langword="null"/> when
        /// there is none.
        /// </summary>
        /// <exception cref="ArchiveExportRefusedException">The file is there but unusable. Nothing was written.</exception>
        internal static OlderExportsArchiveSource? ReadBeside(string workspacePath, CancellationToken cancellationToken)
        {
            var path = OlderExportsDatabase.PathBeside(workspacePath);

            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using var connection = WorkspaceDatabase.OpenReadOnlyConnection(path);

                try
                {
                    OlderExportsDatabase.RequireOlderExportsFile(connection);
                }
                catch (InvalidOperationException exception)
                {
                    throw new ArchiveExportRefusedException(
                        "The older-exports file beside the workspace is not a usable older-exports file. Nothing " +
                        "was written.",
                        exception);
                }

                var exports = ReadExports(connection);
                var messages = ReadMessages(connection, cancellationToken);
                var attachments = ReadAttachments(connection);

                var built = new List<OlderExport>(exports.Count);

                foreach (var export in exports)
                {
                    var own = messages.GetValueOrDefault(export.ConversationID) ?? [];

                    var withAttachments = own
                        .Select(m => m with { Attachments = attachments.GetValueOrDefault(m.MessageID) ?? [] })
                        .ToList();

                    var shown = !export.Shown
                        ? []
                        : withAttachments.Where(m => export.ShownFrom is not { } from || m.SequenceNumber >= from).ToList();

                    built.Add(export with
                    {
                        MessageCount = withAttachments.Count,
                        ShownMessages = shown,
                    });
                }

                var assetIDs = attachments.Values
                    .SelectMany(list => list)
                    .Select(a => a.ResolvedAssetID)
                    .OfType<long>()
                    .Distinct()
                    .Order()
                    .ToList();

                var assets = ReadAssets(connection, assetIDs);

                var locations = new MediaAssetLocationReader().Read(connection, assetIDs)
                    .GroupBy(l => l.MediaAssetID)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<MediaAssetLocation>)g.ToList());

                return new OlderExportsArchiveSource
                {
                    Exports = built,
                    Assets = assets,
                    Locations = locations,
                    MediaRoots = MediaRootPaths(connection),
                };
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                throw new ArchiveExportRefusedException(
                    "The older-exports file is busy: another program holds a lock on it. Nothing was written.");
            }
            catch (SqliteException exception)
            {
                throw new ArchiveExportRefusedException(
                    "The older-exports file beside the workspace could not be read. Nothing was written.", exception);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        // -------------------------------------------------------------------------------------
        // Reading. Fixed read-only SELECTs.
        // -------------------------------------------------------------------------------------

        private static List<OlderExport> ReadExports(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT o.ConversationID, o.Position, o.Shown, o.ShownFromSequenceNumber,
                       (SELECT MIN(s.ImportedDateTimeUtc)
                        FROM Message AS m JOIN ImportSource AS s ON s.ImportSourceID = m.ImportSourceID
                        WHERE m.ConversationID = o.ConversationID)
                FROM ExportOrder AS o
                ORDER BY o.Position;
                """;

            var exports = new List<OlderExport>();
            var roles = ReadRoles(connection);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var conversationID = reader.GetInt64(0);

                exports.Add(new OlderExport
                {
                    ConversationID = conversationID,
                    Position = reader.GetInt32(1),
                    Shown = reader.GetInt64(2) == 1,
                    ShownFrom = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    ImportedUtc = reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
                    Roles = roles.GetValueOrDefault(conversationID) ?? new Dictionary<long, OlderExportRole>(),
                    MessageCount = 0,
                    ShownMessages = [],
                });
            }

            return exports;
        }

        private static Dictionary<long, Dictionary<long, OlderExportRole>> ReadRoles(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ConversationID, ParticipantID, Role FROM ExportRole;";

            var roles = new Dictionary<long, Dictionary<long, OlderExportRole>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var role = reader.GetString(2) switch
                {
                    "Local" => OlderExportRole.Local,
                    "Other" => OlderExportRole.Other,
                    _ => throw new ArchiveExportRefusedException(
                        "The older-exports file records a role the export does not know. Nothing was written."),
                };

                var conversation = reader.GetInt64(0);

                if (!roles.TryGetValue(conversation, out var byParticipant))
                {
                    byParticipant = [];
                    roles[conversation] = byParticipant;
                }

                byParticipant[reader.GetInt64(1)] = role;
            }

            return roles;
        }

        private static Dictionary<long, List<OlderMessage>> ReadMessages(
            SqliteConnection connection, CancellationToken cancellationToken)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT m.MessageID, m.ConversationID, m.SequenceNumber, m.MessageDateTimeLocal, m.MessageType,
                       m.SenderParticipantID, m.MessageContent
                FROM Message AS m
                WHERE m.ConversationID IN (SELECT ConversationID FROM ExportOrder)
                ORDER BY m.ConversationID, m.SequenceNumber;
                """;

            var messages = new Dictionary<long, List<OlderMessage>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var message = new OlderMessage
                {
                    MessageID = reader.GetInt64(0),
                    SequenceNumber = reader.GetInt32(2),
                    DateTimeLocal = ParseLocal(reader.GetString(3)),
                    Type = reader.GetString(4) switch
                    {
                        "User" => MessageType.User,
                        "System" => MessageType.System,
                        _ => throw new ArchiveExportRefusedException(
                            "An older-export message has a type the export does not know. Nothing was written."),
                    },
                    SenderParticipantID = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    Content = reader.GetString(6),
                    Attachments = [],
                };

                var conversation = reader.GetInt64(1);

                if (!messages.TryGetValue(conversation, out var list))
                {
                    list = [];
                    messages[conversation] = list;
                }

                list.Add(message);
            }

            return messages;
        }

        private static Dictionary<long, List<OlderAttachment>> ReadAttachments(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT a.AttachmentID, a.MessageID, a.Ordinal, a.ResolutionStatus, a.ResolvedMediaAssetID, n.FileName
                FROM Attachment AS a
                JOIN Message AS m ON m.MessageID = a.MessageID
                LEFT JOIN ExportAttachmentName AS n ON n.AttachmentID = a.AttachmentID
                WHERE m.ConversationID IN (SELECT ConversationID FROM ExportOrder)
                ORDER BY a.MessageID, a.Ordinal, a.AttachmentID;
                """;

            var attachments = new Dictionary<long, List<OlderAttachment>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var resolved = reader.GetString(3) == "Resolved";
                long? assetID = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                var name = reader.IsDBNull(5) ? null : reader.GetString(5);

                // Only a (file attached) attachment can have been resolved, and only by its name.
                if (resolved != assetID.HasValue || (resolved && name is null))
                {
                    throw new ArchiveExportRefusedException(
                        "An older-export attachment is recorded as resolved without both an asset and an exact " +
                        "file name. Nothing was written.");
                }

                var attachment = new OlderAttachment
                {
                    AttachmentID = reader.GetInt64(0),
                    Ordinal = reader.GetInt32(2),
                    ResolvedAssetID = assetID,
                    FileName = name,
                };

                var message = reader.GetInt64(1);

                if (!attachments.TryGetValue(message, out var list))
                {
                    list = [];
                    attachments[message] = list;
                }

                list.Add(attachment);
            }

            return attachments;
        }

        private static Dictionary<long, OlderAsset> ReadAssets(SqliteConnection connection, IReadOnlyList<long> assetIDs)
        {
            var assets = new Dictionary<long, OlderAsset>();

            if (assetIDs.Count == 0)
            {
                return assets;
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT MediaAssetID, SHA256, MediaType, SizeBytes, DurationMS, Width, Height
                FROM MediaAsset
                WHERE MediaAssetID IN (SELECT value FROM json_each($ids));
                """;
            command.Parameters.AddWithValue(
                "$ids", "[" + string.Join(",", assetIDs.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]");

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var sha = reader.GetString(1).ToLowerInvariant();

                if (!Archive.ArchiveFormat.IsSha256(sha))
                {
                    throw new ArchiveExportRefusedException(
                        "An older-export payload's recorded hash is not a SHA-256. Nothing was written.");
                }

                assets[reader.GetInt64(0)] = new OlderAsset(
                    sha,
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6));
            }

            if (assets.Count != assetIDs.Count)
            {
                throw new ArchiveExportRefusedException(
                    "An older-export attachment names a payload the older-exports file does not record. Nothing " +
                    "was written.");
            }

            return assets;
        }

        private static List<string> MediaRootPaths(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT RootPath FROM MediaSource ORDER BY MediaSourceID;";

            var roots = new List<string>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                roots.Add(reader.GetString(0));
            }

            return roots;
        }

        private static DateTime ParseLocal(string text) =>
            WorkspaceDateFormats.TryParseMessageDateTimeLocal(text, out var value)
                ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
                : throw new ArchiveExportRefusedException(
                    "An older-export message time is not in the workspace's local date-time form. Nothing was written.");

        private static DateTime ParseUtc(string text)
        {
            if (!DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var value))
            {
                throw new ArchiveExportRefusedException(
                    "An older export's import time is not a UTC time. Nothing was written.");
            }

            var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);

            return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }
    }

    /// <summary>One older export as the archive export reads it.</summary>
    internal sealed record OlderExport
    {
        internal required long ConversationID { get; init; }

        internal required int Position { get; init; }

        internal required bool Shown { get; init; }

        /// <summary>The cut: the first shown sequence number, or <see langword="null"/> for none.</summary>
        internal required int? ShownFrom { get; init; }

        /// <summary>When the export was imported, UTC to the second: the time its exact-name restorations were recorded.</summary>
        internal required DateTime? ImportedUtc { get; init; }

        /// <summary>The role of each of its participants.</summary>
        internal required IReadOnlyDictionary<long, OlderExportRole> Roles { get; init; }

        /// <summary>Every message of the export.</summary>
        internal required int MessageCount { get; init; }

        /// <summary>The shown messages, in sequence order. Empty when the export is not shown.</summary>
        internal required IReadOnlyList<OlderMessage> ShownMessages { get; init; }

        internal int HiddenCount => MessageCount - ShownMessages.Count;
    }

    /// <summary>One message of an older export.</summary>
    internal sealed record OlderMessage
    {
        internal required long MessageID { get; init; }

        internal required int SequenceNumber { get; init; }

        internal required DateTime DateTimeLocal { get; init; }

        internal required MessageType Type { get; init; }

        internal required long? SenderParticipantID { get; init; }

        internal required string Content { get; init; }

        internal required IReadOnlyList<OlderAttachment> Attachments { get; init; }
    }

    /// <summary>One attachment of an older-export message.</summary>
    internal sealed record OlderAttachment
    {
        internal required long AttachmentID { get; init; }

        internal required int Ordinal { get; init; }

        /// <summary>The asset the import resolved it to by exact name, or <see langword="null"/>.</summary>
        internal required long? ResolvedAssetID { get; init; }

        /// <summary>The name its <c>(file attached)</c> line gives, or <see langword="null"/>.</summary>
        internal required string? FileName { get; init; }
    }

    /// <summary>One resolved older payload as the older-exports file records it.</summary>
    internal sealed record OlderAsset(
        string Sha256, string MediaType, long SizeBytes, long? DurationMS, int? Width, int? Height);
}
