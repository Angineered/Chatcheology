using System.Globalization;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The Stage 7.2 decision operations, over the same synthetic workspace the Stage 7.1 tests build.
    /// </summary>
    /// <remarks>
    /// Composed around <see cref="Phase7TestFixture"/> rather than derived from it, because that type is
    /// sealed and because the two concerns are genuinely separate: one builds evidence, this one records
    /// decisions about it. <see cref="Evidence"/> is the evidence half, unchanged.
    /// <para>
    /// Everything here is invented. No path under any recovery drive, no real file name, no real hash and
    /// no archive-specific count appears anywhere in this project.
    /// </para>
    /// <para>
    /// Every decision helper closes the building connection first, following the convention
    /// <c>Phase7TestFixture.Run</c> establishes: a Stage 7.2 service opens its own connections and takes
    /// the cooperative lock, and a test that later checkpoints or compares the file must not be holding a
    /// handle on it.
    /// </para>
    /// </remarks>
    internal sealed class Phase7DecisionFixture : IDisposable
    {
        internal const string HumanBasis =
            "Opened the recovered copy and recognised the whiteboard from that meeting.";

        internal const string ExternalBasis =
            "The camera roll export lists this payload against this conversation at this time.";

        internal const string DisproofReason =
            "The recovered copy shows a different room from the one this message describes.";

        internal const string RevocationReason =
            "Looked again and the whiteboard text does not match; the earlier reading was wrong.";

        internal const string ReviewNotes =
            "Looked at every candidate and cannot say which, if any, this attachment is.";

        private readonly Phase7TestFixture _evidence = new();

        /// <summary>The evidence half: messages, attachments, media, and Stage 7.1 runs.</summary>
        internal Phase7TestFixture Evidence => _evidence;

        internal string DatabasePath => _evidence.DatabasePath;

        internal string DirectoryPath => _evidence.DirectoryPath;

        // ---------------------------------------------------------------------------------------
        // Recording decisions.
        // ---------------------------------------------------------------------------------------

        internal Phase7ReviewResult Review(
            long attachmentID,
            long sourceRunID,
            string? notes = ReviewNotes,
            string? actorLabel = "Reviewer A")
        {
            _evidence.CloseBuilding();

            return new Phase7ReviewService().Record(
                new Phase7ReviewRequest
                {
                    DatabasePath = DatabasePath,
                    AttachmentID = attachmentID,
                    SourceRunID = sourceRunID,
                    ReviewNotes = notes,
                    ActorLabel = actorLabel,
                });
        }

        internal Phase7CandidateDispositionResult Disprove(
            long candidateID,
            string reason = DisproofReason,
            string? actorLabel = "Reviewer A")
        {
            _evidence.CloseBuilding();

            return new Phase7CandidateDispositionService().Record(
                new Phase7CandidateDispositionRequest
                {
                    DatabasePath = DatabasePath,
                    AttachmentCandidateID = candidateID,
                    Reason = reason,
                    ActorLabel = actorLabel,
                });
        }

        internal Phase7ConfirmRequest ConfirmRequest(
            long attachmentID,
            long mediaAssetID,
            Phase7IndependentBasisType basisType =
                Phase7IndependentBasisType.HumanContentOrContextInspection,
            string description = HumanBasis,
            long? sourceRunID = null,
            string? actorLabel = "Reviewer A") =>
            new()
            {
                DatabasePath = DatabasePath,
                AttachmentID = attachmentID,
                MediaAssetID = mediaAssetID,
                IndependentBasisType = basisType,
                IndependentBasisDescription = description,
                SourceRunID = sourceRunID,
                ActorLabel = actorLabel,
            };

        internal Phase7ResolutionTransitionResult Confirm(
            long attachmentID,
            long mediaAssetID,
            Phase7IndependentBasisType basisType =
                Phase7IndependentBasisType.HumanContentOrContextInspection,
            string description = HumanBasis,
            long? sourceRunID = null,
            string? actorLabel = "Reviewer A")
        {
            _evidence.CloseBuilding();

            return new Phase7ResolutionService().Confirm(
                ConfirmRequest(
                    attachmentID, mediaAssetID, basisType, description, sourceRunID, actorLabel));
        }

        internal Phase7ConfirmReplacementRequest ReplaceRequest(
            long attachmentID,
            long mediaAssetID,
            long supersedesEventID,
            Phase7IndependentBasisType basisType =
                Phase7IndependentBasisType.HumanContentOrContextInspection,
            string description = HumanBasis,
            long? sourceRunID = null,
            string? actorLabel = "Reviewer A") =>
            new()
            {
                DatabasePath = DatabasePath,
                AttachmentID = attachmentID,
                MediaAssetID = mediaAssetID,
                SupersedesEventID = supersedesEventID,
                IndependentBasisType = basisType,
                IndependentBasisDescription = description,
                SourceRunID = sourceRunID,
                ActorLabel = actorLabel,
            };

        internal Phase7ResolutionTransitionResult Replace(
            long attachmentID,
            long mediaAssetID,
            long supersedesEventID,
            Phase7IndependentBasisType basisType =
                Phase7IndependentBasisType.HumanContentOrContextInspection,
            string description = HumanBasis,
            long? sourceRunID = null,
            string? actorLabel = "Reviewer A")
        {
            _evidence.CloseBuilding();

            return new Phase7ResolutionService().ConfirmReplacement(
                ReplaceRequest(
                    attachmentID,
                    mediaAssetID,
                    supersedesEventID,
                    basisType,
                    description,
                    sourceRunID,
                    actorLabel));
        }

        internal Phase7RevokeRequest RevokeRequest(
            long attachmentID,
            long supersedesEventID,
            string reason = RevocationReason,
            long? sourceRunID = null,
            string? actorLabel = "Reviewer A") =>
            new()
            {
                DatabasePath = DatabasePath,
                AttachmentID = attachmentID,
                SupersedesEventID = supersedesEventID,
                RevocationReason = reason,
                SourceRunID = sourceRunID,
                ActorLabel = actorLabel,
            };

        internal Phase7ResolutionTransitionResult Revoke(
            long attachmentID,
            long supersedesEventID,
            string reason = RevocationReason,
            long? sourceRunID = null,
            string? actorLabel = "Reviewer A")
        {
            _evidence.CloseBuilding();

            return new Phase7ResolutionService().Revoke(
                RevokeRequest(attachmentID, supersedesEventID, reason, sourceRunID, actorLabel));
        }

        // ---------------------------------------------------------------------------------------
        // The internal after-writes observation seam.
        // ---------------------------------------------------------------------------------------

        internal Phase7ResolutionTransitionResult ConfirmWithHook(
            Phase7ConfirmRequest request, Action<Phase7DecisionSession> hook)
        {
            _evidence.CloseBuilding();

            return new Phase7ResolutionService().Confirm(request, hook);
        }

        internal Phase7ResolutionTransitionResult ReplaceWithHook(
            Phase7ConfirmReplacementRequest request, Action<Phase7DecisionSession> hook)
        {
            _evidence.CloseBuilding();

            return new Phase7ResolutionService().ConfirmReplacement(request, hook);
        }

        internal Phase7ResolutionTransitionResult RevokeWithHook(
            Phase7RevokeRequest request, Action<Phase7DecisionSession> hook)
        {
            _evidence.CloseBuilding();

            return new Phase7ResolutionService().Revoke(request, hook);
        }

        /// <summary>
        /// Reads one integer through the session's own open transaction, so a hook can observe rows
        /// that are written but not yet committed.
        /// </summary>
        internal static long ScalarLongInSession(Phase7DecisionSession session, string sql)
        {
            using var command = session.CreateCommand(sql);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------------------------------
        // Reading decisions back.
        // ---------------------------------------------------------------------------------------

        /// <summary>A read-only connection the caller owns, for the read-only services.</summary>
        internal SqliteConnection OpenReadOnly()
        {
            _evidence.CloseBuilding();

            return WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);
        }

        internal IReadOnlyList<Phase7ResolutionHistoryEntry> History(long attachmentID)
        {
            using var connection = OpenReadOnly();

            return new Phase7ResolutionHistoryReader().ReadHistory(connection, attachmentID);
        }

        internal Phase7ResolutionProjection Replayed(long attachmentID)
        {
            using var connection = OpenReadOnly();

            return new Phase7ResolutionHistoryReader().ReplayProjection(connection, attachmentID);
        }

        internal Phase7ReconstructionState Reconstruct(long attachmentID)
        {
            using var connection = OpenReadOnly();

            return new Phase7ReconstructionReader().Read(connection, attachmentID);
        }

        internal (IReadOnlyList<Phase7ReconstructionState> States, int Statements) ReconstructBatch(
            IReadOnlyList<long> attachmentIDs)
        {
            using var connection = OpenReadOnly();

            var reader = new Phase7ReconstructionReader();

            var states = reader.Read(connection, attachmentIDs);

            return (states, reader.LastReadStatementCount);
        }

        internal bool AllDisproven(long runID, long attachmentID)
        {
            using var connection = OpenReadOnly();

            return new Phase7CandidateDispositionReader()
                .AllCandidatesIndependentlyDisproven(connection, runID, attachmentID);
        }

        internal IReadOnlyList<long> DisprovenCandidateIDs(long runID, long attachmentID)
        {
            using var connection = OpenReadOnly();

            return new Phase7CandidateDispositionReader()
                .ReadIndependentlyDisprovenCandidateIDs(connection, runID, attachmentID);
        }

        internal void VerifyWorkspace()
        {
            using var connection = OpenReadOnly();

            new Phase7ResolutionReconciliation().VerifyWorkspace(connection);
        }

        internal void VerifyAttachment(long attachmentID)
        {
            using var connection = OpenReadOnly();

            new Phase7ResolutionReconciliation().VerifyAttachment(connection, attachmentID);
        }

        /// <summary>The candidate row one run recorded for one attachment and asset.</summary>
        internal long CandidateID(long runID, long attachmentID, long mediaAssetID) =>
            _evidence.ScalarLong(
                $"""
                SELECT AttachmentCandidateID
                FROM AttachmentCandidate
                WHERE RunID = {runID}
                  AND AttachmentID = {attachmentID}
                  AND MediaAssetID = {mediaAssetID};
                """);

        /// <summary>One attachment's stored projection, as one comparable line.</summary>
        internal string StoredProjection(long attachmentID) =>
            _evidence.ScalarText(
                $"""
                SELECT ResolutionStatus || '/' || COALESCE(ResolvedMediaAssetID, 'none')
                FROM Attachment
                WHERE AttachmentID = {attachmentID};
                """);

        /// <summary>The whole <c>Attachment</c> resolution distribution, as comparable lines.</summary>
        internal List<string> ResolutionDistribution() =>
            _evidence.Rows(
                """
                SELECT ResolutionStatus, ResolvedMediaAssetID IS NULL, COUNT(*)
                FROM Attachment
                GROUP BY ResolutionStatus, ResolvedMediaAssetID IS NULL
                ORDER BY ResolutionStatus, ResolvedMediaAssetID IS NULL;
                """);

        internal long EventCount(string tableName) =>
            _evidence.ScalarLong($"SELECT COUNT(*) FROM {tableName};");

        /// <summary>All three decision tables' row counts, as one comparable line.</summary>
        internal string DecisionRowCounts() =>
            string.Join(
                "|",
                WorkspaceDatabase.VersionThreeDecisionTableNames.Select(
                    table => $"{table}={EventCount(table).ToString(CultureInfo.InvariantCulture)}"));

        /// <summary>The baseline signature the Stage 7.1 publication tripwire compares.</summary>
        internal string BaselineSignature()
        {
            using var connection = OpenReadOnly();

            return Phase7RunReconciliation.ReadBaseline(connection).Signature;
        }

        // ---------------------------------------------------------------------------------------
        // Direct writes, for states the services cannot produce.
        // ---------------------------------------------------------------------------------------

        internal void ExecuteDirect(string sql) => _evidence.ExecuteDirect(sql);

        /// <summary>
        /// Inserts one resolution event straight into the table, bypassing the service.
        /// </summary>
        /// <remarks>
        /// The only way to build a history the service refuses to create — a double confirmation, a
        /// stale supersession, a same-asset replacement — so that replay and the verifier can be shown to
        /// detect it. Foreign keys are disabled only where the row deliberately breaks one, which is the
        /// same seam <c>MatchingTestWorkspace.ExecuteWithoutForeignKeys</c> already exists for.
        /// </remarks>
        internal void InsertResolutionEventDirectly(
            long attachmentID,
            string eventType,
            long? mediaAssetID,
            long? supersedesEventID,
            bool withForeignKeys = true)
        {
            var basisType = eventType == "Revoke"
                ? "NULL"
                : "'HumanContentOrContextInspection'";

            var basisDescription = eventType == "Revoke" ? "NULL" : "'Injected directly'";
            var revocationReason = eventType == "Revoke" ? "'Injected directly'" : "NULL";

            var sql =
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                    IndependentBasisDescription, RevocationReason, SupersedesEventID,
                    SourceRunID, ActorLabel, OccurredAtUtc)
                VALUES (
                    {attachmentID}, '{eventType}',
                    {(mediaAssetID is { } asset
                        ? asset.ToString(CultureInfo.InvariantCulture)
                        : "NULL")},
                    {basisType}, {basisDescription}, {revocationReason},
                    {(supersedesEventID is { } superseded
                        ? superseded.ToString(CultureInfo.InvariantCulture)
                        : "NULL")},
                    NULL, 'Direct', '2026-08-27T09:00:00.0000000Z');
                """;

            if (withForeignKeys)
            {
                _evidence.ExecuteDirect(sql);
            }
            else
            {
                _evidence.Workspace.ExecuteWithoutForeignKeys(sql);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Whole-row comparison.
        // ---------------------------------------------------------------------------------------

        /// <summary>Copies the workspace to a checkpoint beside it, and returns its path.</summary>
        internal string Checkpoint(string fileName = "checkpoint.db")
        {
            _evidence.CloseBuilding();

            var checkpointPath = Path.Combine(DirectoryPath, fileName);

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(DatabasePath, checkpointPath);

            return checkpointPath;
        }

        /// <summary>
        /// Proves all three decision tables are whole-row identical to a checkpoint.
        /// </summary>
        /// <remarks>
        /// This is the layer of the Stage 7.1 compatibility argument that actually establishes whole-row
        /// preservation. The publication tripwire is four aggregates per table and proves far less; the
        /// structural statement tests prove Stage 7.1 has no data-modifying statement naming these tables
        /// at all, which is what covers the columns the aggregates cannot see.
        /// </remarks>
        internal void RequireDecisionTablesIdentical(string checkpointPath)
        {
            _evidence.CloseBuilding();

            using var live = WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);

            WorkspaceCheckpoint.RequireIdenticalTables(
                live, checkpointPath, WorkspaceDatabase.VersionThreeDecisionTableNames);
        }

        public void Dispose() => _evidence.Dispose();
    }
}
