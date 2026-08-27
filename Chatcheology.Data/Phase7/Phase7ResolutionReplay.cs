using System.Globalization;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The transition rules themselves: applying an attachment's events in identifier order and
    /// producing the state they imply.
    /// </summary>
    /// <remarks>
    /// <para><strong>This is the specification, not a second opinion.</strong> The resolution service
    /// derives its own validation from this function rather than restating the rules, so there is
    /// exactly one place where what a transition is allowed to do is written down. That is why the rule
    /// that a replacement must change the asset lives here too, and not only in the service.</para>
    /// <para><strong>Pure.</strong> No database, no connection, no clock. That is what lets the rules be
    /// tested directly over hand-built sequences, including sequences the service can never produce.
    /// </para>
    /// <para><strong>Nothing is normalised.</strong> There is no "take the last event" shortcut, no
    /// skipping, no repair and no tolerance branch. An impossible history throws, naming the event and
    /// what was expected, because a history that cannot have happened is a fact a reader has to be told
    /// rather than a value to smooth over.</para>
    /// </remarks>
    internal static class Phase7ResolutionReplay
    {
        /// <summary>
        /// Replays one attachment's history, which must already be ordered by
        /// <c>AttachmentResolutionEventID</c> ascending.
        /// </summary>
        /// <param name="orderedByEventID">
        /// The attachment's events, ascending by identifier. An empty sequence replays to the initial
        /// unresolved state, which is the correct reading of an attachment that has never been decided.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The sequence contains a transition that could not have happened from the state that preceded
        /// it. The message names the offending event, its type, the state it was applied to and the
        /// expectation it broke.
        /// </exception>
        internal static Phase7ResolutionProjection Apply(
            IReadOnlyList<Phase7ResolutionHistoryEntry> orderedByEventID)
        {
            ArgumentNullException.ThrowIfNull(orderedByEventID);

            var status = Phase7ResolutionStatus.Unresolved;
            long? mediaAssetID = null;
            long? currentEffectiveEventID = null;

            foreach (var entry in orderedByEventID)
            {
                switch (entry.EventType)
                {
                    case Phase7ResolutionEventType.Confirm:
                        Require(
                            status == Phase7ResolutionStatus.Unresolved,
                            entry,
                            status,
                            "a first confirmation is valid only while the attachment is unresolved; " +
                            "changing an existing resolution is a replacement, and undoing one is a " +
                            "revocation");

                        Require(
                            entry.SupersedesEventID is null,
                            entry,
                            status,
                            "a first confirmation supersedes nothing, because nothing effective " +
                            "exists to supersede");

                        mediaAssetID = RequireTarget(entry);
                        status = Phase7ResolutionStatus.Resolved;
                        currentEffectiveEventID = entry.AttachmentResolutionEventID;

                        break;

                    case Phase7ResolutionEventType.ConfirmReplacement:
                        Require(
                            status == Phase7ResolutionStatus.Resolved,
                            entry,
                            status,
                            "a replacement is valid only while the attachment is resolved; there is " +
                            "nothing to replace otherwise");

                        RequireSupersedesCurrent(entry, status, currentEffectiveEventID);

                        var replacementTarget = RequireTarget(entry);

                        Require(
                            replacementTarget != mediaAssetID,
                            entry,
                            status,
                            "a replacement must change the asset. Each transition has exactly one " +
                            "job — a confirmation sets the asset, a replacement changes it, a " +
                            "revocation clears it — so a replacement naming the asset already " +
                            "recorded would give one operation a second meaning it does not have. " +
                            "There is deliberately no reaffirmation operation");

                        mediaAssetID = replacementTarget;
                        currentEffectiveEventID = entry.AttachmentResolutionEventID;

                        break;

                    case Phase7ResolutionEventType.Revoke:
                        Require(
                            status == Phase7ResolutionStatus.Resolved,
                            entry,
                            status,
                            "a revocation is valid only while the attachment is resolved; there is " +
                            "nothing to undo otherwise");

                        RequireSupersedesCurrent(entry, status, currentEffectiveEventID);

                        status = Phase7ResolutionStatus.Unresolved;
                        mediaAssetID = null;

                        // Deliberately cleared rather than rewound. A revocation leaves nothing to
                        // supersede, so no older resolution can be reactivated by a later event and
                        // restoring an earlier asset requires a fresh confirmation with its own basis.
                        currentEffectiveEventID = null;

                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Resolution event {Text(entry.AttachmentResolutionEventID)} for " +
                            $"attachment {Text(entry.AttachmentID)} records the event type " +
                            $"{entry.EventType}, which replay has no rules for. There are three " +
                            $"transitions and no fourth, and nothing is inferred from a value " +
                            $"outside them.");
                }
            }

            return new Phase7ResolutionProjection
            {
                Status = status,
                ResolvedMediaAssetID = mediaAssetID,
                CurrentEffectiveEventID = currentEffectiveEventID,
            };
        }

        /// <summary>
        /// Requires a replacement or a revocation to name the event replay currently ends on.
        /// </summary>
        /// <remarks>
        /// Because replay is fed only one attachment's rows, an event whose supersedes reference points
        /// at another attachment's event — or at itself — can never equal the current effective
        /// identifier, and is caught here. The schema's composite foreign key independently forbids the
        /// cross-attachment case for any writer that has foreign keys enabled; this check does not rely
        /// on that being so.
        /// </remarks>
        private static void RequireSupersedesCurrent(
            Phase7ResolutionHistoryEntry entry,
            Phase7ResolutionStatus status,
            long? currentEffectiveEventID) =>
            Require(
                entry.SupersedesEventID is { } superseded
                    && currentEffectiveEventID is { } current
                    && superseded == current,
                entry,
                status,
                $"it must supersede the attachment's current effective event, which is " +
                $"{Text(currentEffectiveEventID)}, and not merely an event that exists somewhere in " +
                $"its history; it names {Text(entry.SupersedesEventID)}");

        private static long RequireTarget(Phase7ResolutionHistoryEntry entry) =>
            entry.MediaAssetID
                ?? throw new InvalidOperationException(
                    $"Resolution event {Text(entry.AttachmentResolutionEventID)} for attachment " +
                    $"{Text(entry.AttachmentID)} is a {entry.EventType} with no asset. The schema's " +
                    $"own CHECK forbids that pairing, so this row was written by something that " +
                    $"bypassed it, and replay does not invent the asset it should have named.");

        private static void Require(
            bool condition,
            Phase7ResolutionHistoryEntry entry,
            Phase7ResolutionStatus status,
            string expectation)
        {
            if (condition)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Attachment {Text(entry.AttachmentID)} has an impossible resolution history: event " +
                $"{Text(entry.AttachmentResolutionEventID)} is a {entry.EventType} applied to a " +
                $"{status} state, and {expectation}. Replay reports this rather than normalising it, " +
                $"because a history that cannot have happened is not a state to smooth over.");
        }

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Text(long? value) => value is { } known ? Text(known) : "none";
    }
}
