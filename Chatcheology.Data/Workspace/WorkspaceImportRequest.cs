using Chatcheology.Core.Models;

namespace Chatcheology.Data.Workspace
{
    /// <summary>
    /// Everything one workspace import needs: the messages, and the metadata describing where they
    /// came from.
    /// </summary>
    /// <remarks>
    /// The messages are already parsed. Nothing in <c>Chatcheology.Data</c> reads an export file;
    /// parsing stays in <c>Chatcheology.Core</c>.
    /// </remarks>
    public sealed class WorkspaceImportRequest
    {
        /// <summary>
        /// What kind of export this is, in the caller's own vocabulary, for example
        /// <c>WhatsAppAndroidTextExport</c>.
        /// </summary>
        /// <remarks>
        /// Deliberately not constrained to a fixed set in the database, so adding a source type
        /// later needs no schema change.
        /// </remarks>
        public required string SourceType { get; init; }

        /// <summary>A human-readable name for this import, shown to the user.</summary>
        public required string SourceDisplayName { get; init; }

        /// <summary>
        /// The source file's file name only — never a full path.
        /// </summary>
        /// <remarks>
        /// A workspace should not carry the layout of the machine it was built on, so a value
        /// containing any directory or volume separator is rejected rather than trimmed. May be
        /// null when the import did not come from a named file.
        /// </remarks>
        public string? OriginalFileName { get; init; }

        /// <summary>
        /// An optional caller-supplied hash of the source file. Nothing in this phase computes or
        /// verifies it.
        /// </summary>
        public string? SHA256 { get; init; }

        /// <summary>
        /// When this import is being performed. Must be <see cref="DateTimeKind.Utc"/>.
        /// </summary>
        /// <remarks>
        /// Supplied by the caller rather than read from the clock inside the importer, so that one
        /// import operation stamps every row it creates with a single value and tests need no clock
        /// abstraction.
        /// <para>
        /// This is workspace metadata about the import itself. It has nothing to do with when any
        /// message was sent.
        /// </para>
        /// </remarks>
        public required DateTime ImportedDateTimeUtc { get; init; }

        /// <summary>
        /// Optional context describing the timezone the source export's wall-clock timestamps were
        /// written in, for example <c>Africa/Johannesburg</c>.
        /// </summary>
        /// <remarks>
        /// Stored exactly as supplied and never applied to a message timestamp. It is not
        /// validated, resolved or converted in this phase, and it may be null — a public user may
        /// not know which timezone their export was produced in.
        /// </remarks>
        public string? SourceTimeZoneID { get; init; }

        /// <summary>The title to record for the conversation this import creates.</summary>
        public required string ConversationTitle { get; init; }

        /// <summary>
        /// The already-parsed messages, in source order.
        /// </summary>
        /// <remarks>
        /// Each message's <see cref="ParsedMessage.SequenceNumber"/> is persisted exactly as given.
        /// The importer never renumbers, reorders or otherwise alters the messages it is handed.
        /// <para>
        /// Must not be empty. Parsing an empty or whitespace-only export legitimately yields no
        /// messages, but importing that result is refused rather than committed as a conversation
        /// with nothing in it.
        /// </para>
        /// </remarks>
        public required IReadOnlyList<ParsedMessage> Messages { get; init; }
    }
}
