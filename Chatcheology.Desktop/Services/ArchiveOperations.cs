using System.IO;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Services
{
    /// <summary>
    /// Which workspace conversation an archive is compared with.
    /// </summary>
    /// <param name="WorkspacePath">The workspace file. Opened read-only by the comparer.</param>
    /// <param name="ConversationID">The conversation selected in the viewer.</param>
    /// <param name="ConversationTitle">
    /// Its title, used only to tell whether the archive is of this conversation at all. Never shown.
    /// </param>
    internal sealed record ArchiveComparisonTarget(
        string WorkspacePath, long ConversationID, string ConversationTitle);

    /// <summary>
    /// Everything the archive checks found, counts only.
    /// </summary>
    /// <remarks>
    /// <see cref="Refusal"/> and <see cref="ComparisonNote"/> are fixed phrases, or the Data and
    /// Archive layers' own messages, which are fixed phrases too. Nothing here carries a path, a name
    /// or message text.
    /// </remarks>
    internal sealed record ArchiveCheckReport
    {
        /// <summary>The CH-17 self-check, or <see langword="null"/> when it could not run at all.</summary>
        internal ArchiveValidationResult? Validation { get; init; }

        /// <summary>Why the self-check could not run, when it could not.</summary>
        internal string? Refusal { get; init; }

        /// <summary>The counts the archive's manifest records, when it can be read.</summary>
        internal ArchiveCounts? Counts { get; init; }

        /// <summary>The SHA-256 of the archive's <c>CONTENTS.sha256</c>, lower-case hex, when present.</summary>
        internal string? ContentsSha256 { get; init; }

        /// <summary>The workspace comparison, or <see langword="null"/> when it was not made.</summary>
        internal ArchiveComparisonResult? Comparison { get; init; }

        /// <summary>Why the workspace comparison was not made, when it was not.</summary>
        internal string? ComparisonNote { get; init; }
    }

    /// <summary>
    /// The archive work the viewer starts: counting, exporting, and checking (CH-18).
    /// </summary>
    /// <remarks>
    /// A seam so the view-model can be tested without writing archives; the application always uses
    /// <see cref="ArchiveOperations"/>. Every method runs on the thread pool, never on the dispatcher.
    /// </remarks>
    internal interface IArchiveOperations
    {
        /// <inheritdoc cref="ArchiveExporter.Preview"/>
        ArchiveExportPreview Preview(
            string workspacePath, long conversationID, long localParticipantID, CancellationToken cancellationToken);

        /// <inheritdoc cref="ArchiveExporter.Export"/>
        ArchiveExportResult Export(
            ArchiveExportRequest request,
            IProgress<ArchiveExportProgress> progress,
            CancellationToken cancellationToken);

        /// <summary>
        /// Runs the self-check on an archive folder and, when a target is given, compares it with
        /// that workspace conversation.
        /// </summary>
        ArchiveCheckReport Check(string archiveFolder, ArchiveComparisonTarget? target);
    }

    /// <summary>
    /// The real archive work: <see cref="ArchiveExporter"/>, <see cref="ArchiveValidator"/> and
    /// <see cref="ArchiveWorkspaceComparer"/>, unchanged.
    /// </summary>
    /// <remarks>
    /// Nothing here decides anything about an archive. It calls the CH-16 and CH-17 code and gathers
    /// what came back; every rule about what passes stays there.
    /// </remarks>
    internal sealed class ArchiveOperations : IArchiveOperations
    {
        /// <inheritdoc />
        public ArchiveExportPreview Preview(
            string workspacePath, long conversationID, long localParticipantID, CancellationToken cancellationToken) =>
            new ArchiveExporter().Preview(workspacePath, conversationID, localParticipantID, cancellationToken);

        /// <inheritdoc />
        public ArchiveExportResult Export(
            ArchiveExportRequest request,
            IProgress<ArchiveExportProgress> progress,
            CancellationToken cancellationToken) =>
            new ArchiveExporter().Export(request, progress, cancellationToken);

        /// <inheritdoc />
        /// <remarks>
        /// <para>The comparison uses the local participant the archive itself records, so it asks
        /// exactly "is this what an export made today would write". It is made only when the archive
        /// is of the selected conversation: one of a different conversation would otherwise fail on
        /// every message, which would say something untrue about a sound archive.</para>
        /// </remarks>
        public ArchiveCheckReport Check(string archiveFolder, ArchiveComparisonTarget? target)
        {
            ArchiveValidationResult validation;

            try
            {
                validation = ArchiveValidator.Validate(archiveFolder);
            }
            catch (ArgumentException)
            {
                // The validator's message names the parameter; the fact is all that is shown.
                return new ArchiveCheckReport { Refusal = "There is no archive folder there." };
            }

            var report = new ArchiveCheckReport
            {
                Validation = validation,
                Counts = ReadCounts(archiveFolder),
                ContentsSha256 = HashContents(archiveFolder),
            };

            if (target is null)
            {
                return report with { ComparisonNote = "Not compared with a workspace: no conversation is open." };
            }

            ArchiveInfo info;

            try
            {
                using var reader = ArchiveReader.Open(archiveFolder);
                info = reader.Info;
            }
            catch (Exception exception) when (
                exception is ArchiveFormatException or SqliteException or IOException or UnauthorizedAccessException)
            {
                return report with
                {
                    ComparisonNote = "Not compared with the workspace: the archive database could not be read.",
                };
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }

            if (!string.Equals(info.ConversationTitle, target.ConversationTitle, StringComparison.Ordinal))
            {
                return report with
                {
                    ComparisonNote =
                        "Not compared with the workspace: the archive is of a different conversation from the " +
                        "one selected.",
                };
            }

            try
            {
                var comparison = new ArchiveWorkspaceComparer().Compare(new ArchiveComparisonRequest
                {
                    WorkspacePath = target.WorkspacePath,
                    ConversationID = target.ConversationID,
                    LocalParticipantID = info.LocalParticipantID,
                    ArchivePath = archiveFolder,
                });

                return report with { Comparison = comparison };
            }
            catch (ArchiveComparisonRefusedException exception)
            {
                return report with { ComparisonNote = "Not compared with the workspace. " + exception.Message };
            }
        }

        /// <summary>The manifest's counts, or <see langword="null"/> when it cannot be read.</summary>
        private static ArchiveCounts? ReadCounts(string archiveFolder)
        {
            try
            {
                return ArchiveManifest.Parse(
                    File.ReadAllBytes(Path.Combine(archiveFolder, ArchiveFormat.ManifestFileName))).Counts;
            }
            catch (Exception exception) when (
                exception is ArchiveFormatException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The hash of <c>CONTENTS.sha256</c>, or <see langword="null"/> when it is not readable.</summary>
        private static string? HashContents(string archiveFolder)
        {
            var path = Path.Combine(archiveFolder, ArchiveContents.FileName);

            try
            {
                return File.Exists(path) ? ArchiveHashing.Sha256OfFile(path) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
