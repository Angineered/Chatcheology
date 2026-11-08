using System.Globalization;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>A refusal: the operation stops before anything is written, with a reason to print.</summary>
internal sealed class RefusalException(string message) : Exception(message);

/// <summary>Opening both databases read-only and computing the census pairing.</summary>
internal static class Inputs
{
    /// <summary>
    /// The census pairing for every attachment of the conversation, computed read-only on both sides.
    /// </summary>
    /// <remarks>
    /// The workspace is opened only through <see cref="WorkspaceDatabase.OpenReadOnlyConnection"/>;
    /// the msgstore through an <c>immutable=1</c> read-only URI with its size and last-write time
    /// checked before and after. Pools are cleared on the way out so no handle outlives the call.
    /// </remarks>
    /// <exception cref="RefusalException">An input is missing, unsuitable or changed during the read.</exception>
    internal static SortedDictionary<long, Assessment> ComputePairing(
        string msgstorePath, string workspacePath, PairingParameters parameters) =>
        WithPairing(msgstorePath, workspacePath, parameters, pairing => pairing.Compute());

    /// <summary>The CH-9 day-sequence alignment, under the same read-only discipline.</summary>
    internal static DaySequenceResult ComputeDaySequence(
        string msgstorePath, string workspacePath, PairingParameters parameters) =>
        WithPairing(msgstorePath, workspacePath, parameters, pairing => new DaySequence(pairing).Compute());

    /// <summary>The outside-candidate-set assessment, under the same read-only discipline.</summary>
    internal static SortedDictionary<long, OutsideAssessment> ComputeOutside(
        string msgstorePath, string workspacePath, PairingParameters parameters) =>
        WithPairing(msgstorePath, workspacePath, parameters, pairing => new OutsideCandidateSet(pairing).Compute());

    /// <summary>The documents-rule assessment, under the same read-only discipline.</summary>
    internal static SortedDictionary<long, DocumentAssessment> ComputeDocuments(
        string msgstorePath, string workspacePath, PairingParameters parameters) =>
        WithPairing(msgstorePath, workspacePath, parameters, pairing => new Documents(pairing).Compute());

    /// <summary>The name+size review assessment, under the same read-only discipline.</summary>
    internal static SortedDictionary<long, NameSizeAssessment> ComputeNameSize(
        string msgstorePath, string workspacePath, PairingParameters parameters) =>
        WithPairing(msgstorePath, workspacePath, parameters, pairing => new NameSize(pairing).Compute());

    internal static T WithPairing<T>(
        string msgstorePath, string workspacePath, PairingParameters parameters, Func<Pairing, T> compute)
    {
        RequireMsgstore(msgstorePath);
        RequireWorkspace(workspacePath);

        var before = FileStamp.Of(msgstorePath);
        T result;

        try
        {
            using var msgstore = Msgstore.OpenReadOnly(msgstorePath);

            var schema = MsgstoreSchema.Read(msgstore);

            if (schema.Refusal() is { } schemaRefusal)
            {
                throw new RefusalException(schemaRefusal);
            }

            using var workspace = WorkspaceDatabase.OpenReadOnlyConnection(workspacePath);

            RequireSchemaVersion(workspace);
            RequireKnown(workspace, msgstore, parameters);

            result = compute(new Pairing(workspace, msgstore, schema, parameters));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new RefusalException(
                "The workspace is busy: another program holds a lock on it (is the viewer open?). " +
                "Nothing was written.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        if (FileStamp.Of(msgstorePath) != before)
        {
            throw new RefusalException("The msgstore's size or last-write time changed during the read.");
        }

        return result;
    }

    internal static void RequireMsgstore(string path)
    {
        if (!File.Exists(path))
        {
            throw new RefusalException("There is no file at the --msgstore path.");
        }

        if (File.Exists(path + "-wal") || File.Exists(path + "-journal"))
        {
            throw new RefusalException(
                "A -wal or -journal file sits beside the msgstore. An immutable read would ignore what " +
                "it holds, so this tool refuses. Make a self-contained copy first.");
        }
    }

    internal static void RequireWorkspace(string path)
    {
        if (!File.Exists(path))
        {
            throw new RefusalException("There is no file at the --workspace path.");
        }
    }

    internal static void RequireSchemaVersion(SqliteConnection workspace)
    {
        var version = WorkspaceDatabase.ReadSchemaVersion(workspace);

        if (version == WorkspaceMigrator.FromVersion)
        {
            throw new RefusalException(WorkspaceMigrator.MigrateFirst);
        }

        if (version is 3 or 4)
        {
            throw new RefusalException(WorkspaceMigrator.OlderRefusal(version));
        }

        if (version != WorkspaceDatabase.SchemaVersion)
        {
            throw new RefusalException(
                $"That workspace reports schema version {version.ToString(CultureInfo.InvariantCulture)}; " +
                $"this tool needs version {WorkspaceDatabase.SchemaVersion.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    internal static void RequireKnown(SqliteConnection workspace, SqliteConnection msgstore, PairingParameters p)
    {
        if (Scalar(workspace, "SELECT COUNT(*) FROM Conversation WHERE ConversationID = $id;", p.ConversationID) == 0)
        {
            throw new RefusalException("The conversation is not a conversation in this workspace.");
        }

        if (Scalar(workspace, "SELECT COUNT(*) FROM Participant WHERE ParticipantID = $id;", p.LocalParticipantID) == 0)
        {
            throw new RefusalException("The local participant is not a participant in this workspace.");
        }

        if (Scalar(msgstore, "SELECT COUNT(*) FROM chat WHERE _id = $id;", p.ChatID) == 0)
        {
            throw new RefusalException("The chat is not a chat _id in this msgstore.");
        }
    }

    private static long Scalar(SqliteConnection connection, string sql, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>One attachment's stored projection and its current effective event, read-only.</summary>
    internal sealed record AttachmentState(
        bool Exists, bool Resolved, long? ResolvedAsset, Phase7ResolutionHistoryEntry? CurrentEffective);

    /// <summary>
    /// Reads an attachment's state through a fresh read-only connection and the public history reader.
    /// The connection is closed before returning, so it never holds a lock across a write.
    /// </summary>
    internal static AttachmentState ReadState(string workspacePath, long attachmentID)
    {
        using var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspacePath);

        bool resolved;
        long? asset;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ResolutionStatus, ResolvedMediaAssetID FROM Attachment WHERE AttachmentID = $id;";
            command.Parameters.AddWithValue("$id", attachmentID);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return new AttachmentState(false, false, null, null);
            }

            resolved = reader.GetString(0) == "Resolved";
            asset = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        var historyReader = new Phase7ResolutionHistoryReader();
        var projection = historyReader.ReplayProjection(connection, attachmentID);
        var current = projection.CurrentEffectiveEventID is { } eventID
            ? historyReader.ReadHistory(connection, attachmentID).Single(e => e.AttachmentResolutionEventID == eventID)
            : null;

        return new AttachmentState(true, resolved, asset, current);
    }

    /// <summary>
    /// Refuses when another cooperating operation holds the workspace claim. The claim is taken and
    /// released at once; the decision service takes it again for each write.
    /// </summary>
    internal static void RequireUnclaimed(string workspacePath)
    {
        try
        {
            using var claim = WorkspaceExclusiveLock.Acquire(workspacePath);
        }
        catch (InvalidOperationException)
        {
            throw new RefusalException(
                "The workspace is claimed by another operation (the viewer writing a decision, or " +
                "another tool). Close it and try again.");
        }
    }

    /// <summary>
    /// True when a decision-service failure means the workspace is held by someone else: the
    /// cooperative claim could not be taken, or SQLite refused the writer reservation.
    /// </summary>
    internal static bool IsClaimFailure(Exception exception) =>
        exception switch
        {
            SqliteException { SqliteErrorCode: 5 or 6 } => true, // SQLITE_BUSY, SQLITE_LOCKED
            InvalidOperationException { InnerException: IOException or UnauthorizedAccessException } => true,
            _ => false,
        };
}
