using System.Globalization;
using System.Security.Cryptography;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

// One test at a time: the tool and these fixtures clear SQLite's global connection pool.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MsgstoreImport.Tests;

/// <summary>
/// A synthetic workspace (through the product's own schema and Phase 7 run) and a synthetic
/// msgstore.db, in a private temporary folder outside the repository.
/// </summary>
/// <remarks>
/// Everything is invented: fictional participants, generated hashes, file names that name nothing
/// real. The workspace rows are inserted the way the Data tests' <c>MatchingTestWorkspace</c> does;
/// the evidence snapshot is the real <see cref="Phase7EvidenceSnapshotService"/>.
/// </remarks>
internal sealed class SyntheticData : IDisposable
{
    internal const long ConversationID = 1;
    internal const long LocalParticipantID = 1;
    internal const long OtherParticipantID = 2;
    internal const long ChatID = 7;
    internal static readonly TimeSpan Offset = TimeSpan.FromHours(2);
    internal const string OffsetText = "+02:00";
    internal static readonly DateOnly Day = new(2026, 3, 2);

    private const string Imported = "2026-08-17T16:00:00.0000000Z";

    private SqliteConnection? _workspace;
    private SqliteConnection? _msgstore;
    private int _sequence;
    private int _file;
    private int _hash;
    private int _token;
    private long _msgstoreMessageID = 100;

    internal SyntheticData()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "MsgstoreImport.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);

        WorkspacePath = Path.Combine(DirectoryPath, "workspace.db");
        MsgstorePath = Path.Combine(DirectoryPath, "msgstore.db");

        WorkspaceDatabase.Initialise(WorkspacePath);
        _workspace = WorkspaceDatabase.OpenConnection(WorkspacePath);

        Workspace(
            $"""
            INSERT INTO ImportSource (SourceType, DisplayName, ImportedDateTimeUtc)
            VALUES ('SyntheticExport', 'Synthetic export', '{Imported}');
            INSERT INTO Conversation (Title, CreatedDateTimeUtc) VALUES ('Alex and Sam', '{Imported}');
            INSERT INTO Participant (DisplayName) VALUES ('Alex');
            INSERT INTO Participant (DisplayName) VALUES ('Sam');
            INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 1);
            INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 2);
            INSERT INTO MediaSource (DisplayName, SourceType, RootPath, ImportedDateTimeUtc)
            VALUES ('Synthetic source', 'SyntheticMediaDirectory', 'MediaRoot/Synthetic', '{Imported}');
            """);

        _msgstore = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = MsgstorePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _msgstore.Open();

        Msgstore(
            $"""
            CREATE TABLE chat (_id INTEGER PRIMARY KEY, subject TEXT);
            CREATE TABLE message (
                _id INTEGER PRIMARY KEY, chat_row_id INTEGER, from_me INTEGER, key_id TEXT,
                timestamp INTEGER, message_type INTEGER, sort_id INTEGER, text_data TEXT);
            CREATE TABLE message_media (
                message_row_id INTEGER PRIMARY KEY, file_path TEXT, file_size INTEGER, file_hash TEXT,
                mime_type TEXT, media_name TEXT);
            INSERT INTO chat (_id, subject) VALUES ({ChatID}, NULL);
            INSERT INTO chat (_id, subject) VALUES (8, NULL);
            """);
    }

    internal string DirectoryPath { get; }
    internal string WorkspacePath { get; }
    internal string MsgstorePath { get; }

    // ---------------------------------------------------------------------------------------
    // Workspace side.
    // ---------------------------------------------------------------------------------------

    /// <summary>A message at <paramref name="time"/> on <see cref="Day"/> with one unresolved attachment.</summary>
    internal long AddAttachment(string time, bool outgoing, DateOnly? date = null)
    {
        AddWorkspaceMessage(time, outgoing, date, media: true);

        Workspace(
            """
            INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
            VALUES ((SELECT MAX(MessageID) FROM Message), 1, 'Unresolved');
            """);

        return WorkspaceScalar("SELECT MAX(AttachmentID) FROM Attachment;");
    }

    /// <summary>A workspace message with no attachment (text), on <paramref name="date"/> or <see cref="Day"/>.</summary>
    internal long AddWorkspaceMessage(string time, bool outgoing, DateOnly? date = null, bool media = false)
    {
        _sequence++;
        var sender = outgoing ? LocalParticipantID : OtherParticipantID;
        var day = date ?? Day;

        Workspace(
            $"""
            INSERT INTO Message (
                ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                SenderParticipantID, MessageType, MessageContent, RawContent, SourceLineStart, SourceLineEnd)
            VALUES ({ConversationID}, 1, {_sequence}, '{day:yyyy-MM-dd}T{time}', {sender}, 'User',
                    '{(media ? "<Media omitted>" : "synthetic text")}', 'raw', {_sequence}, {_sequence});
            """);

        return WorkspaceScalar("SELECT MAX(MessageID) FROM Message;");
    }

    /// <summary>A synthetic asset with one marker-named copy dated <paramref name="fileDate"/>.</summary>
    internal Asset AddAsset(DateOnly? fileDate = null, long size = 2048, bool dated = true, string? fileName = null)
    {
        dated &= fileName is null;
        var date = fileDate ?? Day;
        var bytes = NextHash();
        var hex = Convert.ToHexString(bytes);
        var name = fileName ?? (dated
            ? $"IMG-{date:yyyyMMdd}-WA{(++_token).ToString("D4", CultureInfo.InvariantCulture)}.jpg"
            : $"photo-{(++_token).ToString("D4", CultureInfo.InvariantCulture)}.jpg");
        var storedDate = dated ? $"'{date:yyyy-MM-dd}'" : "NULL";

        Workspace($"INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes) VALUES ('{hex}', 'Image', {size});");
        var asset = WorkspaceScalar("SELECT MAX(MediaAssetID) FROM MediaAsset;");

        _file++;
        Workspace(
            $"""
            INSERT INTO MediaFile (
                MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256, MediaType, FileDate, IsSent)
            VALUES (1, 'folder/{_file}-{name}', '{name}', '.jpg', {size}, '{hex}', 'Image', {storedDate}, NULL);
            INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID) VALUES ({asset}, (SELECT MAX(MediaFileID) FROM MediaFile));
            """);

        return new Asset(asset, name, size, Convert.ToBase64String(bytes));
    }

    /// <summary>One more marker-named copy of an existing asset, dated <paramref name="date"/>.</summary>
    internal void AddCopy(Asset asset, DateOnly date)
    {
        var hex = Convert.ToHexString(Convert.FromBase64String(asset.Base64Hash));
        var name = $"IMG-{date:yyyyMMdd}-WA{(++_token).ToString("D4", CultureInfo.InvariantCulture)}.jpg";

        _file++;
        Workspace(
            $"""
            INSERT INTO MediaFile (
                MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256, MediaType, FileDate, IsSent)
            VALUES (1, 'folder/{_file}-{name}', '{name}', '.jpg', {asset.Size}, '{hex}', 'Image', '{date:yyyy-MM-dd}', NULL);
            INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID) VALUES ({asset.ID}, (SELECT MAX(MediaFileID) FROM MediaFile));
            """);
    }

    /// <summary>A second media source; returns its id.</summary>
    internal long AddMediaSource(string name)
    {
        Workspace(
            $"""
            INSERT INTO MediaSource (DisplayName, SourceType, RootPath, ImportedDateTimeUtc)
            VALUES ('{name}', 'SyntheticMediaDirectory', 'MediaRoot/{name}', '{Imported}');
            """);

        return WorkspaceScalar("SELECT MAX(MediaSourceID) FROM MediaSource;");
    }

    /// <summary>An undated copy of an existing asset, with a given name, in a given source.</summary>
    internal void AddUndatedCopy(Asset asset, string fileName, long sourceID)
    {
        var hex = Convert.ToHexString(Convert.FromBase64String(asset.Base64Hash));

        _file++;
        Workspace(
            $"""
            INSERT INTO MediaFile (
                MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256, MediaType, FileDate, IsSent)
            VALUES ({sourceID}, 'folder/{_file}-{fileName}', '{fileName}', '.jpg', {asset.Size}, '{hex}', 'Image', NULL, NULL);
            INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID) VALUES ({asset.ID}, (SELECT MAX(MediaFileID) FROM MediaFile));
            """);
    }

    /// <summary>A base64 SHA-256 that names no asset.</summary>
    internal string UnknownHash() => Convert.ToBase64String(NextHash());

    private byte[] NextHash() =>
        SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("synthetic-" + (++_hash).ToString(CultureInfo.InvariantCulture)));

    /// <summary>Publishes a real Phase 7 evidence snapshot over the conversation.</summary>
    internal void RunPhase7()
    {
        CloseWorkspace();

        var result = new Phase7EvidenceSnapshotService().Run(new Phase7RunRequest
        {
            DatabasePath = WorkspacePath,
            ConversationID = ConversationID,
            LocalParticipantID = LocalParticipantID,
        });

        Assert.Equal(Phase7RunStatus.Completed, result.Status);
    }

    /// <summary>A hand confirmation, as the viewer would write it.</summary>
    internal long ConfirmByHand(long attachment, long asset)
    {
        CloseWorkspace();

        return new Phase7ResolutionService().Confirm(new Phase7ConfirmRequest
        {
            DatabasePath = WorkspacePath,
            AttachmentID = attachment,
            MediaAssetID = asset,
            IndependentBasisType = Phase7IndependentBasisType.HumanContentOrContextInspection,
            IndependentBasisDescription = "Checked by eye in the viewer (synthetic).",
            ActorLabel = "Synthetic tester",
        }).AttachmentResolutionEventID;
    }

    /// <summary>
    /// Records an independent disproof of <paramref name="asset"/> for <paramref name="attachment"/>
    /// against the candidate row of the given run (default: the latest run holding one).
    /// </summary>
    internal void Disprove(long attachment, long asset, long? runID = null)
    {
        CloseWorkspace();

        var candidate = long.Parse(Rows(
            $"""
            SELECT AttachmentCandidateID FROM AttachmentCandidate
            WHERE AttachmentID = {attachment} AND MediaAssetID = {asset}
              {(runID is { } r ? $"AND RunID = {r}" : "")}
            ORDER BY RunID DESC LIMIT 1;
            """).Single(), CultureInfo.InvariantCulture);

        new Phase7CandidateDispositionService().Record(new Phase7CandidateDispositionRequest
        {
            DatabasePath = WorkspacePath,
            AttachmentCandidateID = candidate,
            Reason = "Ruled out by eye (synthetic).",
            ActorLabel = "Synthetic tester",
        });
    }

    // ---------------------------------------------------------------------------------------
    // Msgstore side.
    // ---------------------------------------------------------------------------------------

    /// <summary>One media message in the chat at local <paramref name="time"/> on <see cref="Day"/>.</summary>
    internal long AddRecord(
        string time, bool fromMe, string? fileName, long? fileSize, string? fileHash, string? keyID,
        long? sortID = null, long chat = ChatID, DateOnly? date = null,
        int messageType = 1, string mime = "image/jpeg", string? mediaName = null)
    {
        var id = ++_msgstoreMessageID;
        var local = DateTime.ParseExact($"{date ?? Day:yyyy-MM-dd}T{time}", "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var epoch = new DateTimeOffset(local, Offset).ToUnixTimeMilliseconds();

        using (var command = RequireMsgstore().CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO message (_id, chat_row_id, from_me, key_id, timestamp, message_type, sort_id, text_data)
                VALUES ($id, $chat, $fromMe, $key, $ts, $type, $sort, NULL);
                INSERT INTO message_media (message_row_id, file_path, file_size, file_hash, mime_type, media_name)
                VALUES ($id, $path, $size, $hash, $mime, $mediaName);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$chat", chat);
            command.Parameters.AddWithValue("$fromMe", fromMe ? 1 : 0);
            command.Parameters.AddWithValue("$key", (object?)keyID ?? DBNull.Value);
            command.Parameters.AddWithValue("$ts", epoch);
            command.Parameters.AddWithValue("$sort", sortID ?? id);
            command.Parameters.AddWithValue("$path", fileName is null ? DBNull.Value : "Media/WhatsApp Images/" + fileName);
            command.Parameters.AddWithValue("$size", (object?)fileSize ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", (object?)fileHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", messageType);
            command.Parameters.AddWithValue("$mime", mime);
            command.Parameters.AddWithValue("$mediaName", (object?)mediaName ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        return id;
    }

    /// <summary>A non-media message (text, deleted, revoked...) in the chat, with text the tool must never read.</summary>
    internal long AddNonMediaMessage(string time, bool fromMe, int messageType, string? text, DateOnly? date = null)
    {
        var id = ++_msgstoreMessageID;
        var local = DateTime.ParseExact($"{date ?? Day:yyyy-MM-dd}T{time}", "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        using var command = RequireMsgstore().CreateCommand();
        command.CommandText =
            """
            INSERT INTO message (_id, chat_row_id, from_me, key_id, timestamp, message_type, sort_id, text_data)
            VALUES ($id, $chat, $fromMe, $key, $ts, $type, $id, $text);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$chat", ChatID);
        command.Parameters.AddWithValue("$fromMe", fromMe ? 1 : 0);
        command.Parameters.AddWithValue("$key", "SYNTHTEXT" + id.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$ts", new DateTimeOffset(local, Offset).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$type", messageType);
        command.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        command.ExecuteNonQuery();

        return id;
    }

    /// <summary>A record whose file is <paramref name="asset"/> by hash, and by name+size too when asked.</summary>
    internal long AddRecordFor(
        Asset asset, string time, bool fromMe, string keyID, bool nameAndSize = true, long? sortID = null, DateOnly? date = null) =>
        nameAndSize
            ? AddRecord(time, fromMe, asset.FileName, asset.Size, asset.Base64Hash, keyID, sortID, date: date)
            : AddRecord(time, fromMe, "renamed-" + keyID + ".jpg", 1, asset.Base64Hash, keyID, sortID, date: date);

    /// <summary>A confirmation that looks like an earlier msgstore import naming <paramref name="keyID"/>.</summary>
    internal void ConfirmAsImport(long attachment, long asset, string keyID, string tag = "abcdef123456")
    {
        CloseWorkspace();

        new Phase7ResolutionService().Confirm(new Phase7ConfirmRequest
        {
            DatabasePath = WorkspacePath,
            AttachmentID = attachment,
            MediaAssetID = asset,
            IndependentBasisType = Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth,
            IndependentBasisDescription = $"WhatsApp msgstore record (message key_id {keyID}), file matched by hash only; import batch {tag}",
            ActorLabel = "Angie (msgstore import)",
        });
    }

    /// <summary>Runs SQL against the synthetic msgstore (reopening it if closed).</summary>
    internal void Msgstore(string sql)
    {
        using var command = RequireMsgstore().CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------------------------------
    // Plumbing.
    // ---------------------------------------------------------------------------------------

    /// <summary>Closes both building connections, so the tool sees settled files.</summary>
    internal void CloseAll()
    {
        CloseWorkspace();
        _msgstore?.Dispose();
        _msgstore = null;
        SqliteConnection.ClearAllPools();
    }

    private void CloseWorkspace()
    {
        _workspace?.Dispose();
        _workspace = null;
        SqliteConnection.ClearAllPools();
    }

    private SqliteConnection RequireMsgstore()
    {
        if (_msgstore is null)
        {
            _msgstore = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = MsgstorePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            _msgstore.Open();
        }

        return _msgstore;
    }

    internal void Workspace(string sql)
    {
        _workspace ??= WorkspaceDatabase.OpenConnection(WorkspacePath);

        using var command = _workspace.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal long WorkspaceScalar(string sql)
    {
        _workspace ??= WorkspaceDatabase.OpenConnection(WorkspacePath);

        using var command = _workspace.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Reads rows through a fresh read-only connection, each as one comma-joined line.</summary>
    internal List<string> Rows(string sql)
    {
        CloseWorkspace();

        var rows = new List<string>();

        using (var connection = WorkspaceDatabase.OpenReadOnlyConnection(WorkspacePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                rows.Add(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i =>
                    reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
            }
        }

        SqliteConnection.ClearAllPools();

        return rows;
    }

    internal long Count(string sql) => long.Parse(Rows(sql).Single(), CultureInfo.InvariantCulture);

    /// <summary>Every row of every table but the named ones, as text, for before/after comparison.</summary>
    internal Dictionary<string, List<string>> DumpExcept(params string[] skip)
    {
        var tables = Rows("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");

        return tables
            .Where(t => !skip.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(t => t, t => Rows($"SELECT * FROM \"{t}\" ORDER BY rowid;"));
    }

    public void Dispose()
    {
        CloseAll();

        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder outside the repository; leaving it is harmless.
        }
    }

    internal sealed record Asset(long ID, string FileName, long Size, string Base64Hash);
}
