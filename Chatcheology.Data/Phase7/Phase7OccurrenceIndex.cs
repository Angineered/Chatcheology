using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Everything Stage 7.1 needs to know about physical copies, read in one ordered pass and held in
    /// memory: which files support which asset on which date, what token each dated file's name
    /// yields, and whether each <c>(FileDate, SequenceToken)</c> key maps to exactly one payload.
    /// </summary>
    /// <remarks>
    /// Derived here rather than obtained by widening a frozen Phase 6 type.
    /// <c>AttachmentMatchCandidate</c> deliberately carries copy <em>counts</em> and no file
    /// identifiers, and <c>MediaEvidenceIndex</c>'s statement deliberately names no <c>FileName</c> or
    /// <c>Extension</c> — which is what makes "no filename evidence participates" a property of that
    /// query rather than a promise. Stage 7.1 needs both a file-id list and the name, so it issues its
    /// own statement instead of loosening either of theirs. Agreement is then <em>proved</em> per
    /// candidate at run time against <c>SupportingPhysicalCopyCount</c>, which is stronger than
    /// sharing a field: it holds against real data, in every run, and fails loudly instead of
    /// drifting.
    /// <para>
    /// <c>FileName</c> and <c>Extension</c> are read here and nowhere else, and only to recover the
    /// four approved digits. No name, path, extension or source identity is written to any Phase 7
    /// row.
    /// </para>
    /// <para>
    /// The pass also restates the completed-Phase-5 eligibility rules, as Stage B2B does: every
    /// <c>MediaFile</c> must be hashed, linked to exactly one <c>MediaAsset</c>, and record the same
    /// SHA-256 as that asset; every non-null <c>FileDate</c> must parse under the one sanctioned
    /// format; and every dated name must carry a marker agreeing with its stored date.
    /// </para>
    /// <para>
    /// What is kept is bounded by the archive rather than by the conversation: one entry per dated
    /// <c>(asset, date)</c> pair, one token per dated file, and one entry per
    /// <c>(date, token)</c> key.
    /// </para>
    /// </remarks>
    internal sealed class Phase7OccurrenceIndex
    {
        private readonly Dictionary<(long MediaAssetID, DateOnly FileDate), List<OccurrenceFile>>
            _supportingFiles;

        private readonly Dictionary<(DateOnly FileDate, ushort Token), HashSet<long>> _keyPayloads;

        private readonly HashSet<long> _zeroByteAssets;

        private Phase7OccurrenceIndex(
            Dictionary<(long, DateOnly), List<OccurrenceFile>> supportingFiles,
            Dictionary<(DateOnly, ushort), HashSet<long>> keyPayloads,
            HashSet<long> zeroByteAssets,
            int datedFileCount,
            int supportedTokenFileCount)
        {
            _supportingFiles = supportingFiles;
            _keyPayloads = keyPayloads;
            _zeroByteAssets = zeroByteAssets;

            DatedFileCount = datedFileCount;
            SupportedTokenFileCount = supportedTokenFileCount;
        }

        /// <summary>Physical files carrying a naming-derived date.</summary>
        internal int DatedFileCount { get; }

        /// <summary>Dated physical files whose name yields a supported token.</summary>
        internal int SupportedTokenFileCount { get; }

        /// <summary>
        /// The files linked to <paramref name="mediaAssetID"/> whose <c>FileDate</c> is exactly
        /// <paramref name="fileDate"/>, in ascending <c>MediaFileID</c> order.
        /// </summary>
        /// <remarks>
        /// Character for character the set <c>WorkspaceMatchingService</c> counts as
        /// <c>SupportingPhysicalCopyCount</c> for an exact-date candidate:
        /// <c>BuildExactDateCandidates</c> passes exactly <c>media.CopiesOn(mediaAssetID,
        /// messageDate)</c> to <c>CreateCandidate</c>, which sums <c>copies.CopyCount</c>.
        /// <para>
        /// Ordered so a run is reproducible. Grammar-unsupported copies are included, which is
        /// required both for the count proof and for honest evidence — a copy whose name says nothing
        /// is still a copy that supports the date relation.
        /// </para>
        /// </remarks>
        internal IReadOnlyList<OccurrenceFile> SupportingFilesOn(
            long mediaAssetID, DateOnly fileDate) =>
            _supportingFiles.TryGetValue((mediaAssetID, fileDate), out var files) ? files : [];

        /// <summary>
        /// Whether <c>(<paramref name="fileDate"/>, <paramref name="token"/>)</c> maps to exactly one
        /// <c>MediaAsset</c> across the whole workspace.
        /// </summary>
        /// <remarks>
        /// The population is Stage B1's L1 level: every <c>MediaFile</c> with a non-null
        /// <c>FileDate</c> whose name yields a supported token, counting distinct linked
        /// <c>MediaAssetID</c>s. Zero-byte assets are counted, for two reasons. It is the population
        /// B1's own headline describes, so excluding them would be a <em>different</em> fact from the
        /// one B1 established; and it is the conservative direction, because including them can only
        /// turn a key from unique to colliding and therefore only ever produce more
        /// <c>NotAssessable</c>, never less.
        /// <para>
        /// Uniqueness counts payloads, not files: the archive's heavy copy duplication creates no
        /// collisions at all, because many files carrying one payload are one asset.
        /// </para>
        /// <para>
        /// This is an occurrence fact and an availability gate, never positive ranking evidence. A
        /// unique key does not make a candidate more likely to be <em>this</em> attachment's payload;
        /// a colliding one makes that occurrence's position ambiguous and therefore forces
        /// <c>NotAssessable</c>.
        /// </para>
        /// </remarks>
        internal bool IsDateTokenKeyUniqueToPayload(DateOnly fileDate, ushort token) =>
            _keyPayloads.TryGetValue((fileDate, token), out var payloads) && payloads.Count == 1;

        /// <summary>
        /// Whether <paramref name="mediaAssetID"/> holds no payload, and so can never be a candidate.
        /// </summary>
        internal bool IsZeroByteAsset(long mediaAssetID) => _zeroByteAssets.Contains(mediaAssetID);

        /// <summary>
        /// Reads and validates every media row on its own read-only connection, in one ordered pass.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The media state is not one this run can snapshot, or a stored value is not readable under
        /// the format the workspace writes. Nothing is repaired and nothing is skipped.
        /// </exception>
        internal static Phase7OccurrenceIndex Read(
            string databasePath, CancellationToken cancellationToken)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(databasePath);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a Phase 7 evidence run");

            var supportingFiles = new Dictionary<(long, DateOnly), List<OccurrenceFile>>();
            var keyPayloads = new Dictionary<(DateOnly, ushort), HashSet<long>>();
            var zeroByteAssets = new HashSet<long>();

            var datedFiles = 0;
            var supportedTokenFiles = 0;

            using var command = connection.CreateCommand();
            command.CommandText = MediaPassStatement;

            using var reader = command.ExecuteReader();

            var previousMediaFileID = -1L;

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mediaFileID = reader.GetInt64(0);

                RequireSingleAssetLink(mediaFileID, previousMediaFileID);
                RequireHashedFile(reader, mediaFileID);
                RequireAssetLink(reader, mediaFileID);
                RequireMatchingHashes(reader, mediaFileID);

                previousMediaFileID = mediaFileID;

                var mediaAssetID = reader.GetInt64(6);

                if (reader.GetInt64(8) == 0)
                {
                    zeroByteAssets.Add(mediaAssetID);
                }

                if (reader.IsDBNull(3))
                {
                    // An undated file joins no date, so it can never be a supporting copy and can
                    // never enter the uniqueness population. Stage B1 already established
                    // archive-wide that an undated name holds no marker either.
                    continue;
                }

                datedFiles++;

                var fileDate = ReadFileDate(reader, mediaFileID);
                var fileName = reader.GetString(1);
                var extension = reader.IsDBNull(2) ? null : reader.GetString(2);
                var isSent = reader.IsDBNull(4) ? null : (bool?)(reader.GetInt64(4) != 0);

                Phase7SequenceTokenGrammar.RequireMarkerAgreeingWithFileDate(
                    mediaFileID, fileName, fileDate, out var suffixStart);

                ushort? token = Phase7SequenceTokenGrammar.TryReadSupportedToken(
                    fileName, extension, suffixStart, out var readToken)
                    ? readToken
                    : null;

                if (token is { } supported)
                {
                    supportedTokenFiles++;

                    RecordKeyPayload(keyPayloads, fileDate, supported, mediaAssetID);
                }

                RecordSupportingFile(
                    supportingFiles,
                    mediaAssetID,
                    fileDate,
                    new OccurrenceFile(mediaFileID, token, isSent));
            }

            return new Phase7OccurrenceIndex(
                supportingFiles, keyPayloads, zeroByteAssets, datedFiles, supportedTokenFiles);
        }

        /// <summary>
        /// The one media statement Stage 7.1 issues.
        /// </summary>
        /// <remarks>
        /// The same shape Stage B2B's own pass uses, ordered by <c>MediaFileID</c> so the supporting
        /// lists come out ascending without a sort and so a duplicate asset link shows up as two
        /// adjacent rows for one file.
        /// </remarks>
        private const string MediaPassStatement =
            """
            SELECT
                f.MediaFileID,
                f.FileName,
                f.Extension,
                f.FileDate,
                f.IsSent,
                f.SHA256,
                l.MediaAssetID,
                a.SHA256,
                a.SizeBytes
            FROM MediaFile AS f
            LEFT JOIN MediaAssetFile AS l ON l.MediaFileID = f.MediaFileID
            LEFT JOIN MediaAsset AS a ON a.MediaAssetID = l.MediaAssetID
            ORDER BY f.MediaFileID;
            """;

        private static void RecordSupportingFile(
            Dictionary<(long, DateOnly), List<OccurrenceFile>> supportingFiles,
            long mediaAssetID,
            DateOnly fileDate,
            OccurrenceFile file)
        {
            var key = (mediaAssetID, fileDate);

            if (!supportingFiles.TryGetValue(key, out var files))
            {
                files = [];
                supportingFiles[key] = files;
            }

            files.Add(file);
        }

        private static void RecordKeyPayload(
            Dictionary<(DateOnly, ushort), HashSet<long>> keyPayloads,
            DateOnly fileDate,
            ushort token,
            long mediaAssetID)
        {
            var key = (fileDate, token);

            if (!keyPayloads.TryGetValue(key, out var payloads))
            {
                payloads = [];
                keyPayloads[key] = payloads;
            }

            payloads.Add(mediaAssetID);
        }

        /// <remarks>
        /// <c>MediaAssetFile</c>'s <c>UNIQUE (MediaFileID)</c> forbids two links for one file, so two
        /// adjacent rows for one <c>MediaFileID</c> means the workspace was written with foreign keys
        /// and unique constraints disabled and its deduplication cannot be trusted.
        /// </remarks>
        private static void RequireSingleAssetLink(long mediaFileID, long previousMediaFileID)
        {
            if (mediaFileID != previousMediaFileID)
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} is linked to more than one MediaAsset. The workspace's own " +
                $"unique constraint forbids it, so this workspace was written with enforcement " +
                $"disabled and its media relationships cannot be trusted. Nothing has been published " +
                $"and the workspace is unchanged.");
        }

        private static void RequireHashedFile(SqliteDataReader reader, long mediaFileID)
        {
            if (!reader.IsDBNull(5))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has no SHA-256, so media hashing is incomplete and Phase 5 " +
                $"has not finished for this workspace. An unhashed file belongs to no asset and would " +
                $"silently drop out of the evidence. Nothing has been published and the workspace is " +
                $"unchanged.");
        }

        private static void RequireAssetLink(SqliteDataReader reader, long mediaFileID)
        {
            if (reader.IsDBNull(6))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is hashed but linked to no MediaAsset, so deduplication " +
                    $"is incomplete for this workspace. Nothing has been published and the workspace " +
                    $"is unchanged.");
            }

            if (reader.IsDBNull(7))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is linked to a MediaAsset that does not exist. The " +
                    $"workspace's foreign keys forbid this, so it was written with enforcement " +
                    $"disabled. Nothing has been published and the workspace is unchanged.");
            }
        }

        /// <remarks>
        /// Compared case-insensitively because both hash columns are declared <c>COLLATE NOCASE</c>:
        /// to the database one hash written in two cases is one value.
        /// </remarks>
        private static void RequireMatchingHashes(SqliteDataReader reader, long mediaFileID)
        {
            if (string.Equals(
                reader.GetString(5), reader.GetString(7), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} records a different SHA-256 from the MediaAsset it is " +
                $"linked to, so the file's content identity and its deduplication disagree. One of " +
                $"them is wrong and this run cannot tell which. Nothing has been published and the " +
                $"workspace is unchanged.");
        }

        private static DateOnly ReadFileDate(SqliteDataReader reader, long mediaFileID)
        {
            if (WorkspaceDateFormats.TryParseFileDate(reader.GetString(3), out var fileDate))
            {
                return fileDate;
            }

            // The stored text is not quoted back: a date is harmless, but a diagnostic that repeats
            // stored media values is a habit rather than a special case.
            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has a FileDate that is not the calendar date format this " +
                $"workspace writes. It is not guessed at under another format, because a date read " +
                $"the wrong way is indistinguishable afterwards from one the archive really " +
                $"recorded. Nothing has been published and the workspace is unchanged.");
        }

        /// <summary>
        /// One physical copy, reduced to the three facts an occurrence row needs.
        /// </summary>
        /// <param name="MediaFileID">The copy itself, stored as a plain reference.</param>
        /// <param name="Token">
        /// The approved four-digit token its name yields, or <see langword="null"/> where the name is
        /// not that shape. A null token is unsupported grammar, never a negative reading.
        /// </param>
        /// <param name="IsSent">
        /// The copy's folder direction evidence, kept only to assert the frozen compatible-candidate
        /// invariant. It is never written to a Phase 7 row.
        /// </param>
        internal readonly record struct OccurrenceFile(long MediaFileID, ushort? Token, bool? IsSent);
    }
}
