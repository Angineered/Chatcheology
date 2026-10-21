using Chatcheology.Archive;

namespace Chatcheology.Data.ArchiveExport
{
    /// <summary>
    /// What to export, where to, and the counts the caller expects. Nothing is ever assumed: there is
    /// no default conversation, path or count.
    /// </summary>
    public sealed class ArchiveExportRequest
    {
        /// <summary>The workspace database. It is opened read-only.</summary>
        public required string WorkspacePath { get; init; }

        /// <summary>The conversation to export.</summary>
        public required long ConversationID { get; init; }

        /// <summary>The participant whose side of the conversation is "me".</summary>
        public required long LocalParticipantID { get; init; }

        /// <summary>
        /// The archive folder to create. It must not exist yet, its parent folder must, and it must be
        /// outside any repository and outside every media source folder.
        /// </summary>
        public required string OutputPath { get; init; }

        /// <summary>How many of the conversation's attachments must be resolved.</summary>
        public required int ExpectResolved { get; init; }

        /// <summary>
        /// How many unresolved attachments must carry a current "reviewed, insufficient evidence"
        /// review.
        /// </summary>
        public required int ExpectMissingReviewed { get; init; }
    }

    /// <summary>The stage an export is in, for a progress display.</summary>
    public enum ArchiveExportStage
    {
        /// <summary>Reading the conversation's messages from the workspace.</summary>
        ReadingMessages,

        /// <summary>Copying and hashing the media files, one distinct payload at a time.</summary>
        CopyingMedia,

        /// <summary>Writing the messages into the archive database.</summary>
        WritingArchive,
    }

    /// <summary>How far an export has got within one stage.</summary>
    /// <param name="Stage">The stage.</param>
    /// <param name="Done">Items done in that stage.</param>
    /// <param name="Total">Items in that stage.</param>
    public sealed record ArchiveExportProgress(ArchiveExportStage Stage, int Done, int Total);

    /// <summary>
    /// What an export of a conversation would hold, from <see cref="ArchiveExporter.Preview"/>. Counts
    /// only: no title, name or path.
    /// </summary>
    /// <param name="Messages">The conversation's messages.</param>
    /// <param name="Restored">Resolved attachments: pass as <see cref="ArchiveExportRequest.ExpectResolved"/>.</param>
    /// <param name="Missing">
    /// Unresolved attachments with a current "reviewed, insufficient evidence" review: pass as
    /// <see cref="ArchiveExportRequest.ExpectMissingReviewed"/>.
    /// </param>
    /// <param name="Unmatched">Every other unresolved attachment.</param>
    /// <param name="MediaFiles">Distinct media payloads, one file each in the archive.</param>
    /// <param name="MediaBytes">Their recorded sizes, added up.</param>
    public sealed record ArchiveExportPreview(
        int Messages, int Restored, int Missing, int Unmatched, int MediaFiles, long MediaBytes);

    /// <summary>What a completed export wrote. Counts only.</summary>
    public sealed class ArchiveExportResult
    {
        /// <summary>How many rows of each kind the archive holds.</summary>
        public required ArchiveCounts Counts { get; init; }

        /// <summary>The SHA-256 of the archive's <c>CONTENTS.sha256</c>, lower-case hex.</summary>
        public required string ContentsSha256 { get; init; }

        /// <summary>Recorded copies skipped because their bytes differ from the recorded hash.</summary>
        public required int DifferentCopies { get; init; }

        /// <summary>Recorded copies skipped because they were not there or could not be read.</summary>
        public required int UnreadableCopies { get; init; }

        /// <summary>Media taken from a copy other than the first one recorded.</summary>
        public required int FromLaterCopy { get; init; }

        /// <summary>
        /// Low-quality links left out because the full copy they name is not in this conversation.
        /// </summary>
        public required int LowQualityLinksOutsideConversation { get; init; }
    }

    /// <summary>
    /// The export refused before writing anything. The message names the request option involved,
    /// never a path.
    /// </summary>
    public sealed class ArchiveExportRefusedException : Exception
    {
        /// <summary>A refusal with a reason to show.</summary>
        public ArchiveExportRefusedException(string message)
            : base(message)
        {
        }

        /// <summary>A refusal with a reason to show, and the fault that caused it.</summary>
        public ArchiveExportRefusedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// The export stopped after writing began. No archive folder exists, and the partial folder was
    /// removed. The message never carries a path.
    /// </summary>
    public sealed class ArchiveExportStoppedException : Exception
    {
        /// <summary>A stop with a reason to show.</summary>
        public ArchiveExportStoppedException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// A stop with a reason to show, and the fault that caused it. The fault's own message may name
        /// a path; it is kept for diagnosis, never shown.
        /// </summary>
        public ArchiveExportStoppedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
