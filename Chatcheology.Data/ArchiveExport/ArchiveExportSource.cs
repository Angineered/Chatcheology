using System.Globalization;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.ArchiveExport
{
    /// <summary>
    /// Everything an archive export needs from one workspace, read on one read-only connection before
    /// anything is written.
    /// </summary>
    /// <remarks>
    /// <para><strong>Public readers only.</strong> Messages and participants come from
    /// <see cref="ConversationReader"/>, attachment states from
    /// <see cref="Phase7ReconstructionReader"/>, each restored attachment's basis from
    /// <see cref="Phase7ResolutionHistoryReader"/>, attachments' forwarded facts and low-quality links
    /// from <see cref="MsgstoreFactReader"/>, messages' forwarded facts from
    /// <see cref="MessageMsgstoreFactReader"/>, and the recorded copies of each payload from
    /// <see cref="MediaAssetLocationReader"/>. Nothing here forms a second opinion about any of
    /// them.</para>
    /// <para>Two small read-only queries of its own, because no reader exposes these on purpose:
    /// <c>MediaAsset.SHA256</c> of the resolved payloads (the same query the CH-14 backup uses), and
    /// the import source's recorded export hash and time zone.</para>
    /// </remarks>
    internal sealed class ArchiveExportSource
    {
        private const int ChunkSize = 500;

        internal required string ConversationTitle { get; init; }

        /// <summary>Ordered by participant identifier.</summary>
        internal required IReadOnlyList<ConversationParticipantSummary> Participants { get; init; }

        /// <summary>In sequence order.</summary>
        internal required IReadOnlyList<ConversationMessage> Messages { get; init; }

        internal required IReadOnlyDictionary<long, Phase7ReconstructionState> States { get; init; }

        /// <summary>Each resolved payload's SHA-256, lower-case hex, by asset.</summary>
        internal required IReadOnlyDictionary<long, string> AssetSha256 { get; init; }

        /// <summary>Each resolved payload's recorded copies, in the location reader's order.</summary>
        internal required IReadOnlyDictionary<long, IReadOnlyList<MediaAssetLocation>> Locations { get; init; }

        /// <summary>Each restored attachment's current confirmation event.</summary>
        internal required IReadOnlyDictionary<long, Phase7ResolutionHistoryEntry> Basis { get; init; }

        internal required MsgstoreFactSet Facts { get; init; }

        /// <summary>
        /// Each message's recorded msgstore fact (CH-22), for the messages that have one. A message
        /// with no row has no known forwarded status.
        /// </summary>
        internal required IReadOnlyDictionary<long, MessageMsgstoreFact> MessageFacts { get; init; }

        /// <summary>The recorded export hash, lower-case hex, or empty when none was recorded.</summary>
        internal required string SourceExportSha256 { get; init; }

        /// <summary>The recorded source time zone, or empty.</summary>
        internal required string TimeZoneID { get; init; }

        /// <summary>
        /// The recorded root of every media source in the workspace, whether or not any copy under it
        /// is used: the output may sit inside none of them.
        /// </summary>
        internal required IReadOnlyList<string> MediaRoots { get; init; }

        internal int Resolved => States.Values.Count(s => s.Status == Phase7ResolutionStatus.Resolved);

        internal int MissingReviewed => States.Values.Count(IsMissing);

        internal int Unmatched => States.Count - Resolved - MissingReviewed;

        /// <summary>
        /// Unresolved with a current "reviewed, insufficient evidence" review, whatever headline
        /// reason the reconstruction reader chose.
        /// </summary>
        internal static bool IsMissing(Phase7ReconstructionState state) =>
            state.Status == Phase7ResolutionStatus.Unresolved && state.HasCurrentReviewedInsufficientEvidence;

        /// <exception cref="ArchiveExportRefusedException">
        /// The workspace, conversation or participant is unsuitable. Nothing was written.
        /// </exception>
        internal static ArchiveExportSource Read(
            ArchiveExportRequest request, int pageSize, IProgress<ArchiveExportProgress>? progress)
        {
            if (!File.Exists(request.WorkspacePath))
            {
                throw new ArchiveExportRefusedException("There is no file at the WorkspacePath. Nothing was written.");
            }

            try
            {
                using var connection = WorkspaceDatabase.OpenReadOnlyConnection(request.WorkspacePath);

                var version = WorkspaceDatabase.ReadSchemaVersion(connection);

                if (version != WorkspaceDatabase.SchemaVersion)
                {
                    throw new ArchiveExportRefusedException(
                        $"The workspace at WorkspacePath reports schema version {Text(version)}; the export " +
                        $"needs version {Text(WorkspaceDatabase.SchemaVersion)}. Nothing was written.");
                }

                var conversationReader = new ConversationReader();

                var summary = conversationReader.ReadConversations(connection)
                    .SingleOrDefault(c => c.ConversationID == request.ConversationID)
                    ?? throw new ArchiveExportRefusedException(
                        "The ConversationID names no conversation in this workspace. Nothing was written.");

                if (summary.Participants.All(p => p.ParticipantID != request.LocalParticipantID))
                {
                    throw new ArchiveExportRefusedException(
                        "The LocalParticipantID is not a participant of the conversation. Nothing was written.");
                }

                var messages = ReadMessages(connection, conversationReader, summary, pageSize, progress);

                if (messages.Count != summary.MessageCount)
                {
                    throw new ArchiveExportRefusedException(
                        "The conversation's messages could not be read as one consistent set (is something " +
                        "writing to the workspace?). Nothing was written.");
                }

                var attachmentIDs = messages.SelectMany(m => m.Attachments).Select(a => a.AttachmentID).ToList();
                var states = ReadStates(connection, attachmentIDs);

                var resolved = states.Values
                    .Where(s => s.Status == Phase7ResolutionStatus.Resolved)
                    .OrderBy(s => s.AttachmentID)
                    .ToList();

                var assetIDs = resolved.Select(s => s.ResolvedAsset!.MediaAssetID).Distinct().Order().ToList();
                var hashes = AssetHashes(connection, request.ConversationID);

                if (hashes.Count != assetIDs.Count
                    || assetIDs.Any(id => !hashes.ContainsKey(id))
                    || hashes.Values.Any(hash => !Archive.ArchiveFormat.IsSha256(hash)))
                {
                    throw new ArchiveExportRefusedException(
                        "The resolved assets read through Phase 7 and the assets' recorded hashes do not line " +
                        "up. Nothing was written.");
                }

                var locations = new MediaAssetLocationReader().Read(connection, assetIDs)
                    .GroupBy(l => l.MediaAssetID)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<MediaAssetLocation>)g.ToList());

                var basis = ReadBasis(connection, resolved);
                var facts = new MsgstoreFactReader().Read(connection, attachmentIDs);

                var messageFacts = new MessageMsgstoreFactReader()
                    .Read(connection, messages.Select(m => m.MessageID).ToList());
                var (exportSha, timeZone) = ImportSource(connection, request.ConversationID);

                return new ArchiveExportSource
                {
                    ConversationTitle = summary.Title,
                    Participants = summary.Participants.OrderBy(p => p.ParticipantID).ToList(),
                    Messages = messages,
                    States = states,
                    AssetSha256 = hashes,
                    Locations = locations,
                    Basis = basis,
                    Facts = facts,
                    MessageFacts = messageFacts,
                    SourceExportSha256 = exportSha,
                    TimeZoneID = timeZone,
                    MediaRoots = MediaRootPaths(connection),
                };
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                throw new ArchiveExportRefusedException(
                    "The workspace is busy: another program holds a lock on it. Nothing was written.");
            }
            catch (InvalidOperationException exception)
            {
                // A reader refused the workspace's contents (an impossible history, an unknown stored
                // value). Its message stays on the inner exception; it carries no path.
                throw new ArchiveExportRefusedException(
                    "The workspace could not be read consistently: a reader refused its contents. Nothing " +
                    "was written.",
                    exception);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        private static List<ConversationMessage> ReadMessages(
            SqliteConnection connection,
            ConversationReader reader,
            ConversationSummary summary,
            int pageSize,
            IProgress<ArchiveExportProgress>? progress)
        {
            var messages = new List<ConversationMessage>(summary.MessageCount);
            var page = reader.ReadFirstPage(connection, summary.ConversationID, pageSize);

            while (true)
            {
                messages.AddRange(page.Messages);
                progress?.Report(new ArchiveExportProgress(
                    ArchiveExportStage.ReadingMessages, messages.Count, summary.MessageCount));

                if (!page.HasLater || page.LastSequenceNumber is not { } last)
                {
                    return messages;
                }

                page = reader.ReadPageFrom(connection, summary.ConversationID, last + 1, pageSize);
            }
        }

        private static Dictionary<long, Phase7ReconstructionState> ReadStates(
            SqliteConnection connection, List<long> attachmentIDs)
        {
            var reader = new Phase7ReconstructionReader();
            var states = new Dictionary<long, Phase7ReconstructionState>(attachmentIDs.Count);

            for (var offset = 0; offset < attachmentIDs.Count; offset += ChunkSize)
            {
                foreach (var state in reader.Read(connection, attachmentIDs.Skip(offset).Take(ChunkSize).ToList()))
                {
                    states.Add(state.AttachmentID, state);
                }
            }

            return states;
        }

        /// <summary>
        /// Each restored attachment's current confirmation: the event its history replays to, which
        /// must be a confirmation of the very asset the projection records.
        /// </summary>
        private static Dictionary<long, Phase7ResolutionHistoryEntry> ReadBasis(
            SqliteConnection connection, IReadOnlyList<Phase7ReconstructionState> resolved)
        {
            var reader = new Phase7ResolutionHistoryReader();
            var basis = new Dictionary<long, Phase7ResolutionHistoryEntry>(resolved.Count);

            foreach (var state in resolved)
            {
                var projection = reader.ReplayProjection(connection, state.AttachmentID);

                var current = projection.CurrentEffectiveEventID is { } eventID
                    ? reader.ReadHistory(connection, state.AttachmentID)
                        .SingleOrDefault(e => e.AttachmentResolutionEventID == eventID)
                    : null;

                if (projection.Status != Phase7ResolutionStatus.Resolved
                    || projection.ResolvedMediaAssetID != state.ResolvedAsset!.MediaAssetID
                    || current is null
                    || current.EventType == Phase7ResolutionEventType.Revoke
                    || current.MediaAssetID != state.ResolvedAsset.MediaAssetID
                    || current.IndependentBasisType is null
                    || string.IsNullOrEmpty(current.IndependentBasisDescription))
                {
                    throw new ArchiveExportRefusedException(
                        "A resolved attachment's resolution history does not replay to a confirmation of " +
                        "its recorded asset, so there is no basis line to write. Run the workspace " +
                        "verifier. Nothing was written.");
                }

                basis.Add(state.AttachmentID, current);
            }

            return basis;
        }

        /// <summary>
        /// The one read-only query for <c>MediaAsset.SHA256</c> (Phase 7's descriptor leaves the hash
        /// out on purpose): every asset an attachment of the conversation is resolved to.
        /// </summary>
        private static Dictionary<long, string> AssetHashes(SqliteConnection connection, long conversationID)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT DISTINCT ma.MediaAssetID, ma.SHA256
                FROM Attachment AS a
                INNER JOIN Message AS m ON m.MessageID = a.MessageID
                INNER JOIN MediaAsset AS ma ON ma.MediaAssetID = a.ResolvedMediaAssetID
                WHERE m.ConversationID = $c;
                """;
            command.Parameters.AddWithValue("$c", conversationID);

            var hashes = new Dictionary<long, string>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                hashes[reader.GetInt64(0)] = reader.GetString(1).ToLowerInvariant();
            }

            return hashes;
        }

        /// <summary>
        /// The import source the conversation's messages came from: its recorded export hash and time
        /// zone. A conversation drawn from several imports has no single source export, and is refused.
        /// </summary>
        private static (string Sha256, string TimeZoneID) ImportSource(
            SqliteConnection connection, long conversationID)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT DISTINCT s.ImportSourceID, s.SHA256, s.SourceTimeZoneID
                FROM Message AS m
                INNER JOIN ImportSource AS s ON s.ImportSourceID = m.ImportSourceID
                WHERE m.ConversationID = $c;
                """;
            command.Parameters.AddWithValue("$c", conversationID);

            var rows = new List<(string, string)>();

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add((
                        reader.IsDBNull(1) ? "" : reader.GetString(1).Trim().ToLowerInvariant(),
                        reader.IsDBNull(2) ? "" : reader.GetString(2)));
                }
            }

            if (rows.Count > 1)
            {
                throw new ArchiveExportRefusedException(
                    $"The conversation's messages come from {Text(rows.Count)} import sources; an archive " +
                    "records exactly one source export. Nothing was written.");
            }

            var (sha, timeZone) = rows.Count == 1 ? rows[0] : ("", "");

            if (sha.Length != 0 && !Archive.ArchiveFormat.IsSha256(sha))
            {
                throw new ArchiveExportRefusedException(
                    "The import source's recorded export hash is not a SHA-256. Nothing was written.");
            }

            return (sha, timeZone);
        }

        /// <summary>Every media source's recorded root. One read-only statement.</summary>
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

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
