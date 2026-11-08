using System.Globalization;
using System.Text;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>What <c>migrate</c> found and did. Counts and versions only.</summary>
internal sealed class MigrateResult
{
    internal required bool Applied { get; init; }
    internal required int VersionBefore { get; init; }
    internal required int? VersionAfter { get; init; }
    internal required bool GatePasses { get; init; }
    internal required int MissingObjects { get; init; }
    internal required int UnexpectedObjects { get; init; }
}

/// <summary>
/// <c>migrate</c> (CH-8, CH-10, CH-22, CH-71): takes a schema version 5 workspace to version 6 through
/// the product's own <see cref="WorkspaceDatabase.Initialise"/>, and nothing else. Dry run by default.
/// </summary>
/// <remarks>
/// <para>Initialise opens <c>ReadWriteCreate</c>, so it would create a database at a mistyped path. The
/// missing-file check therefore runs first, before anything opens the path or its lock file.</para>
/// <para>The dry run opens the workspace read-only and does not create the lock file: it only tries an
/// existing one. <c>--apply</c> takes <see cref="WorkspaceExclusiveLock"/>, re-reads the version and the
/// shape gate under it, then calls Initialise, which runs the product's own gate and migrates in one
/// transaction.</para>
/// <para>Only version 5, the version the CH-22 migration left, is taken. A version 3 or 4 workspace is
/// refused rather than taken straight to 6: the product could do it, but this command's gate lists, dry
/// run and tests describe exactly one step.</para>
/// </remarks>
internal static class WorkspaceMigrator
{
    /// <summary>The only version this command migrates from.</summary>
    internal const int FromVersion = 5;

    // The version-5 shape, as the product's own (internal) gate defines it in WorkspaceDatabase:
    // VersionTwoTableNames + VersionThreeTableNames + MsgstoreFactTableName + MessageMsgstoreFactTableName,
    // and VersionThreeIndexNames + VersionFourIndexNames + VersionFiveIndexNames. Copied here only to
    // report the gate in a dry run; --apply relies on the product's gate inside Initialise.
    internal static readonly string[] VersionFiveTables =
    [
        "ImportSource", "Conversation", "Participant", "ConversationParticipant", "Message", "MediaSource",
        "MediaAsset", "MediaFile", "MediaAssetFile", "Attachment",
        "Phase7Run", "AttachmentEvidenceSnapshot", "AttachmentCandidate", "AttachmentCandidateOccurrenceEvidence",
        "AttachmentAdjacentDateContext", "AttachmentReviewEvent", "AttachmentCandidateDispositionEvent",
        "AttachmentResolutionEvent",
        "AttachmentMsgstoreFact",
        "MessageMsgstoreFact",
    ];

    internal static readonly string[] VersionFiveIndexes =
    [
        "IX_AttachmentReviewEvent_AttachmentID",
        "IX_AttachmentCandidateDispositionEvent_AttachmentCandidateID",
        "IX_AttachmentResolutionEvent_AttachmentID",
        "IX_AttachmentMsgstoreFact_LowQualityCopyOfAttachmentID",
        "IX_AttachmentMsgstoreFact_SourceBatch",
        "IX_MessageMsgstoreFact_SourceBatch",
    ];

    internal static readonly string MigrateFirst =
        $"This workspace is schema version {V(FromVersion)}. Back it up, close the viewer, and run " +
        "'migrate --workspace <path> --apply' first. Nothing was written.";

    /// <summary>The refusal for a workspace older than <see cref="FromVersion"/> (version 3 or 4).</summary>
    internal static string OlderRefusal(int version) =>
        $"This workspace is schema version {V(version)}. migrate takes only a version {V(FromVersion)} workspace " +
        "(the version the CH-22 migration left), so an older workspace has to go through the product's own " +
        "migration path, not this tool. Nothing was written.";

    internal const string ClaimedRefusal =
        "The workspace is claimed by another operation (the viewer, or another tool). Close it and try " +
        "again. Nothing was written.";

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static MigrateResult Run(Options options)
    {
        var path = options.WorkspacePath;

        // First, before anything that could create a file at this path or beside it.
        if (!File.Exists(path))
        {
            throw new RefusalException(
                "There is no file at the --workspace path. Nothing was opened or created.");
        }

        var (before, missing, unexpected) = Inspect(path);
        RequireFromVersion(before);

        if (!options.ApplySource)
        {
            if (LockIsHeld(path))
            {
                throw new RefusalException(ClaimedRefusal);
            }

            return new MigrateResult
            {
                Applied = false,
                VersionBefore = before,
                VersionAfter = null,
                GatePasses = missing == 0 && unexpected == 0,
                MissingObjects = missing,
                UnexpectedObjects = unexpected,
            };
        }

        if (missing != 0 || unexpected != 0)
        {
            throw new RefusalException(
                $"The workspace reports version {V(FromVersion)} but does not have the version-{V(FromVersion)} shape ({Text.Count(missing)} " +
                $"missing, {Text.Count(unexpected)} unexpected tables or indexes). It is not migrated. " +
                "Nothing was written.");
        }

        WorkspaceExclusiveLock claim;

        try
        {
            claim = WorkspaceExclusiveLock.Acquire(path);
        }
        catch (InvalidOperationException)
        {
            throw new RefusalException(ClaimedRefusal);
        }

        using (claim)
        {
            // Re-read under the claim: something may have changed since the first look.
            var (again, missingAgain, unexpectedAgain) = Inspect(path);
            RequireFromVersion(again);

            if (missingAgain != 0 || unexpectedAgain != 0)
            {
                throw new RefusalException(
                    "The workspace's shape changed since it was first read. It is not migrated. Nothing was written.");
            }

            try
            {
                WorkspaceDatabase.Initialise(path);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                throw new RefusalException(
                    "The workspace is busy: another program holds a lock on it (is the viewer open?). " +
                    "The migration is one transaction, so nothing was written.");
            }
            catch (InvalidOperationException)
            {
                // The product's own gate. Its message names tables, not paths, but only the fact is printed.
                throw new RefusalException(
                    $"The product's version-{V(FromVersion)} shape gate refused the workspace. Nothing was written.");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        var (after, _, _) = Inspect(path);

        return new MigrateResult
        {
            Applied = true,
            VersionBefore = before,
            VersionAfter = after,
            GatePasses = true,
            MissingObjects = 0,
            UnexpectedObjects = 0,
        };
    }

    internal static string Report(MigrateResult r)
    {
        var text = new StringBuilder();

        text.AppendLine(r.Applied ? "# Msgstore import - migrate" : "# Msgstore import - migrate (dry run)");
        text.AppendLine();
        text.AppendLine("Versions and counts only.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine(Text.Line("schema version before", r.VersionBefore));
        text.AppendLine($"{$"v{V(FromVersion)}-to-v{V(WorkspaceDatabase.SchemaVersion)} shape gate",-58}{(r.GatePasses ? "PASS" : "FAIL")}");
        text.AppendLine(Text.Line("  missing tables or indexes", r.MissingObjects));
        text.AppendLine(Text.Line("  unexpected tables or indexes", r.UnexpectedObjects));

        if (r.Applied)
        {
            text.AppendLine(Text.Line("schema version after", r.VersionAfter!.Value));
        }

        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine(r.Applied
            ? r.VersionAfter == WorkspaceDatabase.SchemaVersion
                ? $"Migrated to version {V(WorkspaceDatabase.SchemaVersion)} in one transaction."
                : $"MISMATCH: the version after the migration is not {V(WorkspaceDatabase.SchemaVersion)}."
            : "Dry run: nothing was written. Back up the workspace, close the viewer, then add --apply.");

        return text.ToString();
    }

    internal static bool Succeeded(MigrateResult r) =>
        r.Applied ? r.VersionAfter == WorkspaceDatabase.SchemaVersion : r.GatePasses;

    private static void RequireFromVersion(int version)
    {
        if (version == FromVersion)
        {
            return;
        }

        throw new RefusalException(version switch
        {
            WorkspaceDatabase.SchemaVersion =>
                $"The workspace is already schema version {V(version)}. Nothing to migrate; nothing was written.",
            3 or 4 => OlderRefusal(version),
            _ => $"The workspace reports schema version {V(version)}. migrate takes only a version " +
                 $"{V(FromVersion)} workspace. Nothing was written.",
        });
    }

    /// <summary>
    /// Refuses unless the workspace exists and is at the current schema version: the migrate-first
    /// message for version 5, the older-version message for versions 3 and 4. Read-only; nothing is
    /// created.
    /// </summary>
    internal static void RequireCurrent(string workspacePath)
    {
        Inputs.RequireWorkspace(workspacePath);

        try
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspacePath);

            Inputs.RequireSchemaVersion(connection);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new RefusalException(
                "The workspace is busy: another program holds a lock on it (is the viewer open?). Nothing was written.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static string V(int version) => version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Version, and the shape gate's missing and unexpected object counts, read-only.</summary>
    private static (int Version, int Missing, int Unexpected) Inspect(string path)
    {
        try
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(path);

            var version = WorkspaceDatabase.ReadSchemaVersion(connection);
            var tables = Names(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';");
            var indexes = Names(connection, "SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL;");

            var missing = VersionFiveTables.Count(t => !tables.Contains(t)) + VersionFiveIndexes.Count(i => !indexes.Contains(i));
            var unexpected = tables.Count(t => !VersionFiveTables.Contains(t, StringComparer.Ordinal))
                             + indexes.Count(i => !VersionFiveIndexes.Contains(i, StringComparer.Ordinal));

            return (version, missing, unexpected);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new RefusalException(
                "The workspace is busy: another program holds a lock on it (is the viewer open?). Nothing was written.");
        }
        catch (SqliteException)
        {
            throw new RefusalException("The --workspace file is not a readable SQLite database. Nothing was written.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static HashSet<string> Names(SqliteConnection connection, string sql)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// True when another operation holds the workspace's lock file. Never creates the lock file: a
    /// missing one cannot be held.
    /// </summary>
    private static bool LockIsHeld(string path)
    {
        var lockPath = path + ".lock";

        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
