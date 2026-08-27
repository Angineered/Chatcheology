using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Proves that the append-only resolution history and the <c>Attachment</c> projection say the same
    /// thing — and reports it when they do not.
    /// </summary>
    /// <remarks>
    /// <para><strong>Read-only, and it never repairs.</strong> A mismatch is an invariant failure
    /// requiring diagnosis. Silently writing over either side would destroy the evidence of what went
    /// wrong, and choosing which side to believe is not a decision code can make.</para>
    /// <para><strong>What it detects.</strong> An invalid event sequence; an invalid supersession chain,
    /// including a stale one; a same-asset replacement inserted outside the service; mutation of a
    /// <em>replay-significant</em> field — the event type, the asset, the supersedes reference or the
    /// attachment — because such an edit either invalidates the sequence or changes the state it
    /// implies; projection and history disagreeing in either direction; and a resolved attachment with
    /// no valid resolving history.</para>
    /// <para><strong>What it does not detect, stated rather than implied.</strong> A schema-valid
    /// in-place edit to a field replay does not consume — the actor label, the basis description, the
    /// revocation reason, the source run, the timestamp — is invisible to it, and so is any edit to the
    /// review or disposition tables, neither of which takes part in resolution replay. Detecting those
    /// would need a hash, an audit digest, a trigger or a whole-row history comparison, and none is
    /// proposed. <strong>This is a semantic-consistency detector, not a general audit-integrity
    /// checker.</strong> Append-only remains a service and structural-test invariant: the Stage 7.2
    /// services issue no <c>UPDATE</c> or <c>DELETE</c> against any event table, which is a property of
    /// their statement sets and of a source scan, not something reconciliation is asked to prove.</para>
    /// <para><strong>Where that scope actually comes from.</strong> It is not a property of the SQL:
    /// the history statements select every column of the event row, because the history reader is a
    /// public API whose callers legitimately want the whole entry. The scope is a property of what
    /// replay <em>consumes</em>. <see cref="Phase7ResolutionReplay.Apply"/> reads only the event
    /// identifier, the event type, the asset, the supersedes reference and the attachment, and
    /// <c>RequireAgreement</c> compares only the replayed status and asset against the two fields the
    /// <c>Attachment</c> row stores. A field neither of them looks at cannot change either
    /// answer.</para>
    /// <para>
    /// That boundary is pinned rather than asserted:
    /// <c>VerifyWorkspace_DetectsAReplaySignificantEventMutation_ButNotADescriptiveOne</c> mutates
    /// <c>ActorLabel</c> on an event row and requires this verifier to still pass, then mutates
    /// <c>MediaAssetID</c> on the same row and requires it to fail.
    /// </para>
    /// </remarks>
    public sealed class Phase7ResolutionReconciliation
    {
        /// <summary>
        /// Proves one attachment's history and projection agree.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The attachment does not exist, its history is impossible, or the two representations
        /// disagree. Nothing is written under any circumstances.
        /// </exception>
        public void VerifyAttachment(SqliteConnection connection, long attachmentID)
        {
            ArgumentNullException.ThrowIfNull(connection);

            var stored = ReadStoredProjection(connection, null, attachmentID)
                ?? throw new InvalidOperationException(
                    $"There is no attachment {Text(attachmentID)} in this workspace, so there is " +
                    $"nothing to reconcile.");

            var history =
                Phase7ResolutionHistoryReader.ReadHistory(connection, null, attachmentID);

            RequireAgreement(attachmentID, Phase7ResolutionReplay.Apply(history), stored);
        }

        /// <summary>
        /// Proves every attachment's history and projection agree, in both directions.
        /// </summary>
        /// <remarks>
        /// Two statements and one streaming pass, never one query per attachment. Both directions are
        /// checked: every attachment that has history must replay to its stored projection, and every
        /// resolved attachment must have history that resolves it — a resolved projection with no events
        /// at all is an invariant failure rather than an absence of information.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Some attachment's two representations disagree, or some history is impossible. The message
        /// names the attachment and both readings. Nothing is written.
        /// </exception>
        public void VerifyWorkspace(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            var byAttachment = new Dictionary<long, List<Phase7ResolutionHistoryEntry>>();

            foreach (var entry in Phase7ResolutionHistoryReader.ReadAllHistory(connection))
            {
                if (!byAttachment.TryGetValue(entry.AttachmentID, out var history))
                {
                    history = [];
                    byAttachment[entry.AttachmentID] = history;
                }

                history.Add(entry);
            }

            var resolved = ReadResolvedProjections(connection);

            // Direction one: everything with history must replay to what the projection stores.
            foreach (var (attachmentID, history) in byAttachment.OrderBy(pair => pair.Key))
            {
                var stored = resolved.TryGetValue(attachmentID, out var known)
                    ? known
                    : Unresolved;

                RequireAgreement(attachmentID, Phase7ResolutionReplay.Apply(history), stored);
            }

            // Direction two: nothing may claim an asset without the history that established it.
            foreach (var attachmentID in resolved.Keys.Where(id => !byAttachment.ContainsKey(id))
                .OrderBy(id => id))
            {
                throw new InvalidOperationException(
                    $"Attachment {Text(attachmentID)} is recorded as resolved to asset " +
                    $"{Text(resolved[attachmentID].ResolvedMediaAssetID)}, but its resolution history " +
                    $"is empty. The history is the authoritative provenance of every resolution ever " +
                    $"made, so a resolution with no history is not a resolution this workspace can " +
                    $"account for. Nothing has been repaired.");
            }
        }

        /// <summary>
        /// The in-transaction form, for the resolution service's own pre- and postcondition checks.
        /// </summary>
        /// <returns>
        /// The replayed state, so the caller has the current effective event identifier a replacement
        /// or a revocation must supersede without reading the history a second time.
        /// </returns>
        internal static Phase7ResolutionProjection VerifyAttachment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long attachmentID,
            Phase7ResolutionProjection stored)
        {
            var history =
                Phase7ResolutionHistoryReader.ReadHistory(connection, transaction, attachmentID);

            var replayed = Phase7ResolutionReplay.Apply(history);

            RequireAgreement(attachmentID, replayed, stored);

            return replayed;
        }

        /// <summary>
        /// One attachment's stored projection, or null when the attachment does not exist.
        /// </summary>
        internal static Phase7ResolutionProjection? ReadStoredProjection(
            SqliteConnection connection, SqliteTransaction? transaction, long attachmentID)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Phase7DecisionStatements.AttachmentProjectionStatement;
            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return null;
            }

            return new Phase7ResolutionProjection
            {
                Status = Phase7DecisionText.ParseResolutionStatus(reader.GetString(0)),
                ResolvedMediaAssetID = reader.IsDBNull(1) ? null : reader.GetInt64(1),

                // The stored row carries no current-event pointer, and deliberately never will. The
                // comparison below is on the two fields that are actually stored.
                CurrentEffectiveEventID = null,
            };
        }

        private static Phase7ResolutionProjection Unresolved =>
            new()
            {
                Status = Phase7ResolutionStatus.Unresolved,
                ResolvedMediaAssetID = null,
                CurrentEffectiveEventID = null,
            };

        private static Dictionary<long, Phase7ResolutionProjection> ReadResolvedProjections(
            SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ResolvedProjectionsStatement;

            command.Parameters.AddWithValue(
                "$resolved", Phase7DecisionText.Format(Phase7ResolutionStatus.Resolved));

            var projections = new Dictionary<long, Phase7ResolutionProjection>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                projections[reader.GetInt64(0)] = new Phase7ResolutionProjection
                {
                    Status = Phase7DecisionText.ParseResolutionStatus(reader.GetString(1)),
                    ResolvedMediaAssetID = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    CurrentEffectiveEventID = null,
                };
            }

            return projections;
        }

        /// <summary>
        /// Requires the replayed state and the stored projection to say the same thing.
        /// </summary>
        /// <remarks>
        /// Only the two stored fields are compared, because only those two are stored. The replayed
        /// current-effective-event identifier is derived and has nothing to be compared against, which
        /// is the point of not storing it.
        /// </remarks>
        private static void RequireAgreement(
            long attachmentID,
            Phase7ResolutionProjection replayed,
            Phase7ResolutionProjection stored)
        {
            if (replayed.Status == stored.Status
                && replayed.ResolvedMediaAssetID == stored.ResolvedMediaAssetID)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Attachment {Text(attachmentID)}'s resolution history and its stored projection " +
                $"disagree. Replaying the history in event-identifier order produces " +
                $"{Describe(replayed)}, and the Attachment row stores {Describe(stored)}. The history " +
                $"is the authoritative provenance and the projection is what ordinary reads use, so a " +
                $"disagreement means one of them is wrong and this code cannot say which. Nothing has " +
                $"been repaired and nothing has been written.");
        }

        private static string Describe(Phase7ResolutionProjection projection) =>
            $"{projection.Status} with asset {Text(projection.ResolvedMediaAssetID)}";

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Text(long? value) => value is { } known ? Text(known) : "none";

        /// <summary>
        /// Every statement this verifier issues, for the structural test that asserts none of them
        /// writes.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            ResolvedProjectionsStatement,
            Phase7DecisionStatements.AttachmentProjectionStatement,
        ];

        private const string ResolvedProjectionsStatement =
            """
            SELECT AttachmentID, ResolutionStatus, ResolvedMediaAssetID
            FROM Attachment
            WHERE ResolutionStatus = $resolved;
            """;
    }
}
