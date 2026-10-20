using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;
using Chatcheology.Core.Models;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.ArchiveExport
{
    /// <summary>
    /// Exports one reconstructed conversation from a workspace into a new portable archive folder
    /// (format: <see cref="ArchiveFormat"/>).
    /// </summary>
    /// <remarks>
    /// <para><strong>Every guard first, writing nothing.</strong> The output must be new, outside any
    /// repository and outside every media source folder, and the resolved and reviewed-missing counts
    /// must be exactly what the caller expects. Only then is anything written.</para>
    /// <para><strong>Read-only on every input.</strong> The workspace is read through
    /// <see cref="ArchiveExportSource"/> on a read-only connection, closed before the first write; the
    /// media copies are only opened for reading. Nothing is written outside the output folder.</para>
    /// <para><strong>A folder at the output path is always complete.</strong> Everything is written
    /// into <c>&lt;output&gt;.partial</c>, which this run proves it owns with a marker file, and which
    /// is renamed only after <c>manifest.json</c> and <c>CONTENTS.sha256</c> are written. On any stop
    /// the partial folder is removed.</para>
    /// <para><strong>Media.</strong> One file per distinct payload. The recorded copies are tried in
    /// the location reader's order; the first whose bytes, hashed while copying, equal the recorded
    /// SHA-256 is kept. A restored attachment with no good copy stops the export.</para>
    /// <para><strong>Deterministic.</strong> The same workspace gives the same rows and the same
    /// media bytes; only the creation time differs. It prints nothing; progress goes to the callback.</para>
    /// </remarks>
    public sealed class ArchiveExporter
    {
        /// <summary>The suffix of the folder the archive is built in.</summary>
        public const string PartialSuffix = ".partial";

        /// <summary>The ownership marker in the partial folder. Never part of a finished archive.</summary>
        internal const string MarkerFile = ".archive-export-in-progress";

        /// <summary>Messages per workspace page read.</summary>
        internal int PageSize { get; init; } = ConversationReader.MaximumPageSize;

        /// <summary>Test seam: runs just before the partial folder is created. Never set outside tests.</summary>
        internal Action<string>? BeforePartialCreatedForTests { get; init; }

        /// <summary>
        /// Test seam: runs with the media folder's path once it exists, before any copy. Never set
        /// outside tests.
        /// </summary>
        internal Action<string>? BeforeMediaCopiedForTests { get; init; }

        /// <summary>Test seam: runs once the media are copied. Never set outside tests.</summary>
        internal Action<string>? AfterMediaCopiedForTests { get; init; }

        /// <summary>What the archive records as its producer.</summary>
        public static string ProducerVersion =>
            "Chatcheology " +
            (typeof(ArchiveExporter).Assembly
                 .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
             ?? "unknown");

        /// <summary>Exports the conversation.</summary>
        /// <exception cref="ArchiveExportRefusedException">A guard failed. Nothing was written.</exception>
        /// <exception cref="ArchiveExportStoppedException">
        /// Writing began and then stopped. The partial folder was removed and no archive exists.
        /// </exception>
        public ArchiveExportResult Export(
            ArchiveExportRequest request, IProgress<ArchiveExportProgress>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(request);

            // ---- Guards: nothing is written until every one has passed. ----

            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
            {
                throw new ArchiveExportRefusedException("The WorkspacePath is empty. Nothing was written.");
            }

            if (request.ExpectResolved < 0 || request.ExpectMissingReviewed < 0)
            {
                throw new ArchiveExportRefusedException(
                    "ExpectResolved and ExpectMissingReviewed must not be negative. Nothing was written.");
            }

            var output = OutputGuard.Check(request.OutputPath);
            var partial = output + PartialSuffix;

            var source = ArchiveExportSource.Read(request, PageSize, progress);

            if (source.Resolved != request.ExpectResolved)
            {
                throw new ArchiveExportRefusedException(
                    $"{Count(source.Resolved)} attachments are resolved; ExpectResolved is " +
                    $"{Count(request.ExpectResolved)}. Nothing was written.");
            }

            if (source.MissingReviewed != request.ExpectMissingReviewed)
            {
                throw new ArchiveExportRefusedException(
                    $"{Count(source.MissingReviewed)} unresolved attachments are reviewed as missing (a " +
                    $"current ReviewedInsufficientEvidence review); ExpectMissingReviewed is " +
                    $"{Count(request.ExpectMissingReviewed)}. Nothing was written.");
            }

            foreach (var root in source.MediaRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (IsWithinRoot(output, root))
                {
                    throw new ArchiveExportRefusedException(
                        "The OutputPath is inside a media source folder. Nothing was written.");
                }
            }

            // ---- Writing. ----

            BeforePartialCreatedForTests?.Invoke(partial);

            try
            {
                ClaimPartial(partial);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Not removed: a folder this run could not create or claim is not this run's.
                throw new ArchiveExportStoppedException(
                    "The partial folder beside the OutputPath could not be created. No archive folder was " +
                    "written.",
                    exception);
            }

            try
            {
                return Write(source, request, partial, output, progress);
            }
            catch (ArchiveExportStoppedException)
            {
                Remove(partial);
                throw;
            }
            catch (InvalidOperationException exception)
            {
                // The archive writer refused a row. Its messages are fixed phrases naming the rule.
                Remove(partial);
                throw new ArchiveExportStoppedException(
                    "The archive database could not be written: " + exception.Message + " No archive folder " +
                    "was written.",
                    exception);
            }
            catch (Exception exception)
            {
                // Anything else after writing began (SQLite, the file system). Its own message may carry
                // a path, so only its type is named; it stays available as the inner exception.
                Remove(partial);
                throw new ArchiveExportStoppedException(
                    $"The export could not complete ({exception.GetType().Name}). No archive folder was " +
                    "written.",
                    exception);
            }
        }

        /// <summary>
        /// Whether the output is inside a recorded media root. A root that is not a usable path on this
        /// machine cannot contain the output.
        /// </summary>
        private static bool IsWithinRoot(string output, string root)
        {
            try
            {
                return ArchivePaths.IsWithin(output, root);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        private ArchiveExportResult Write(
            ArchiveExportSource source,
            ArchiveExportRequest request,
            string partial,
            string output,
            IProgress<ArchiveExportProgress>? progress)
        {
            var media = CopyMedia(source, partial, progress);

            if (media.Failed > 0)
            {
                throw new ArchiveExportStoppedException(
                    $"{Count(media.Failed)} of {Count(source.AssetSha256.Count)} resolved media payloads have " +
                    $"no readable copy whose bytes match the recorded SHA-256 ({Count(media.Unreadable)} copies " +
                    $"not there or unreadable, {Count(media.Different)} with different bytes). No archive " +
                    "folder was written.");
            }

            AfterMediaCopiedForTests?.Invoke(partial);

            // A rejected copy's temporary file whose delete failed must never end up in an archive.
            if (Directory.EnumerateFiles(
                    Path.Combine(partial, ArchiveFormat.MediaFolderName), "*.tmp", SearchOption.AllDirectories).Any())
            {
                throw new ArchiveExportStoppedException(
                    "A temporary media file could not be removed from the partial folder. No archive folder " +
                    "was written.");
            }

            var now = DateTime.UtcNow;
            var created = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

            var (counts, linksOutside) = WriteDatabase(source, request, media.Stored, created, partial, progress);

            // The ownership marker is not part of the archive. From here on a stop still removes the
            // folder: ownership was established when it was created.
            File.Delete(Path.Combine(partial, MarkerFile));

            var manifest = new ArchiveManifest(
                ArchiveManifest.FormatName, ArchiveFormat.FormatVersion, ProducerVersion, created, counts);

            File.WriteAllBytes(Path.Combine(partial, ArchiveFormat.ManifestFileName), manifest.ToBytes());

            var contents = ArchiveContents.Build(partial);
            File.WriteAllBytes(Path.Combine(partial, ArchiveContents.FileName), contents);

            // Last step: only now does a folder at the output path exist.
            if (Directory.Exists(output) || File.Exists(output))
            {
                throw new ArchiveExportStoppedException(
                    "Something appeared at the OutputPath during the run. No archive folder was written.");
            }

            Directory.Move(partial, output);

            return new ArchiveExportResult
            {
                Counts = counts,
                ContentsSha256 = ArchiveHashing.Hex(SHA256.HashData(contents)),
                DifferentCopies = media.Different,
                UnreadableCopies = media.Unreadable,
                FromLaterCopy = media.FromLater,
                LowQualityLinksOutsideConversation = linksOutside,
            };
        }

        // -------------------------------------------------------------------------------------
        // archive.db
        // -------------------------------------------------------------------------------------

        private static (ArchiveCounts Counts, int LinksOutside) WriteDatabase(
            ArchiveExportSource source,
            ArchiveExportRequest request,
            IReadOnlyDictionary<long, string> originalFileNames,
            DateTime created,
            string partial,
            IProgress<ArchiveExportProgress>? progress)
        {
            var info = new ArchiveInfo
            {
                FormatVersion = ArchiveFormat.FormatVersion,
                CreatedUtc = created,
                ProducerVersion = ProducerVersion,
                ConversationTitle = source.ConversationTitle,
                SourceExportSha256 = source.SourceExportSha256,
                TimeZoneID = source.TimeZoneID,
                LocalParticipantID = request.LocalParticipantID,
            };

            var inConversation = source.States.Keys.ToHashSet();
            var linksOutside = 0;

            using var writer = ArchiveWriter.Create(Path.Combine(partial, ArchiveFormat.DatabaseFileName), info);

            foreach (var participant in source.Participants)
            {
                writer.WriteParticipant(new ArchiveParticipant
                {
                    ParticipantID = participant.ParticipantID,
                    DisplayName = participant.DisplayName,
                    IsLocal = participant.ParticipantID == request.LocalParticipantID,
                });
            }

            var assets = source.States.Values
                .Where(s => s.Status == Phase7ResolutionStatus.Resolved)
                .Select(s => s.ResolvedAsset!)
                .DistinctBy(a => a.MediaAssetID)
                .OrderBy(a => a.MediaAssetID);

            foreach (var asset in assets)
            {
                writer.WriteMedia(new ArchiveMedia
                {
                    Sha256 = source.AssetSha256[asset.MediaAssetID],
                    MediaType = asset.MediaType,
                    SizeBytes = asset.SizeBytes,
                    DurationMS = asset.DurationMS,
                    Width = asset.Width,
                    Height = asset.Height,
                });
            }

            var written = 0;

            foreach (var message in source.Messages)
            {
                var attachments = new List<ArchiveAttachment>(message.Attachments.Count);

                foreach (var position in message.Attachments)
                {
                    var state = source.States[position.AttachmentID];
                    source.Facts.Facts.TryGetValue(position.AttachmentID, out var fact);

                    var lowQualityOf = fact?.LowQualityCopyOfAttachmentID;

                    // A link to a full copy outside this conversation cannot point at a row of the
                    // archive. It is left out (null: no link known) and counted.
                    if (lowQualityOf is { } target && !inConversation.Contains(target))
                    {
                        lowQualityOf = null;
                        linksOutside++;
                    }

                    attachments.Add(state.Status == Phase7ResolutionStatus.Resolved
                        ? Restored(source, message.MessageID, position, state, originalFileNames, fact, lowQualityOf)
                        : new ArchiveAttachment
                        {
                            AttachmentID = position.AttachmentID,
                            MessageID = message.MessageID,
                            Ordinal = position.Ordinal,
                            State = ArchiveExportSource.IsMissing(state)
                                ? ArchiveAttachmentState.Missing
                                : ArchiveAttachmentState.Unmatched,
                            MediaSha256 = null,
                            OriginalFileName = null,
                            IsForwarded = fact?.IsForwarded,
                            ForwardScore = fact?.ForwardScore,
                            LowQualityCopyOfAttachmentID = lowQualityOf,
                            Basis = null,
                        });
                }

                source.MessageFacts.TryGetValue(message.MessageID, out var messageFact);

                writer.WriteMessage(new ArchiveMessage
                {
                    MessageID = message.MessageID,
                    SequenceNumber = message.SequenceNumber,
                    DateTimeLocal = message.MessageDateTimeLocal,
                    Kind = message.MessageType == MessageType.System ? ArchiveMessageKind.System : ArchiveMessageKind.User,
                    SenderParticipantID = message.SenderParticipantID,
                    Text = message.MessageContent,

                    // From the message's msgstore fact (CH-22). No row: null, "not known", never
                    // "not forwarded".
                    IsForwarded = messageFact?.IsForwarded,
                    ForwardScore = messageFact?.ForwardScore,

                    Attachments = attachments,
                });

                written++;

                if (written % ConversationReader.MaximumPageSize == 0 || written == source.Messages.Count)
                {
                    progress?.Report(new ArchiveExportProgress(
                        ArchiveExportStage.WritingArchive, written, source.Messages.Count));
                }
            }

            return (writer.Complete(), linksOutside);
        }

        private static ArchiveAttachment Restored(
            ArchiveExportSource source,
            long messageID,
            ConversationAttachment position,
            Phase7ReconstructionState state,
            IReadOnlyDictionary<long, string> originalFileNames,
            MsgstoreFacts.AttachmentMsgstoreFact? fact,
            long? lowQualityOf)
        {
            var assetID = state.ResolvedAsset!.MediaAssetID;
            var basis = source.Basis[position.AttachmentID];

            return new ArchiveAttachment
            {
                AttachmentID = position.AttachmentID,
                MessageID = messageID,
                Ordinal = position.Ordinal,
                State = ArchiveAttachmentState.Restored,
                MediaSha256 = source.AssetSha256[assetID],
                OriginalFileName = originalFileNames[assetID],
                IsForwarded = fact?.IsForwarded,
                ForwardScore = fact?.ForwardScore,
                LowQualityCopyOfAttachmentID = lowQualityOf,
                Basis = new ArchiveAttachmentBasis
                {
                    BasisType = BasisTypeText(basis.IndependentBasisType!.Value),
                    BasisText = basis.IndependentBasisDescription!,
                    Actor = basis.ActorLabel,
                    ConfirmedUtc = basis.OccurredAtUtc,
                },
            };
        }

        /// <summary>
        /// The archive's spelling of a basis type: the workspace's own names, mapped explicitly so a
        /// renamed enum member cannot silently change what an archive says.
        /// </summary>
        internal static string BasisTypeText(Phase7IndependentBasisType type) => type switch
        {
            Phase7IndependentBasisType.HumanContentOrContextInspection => "HumanContentOrContextInspection",
            Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth => "ExternalProvenanceOrGroundTruth",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a basis type the archive knows."),
        };

        // -------------------------------------------------------------------------------------
        // Media.
        // -------------------------------------------------------------------------------------

        private sealed record MediaOutcome(
            Dictionary<long, string> Stored, int Failed, int Different, int Unreadable, int FromLater);

        /// <summary>
        /// For each resolved payload, the recorded copies in the location reader's order: the first
        /// whose bytes hash to the recorded SHA-256 is stored as <c>media/&lt;sha256&gt;&lt;.ext&gt;</c>.
        /// Once one payload has failed nothing more is copied; the remaining ones are only hashed, so
        /// the count of failures is complete.
        /// </summary>
        /// <returns>For each stored payload, the file name of the copy it was taken from.</returns>
        private MediaOutcome CopyMedia(
            ArchiveExportSource source, string partial, IProgress<ArchiveExportProgress>? progress)
        {
            var folder = Path.Combine(partial, ArchiveFormat.MediaFolderName);
            Directory.CreateDirectory(folder);

            BeforeMediaCopiedForTests?.Invoke(folder);

            var stored = new Dictionary<long, string>();
            int failed = 0, different = 0, unreadable = 0, fromLater = 0, done = 0;

            foreach (var (assetID, expected) in source.AssetSha256.OrderBy(pair => pair.Key))
            {
                var locations = source.Locations.GetValueOrDefault(assetID) ?? [];
                string? taken = null;

                for (var index = 0; index < locations.Count && taken is null; index++)
                {
                    var location = locations[index];
                    var target = Path.Combine(folder, ArchiveFormat.MediaFileName(expected, location.FileName));
                    var temporary = target + ".tmp";
                    string actual;

                    try
                    {
                        if (!File.Exists(location.FullPath))
                        {
                            unreadable++;
                            continue;
                        }

                        actual = failed == 0
                            ? ArchiveHashing.CopyWithHash(location.FullPath, temporary).Sha256
                            : ArchiveHashing.Sha256OfFile(location.FullPath);
                    }
                    catch (ArchiveDestinationWriteException exception)
                    {
                        // The archive folder could not take the file (a full disk, a blocked name).
                        // Another copy would fail the same way, so this stops rather than falls through.
                        throw DestinationFailure(exception);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        TryDelete(temporary);
                        unreadable++;
                        continue;
                    }

                    if (actual != expected)
                    {
                        TryDelete(temporary);
                        different++;
                        continue;
                    }

                    if (failed == 0)
                    {
                        try
                        {
                            File.Move(temporary, target);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            throw DestinationFailure(exception);
                        }
                    }

                    taken = location.FileName;

                    if (index > 0)
                    {
                        fromLater++;
                    }
                }

                if (taken is null)
                {
                    failed++;
                }
                else
                {
                    stored[assetID] = taken;
                }

                progress?.Report(new ArchiveExportProgress(
                    ArchiveExportStage.CopyingMedia, ++done, source.AssetSha256.Count));
            }

            return new MediaOutcome(stored, failed, different, unreadable, fromLater);
        }

        private static ArchiveExportStoppedException DestinationFailure(Exception exception) =>
            new(
                "A media file could not be written into the partial folder (is the disk full?). No archive " +
                "folder was written.",
                exception);

        // -------------------------------------------------------------------------------------
        // The partial folder.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Creates the partial folder and proves this run owns it: a marker written with
        /// <see cref="FileMode.CreateNew"/>, and nothing else in the folder. Only an owned folder is
        /// ever removed on a stop, so a folder some other run or person created in the meantime is left
        /// alone.
        /// </summary>
        /// <exception cref="ArchiveExportStoppedException">The folder was not this run's. It was left as found.</exception>
        private static void ClaimPartial(string partial)
        {
            const string NotOurs =
                "A folder named like the OutputPath with .partial on the end appeared during the run. It " +
                "was left alone. No archive folder was written.";

            if (File.Exists(partial))
            {
                throw new ArchiveExportStoppedException(NotOurs);
            }

            Directory.CreateDirectory(partial);
            var marker = Path.Combine(partial, MarkerFile);

            try
            {
                using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")));
            }
            catch (IOException)
            {
                throw new ArchiveExportStoppedException(NotOurs);
            }

            if (Directory.EnumerateFileSystemEntries(partial).Count() != 1)
            {
                TryDelete(marker);
                throw new ArchiveExportStoppedException(NotOurs);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Inside the partial folder, which is removed on any stop.
            }
        }

        /// <summary>Removes the partial folder (best effort; on failure its .partial name still marks it incomplete).</summary>
        private static void Remove(string partial)
        {
            SqliteConnection.ClearAllPools();

            try
            {
                if (Directory.Exists(partial))
                {
                    Directory.Delete(partial, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left with its .partial name, which marks it incomplete.
            }
        }

        private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>Where the archive may go: a new folder, outside any repository, in a folder that exists.</summary>
    internal static class OutputGuard
    {
        /// <returns>The full path, without a trailing separator.</returns>
        /// <exception cref="ArchiveExportRefusedException">Not a usable place. Nothing was written.</exception>
        internal static string Check(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArchiveExportRefusedException("The OutputPath is empty. Nothing was written.");
            }

            string full;

            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArchiveExportRefusedException("The OutputPath is not a usable path. Nothing was written.");
            }

            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                {
                    if (IsRepository(directory.FullName) && ArchivePaths.IsWithin(full, directory.FullName))
                    {
                        throw new ArchiveExportRefusedException(
                            "The OutputPath is inside the repository. The archive holds private data and must " +
                            "be written outside it. Nothing was written.");
                    }
                }
            }

            // Also walk up from the output itself, so any repository is caught wherever the program
            // happens to run from.
            for (var directory = Path.GetDirectoryName(full) is { } parent ? new DirectoryInfo(parent) : null;
                 directory is not null;
                 directory = directory.Parent)
            {
                if (IsRepository(directory.FullName))
                {
                    throw new ArchiveExportRefusedException(
                        "The OutputPath is inside a repository. The archive holds private data and must be " +
                        "written outside it. Nothing was written.");
                }
            }

            if (File.Exists(full) || Directory.Exists(full))
            {
                throw new ArchiveExportRefusedException(
                    "Something already exists at the OutputPath; an archive is never written over anything. " +
                    "Nothing was written.");
            }

            if (File.Exists(full + ArchiveExporter.PartialSuffix) || Directory.Exists(full + ArchiveExporter.PartialSuffix))
            {
                throw new ArchiveExportRefusedException(
                    "A folder named like the OutputPath with .partial on the end already exists: an earlier " +
                    "run's incomplete archive. Delete it first. Nothing was written.");
            }

            if (Path.GetDirectoryName(full) is not { } folder || !Directory.Exists(folder))
            {
                throw new ArchiveExportRefusedException(
                    "The folder that should hold the OutputPath does not exist. Nothing was written.");
            }

            return full;
        }

        private static bool IsRepository(string directory) =>
            File.Exists(Path.Combine(directory, "Chatcheology.slnx"))
            || Directory.Exists(Path.Combine(directory, ".git"))
            || File.Exists(Path.Combine(directory, ".git"));
    }
}
