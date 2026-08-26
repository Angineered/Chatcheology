namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// What one Phase 7 evidence run is asked to look at.
    /// </summary>
    /// <remarks>
    /// The same two scope values the frozen Phase 6 analysis takes, because a Phase 7 run is a
    /// persisted snapshot of that analysis and nothing else. Nothing here is a threshold, a filter or
    /// a policy: there is no way to ask this service for a subset of the evidence, or for a different
    /// reading of it.
    /// <para>
    /// The workspace is opened, never created and never migrated. A workspace at any schema version
    /// other than the current one is refused before a read-write connection is opened at all.
    /// </para>
    /// </remarks>
    public sealed class Phase7RunRequest
    {
        /// <summary>
        /// An existing workspace at schema version
        /// <see cref="Workspace.WorkspaceDatabase.SchemaVersion"/>.
        /// </summary>
        public required string DatabasePath { get; init; }

        /// <summary>The conversation whose unresolved attachments are evaluated.</summary>
        public required long ConversationID { get; init; }

        /// <summary>
        /// Which conversation participant is the local, exporting user, or <see langword="null"/>
        /// when that is not known.
        /// </summary>
        /// <remarks>
        /// Passed straight to the frozen analysis and never guessed. When it is null every message
        /// direction is <see cref="Matching.MessageDirection.Unknown"/>, every candidate's direction
        /// annotation is <see cref="Matching.DirectionCompatibility.Unknown"/>, and the whole
        /// direction dimension is simply unavailable — which is the honest result rather than a
        /// degraded one.
        /// <para>
        /// The workspace records nowhere which value a run used, so two runs with different values
        /// legitimately produce different direction annotations and nothing in the database can tell
        /// them apart. That is why <see cref="Phase7RunResult.LocalParticipantID"/> echoes it back
        /// as provenance, and why retained run evidence must preserve it.
        /// </para>
        /// </remarks>
        public long? LocalParticipantID { get; init; }
    }
}
