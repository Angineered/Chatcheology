using Chatcheology.Data.Media;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// Everything the analysis needs to know about the workspace's media, read in one pass and held
    /// in memory for the whole run.
    /// </summary>
    /// <remarks>
    /// Built this way because of what the schema does not have. There is no index on
    /// <c>MediaFile.FileDate</c> or on <c>MediaAssetFile.MediaAssetID</c>, so a query per attachment
    /// would scan every media row again for every attachment — tens of thousands of rows, thousands
    /// of times over. One ordered pass reads each row once instead, and the lookups it leaves behind
    /// answer every attachment in memory.
    /// <para>
    /// The size of what is kept is bounded by the archive, not by the conversation: one entry per
    /// asset, and one entry per asset and date it has a dated copy on. Nothing here holds a row per
    /// possible attachment/asset pair, which is the combination that would not fit.
    /// </para>
    /// <para>
    /// The pass also proves the media state is one this phase can analyse at all. Schema version 2
    /// permits a half-hashed workspace — discovery legitimately precedes hashing — and analysing one
    /// would silently describe part of the archive as though it were all of it.
    /// </para>
    /// </remarks>
    internal sealed class MediaEvidenceIndex
    {
        private readonly Dictionary<long, AssetFacts> _assets;
        private readonly Dictionary<DateOnly, List<long>> _eligibleAssetsByDate;
        private readonly Dictionary<(long MediaAssetID, DateOnly FileDate), SupportingCopies> _supporting;
        private readonly Dictionary<long, SourceFacts> _sources;

        private MediaEvidenceIndex(
            Dictionary<long, AssetFacts> assets,
            Dictionary<DateOnly, List<long>> eligibleAssetsByDate,
            Dictionary<(long, DateOnly), SupportingCopies> supporting,
            Dictionary<long, SourceFacts> sources,
            int mediaFileWithFileDateCount,
            int mediaFileWithNullFileDateCount,
            int zeroByteAssetsExcluded,
            int zeroBytePhysicalFilesRepresented,
            int noDateEvidenceAssetPoolCount)
        {
            _assets = assets;
            _eligibleAssetsByDate = eligibleAssetsByDate;
            _supporting = supporting;
            _sources = sources;

            MediaFileWithFileDateCount = mediaFileWithFileDateCount;
            MediaFileWithNullFileDateCount = mediaFileWithNullFileDateCount;
            ZeroByteAssetsExcluded = zeroByteAssetsExcluded;
            ZeroBytePhysicalFilesRepresentedByExcludedAsset = zeroBytePhysicalFilesRepresented;
            NoDateEvidenceAssetPoolCount = noDateEvidenceAssetPoolCount;
        }

        /// <summary>Physical files carrying a naming-derived date.</summary>
        internal int MediaFileWithFileDateCount { get; }

        /// <summary>Physical files carrying no naming-derived date.</summary>
        internal int MediaFileWithNullFileDateCount { get; }

        /// <summary>Assets holding no payload, which are never candidates.</summary>
        internal int ZeroByteAssetsExcluded { get; }

        /// <summary>Physical files represented by those excluded assets.</summary>
        internal int ZeroBytePhysicalFilesRepresentedByExcludedAsset { get; }

        /// <summary>Eligible assets no copy of which carries a date.</summary>
        internal int NoDateEvidenceAssetPoolCount { get; }

        /// <summary>The media sources, in <c>MediaSourceID</c> order.</summary>
        internal IEnumerable<SourceFacts> Sources =>
            _sources.Values.OrderBy(source => source.MediaSourceID);

        /// <summary>The asset facts for <paramref name="mediaAssetID"/>.</summary>
        internal AssetFacts Asset(long mediaAssetID) => _assets[mediaAssetID];

        /// <summary>
        /// The candidate-eligible assets holding a copy dated <paramref name="fileDate"/>, in
        /// <c>MediaAssetID</c> order, or an empty list when that date carries none.
        /// </summary>
        internal IReadOnlyList<long> EligibleAssetsOn(DateOnly fileDate) =>
            _eligibleAssetsByDate.TryGetValue(fileDate, out var assets) ? assets : [];

        /// <summary>
        /// The copies of <paramref name="mediaAssetID"/> dated exactly
        /// <paramref name="fileDate"/>, or <see langword="null"/> when it has none that day.
        /// </summary>
        internal SupportingCopies? CopiesOn(long mediaAssetID, DateOnly fileDate) =>
            _supporting.TryGetValue((mediaAssetID, fileDate), out var copies) ? copies : null;

        /// <summary>Records that a source supplied evidence for one exact-date relationship.</summary>
        internal void CountExactCandidateRelationContribution(long mediaSourceID) =>
            _sources[mediaSourceID].ExactCandidateRelationsContributed++;

        /// <summary>
        /// How many eligible assets sit on each date that carries any, described as a spread.
        /// </summary>
        /// <remarks>
        /// The median is the lower of the two middle values for an even number of dates. Stated
        /// here rather than left implicit so a second run of this census is comparable with the
        /// first.
        /// </remarks>
        internal AssetsPerDateDensity DescribeDensity()
        {
            if (_eligibleAssetsByDate.Count == 0)
            {
                return new AssetsPerDateDensity
                {
                    DatedEligibleMediaDateCount = 0,
                    Minimum = 0,
                    Median = 0,
                    Maximum = 0,
                };
            }

            var counts = _eligibleAssetsByDate.Values.Select(assets => assets.Count).ToList();
            counts.Sort();

            return new AssetsPerDateDensity
            {
                DatedEligibleMediaDateCount = counts.Count,
                Minimum = counts[0],
                Median = counts[(counts.Count - 1) / 2],
                Maximum = counts[^1],
            };
        }

        /// <summary>
        /// Reads and validates every media row on <paramref name="connection"/> in one ordered pass.
        /// </summary>
        /// <remarks>
        /// Only the columns this phase is entitled to use are selected. No root path, relative path,
        /// file name or extension appears in the statement at all, which is what makes "no filename
        /// or path evidence participates" a property of the query rather than a promise about the
        /// code above it. The two hash columns are read for one purpose: proving the file and the
        /// asset it is linked to agree about what payload it holds.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The media state is not one Phase 6 can analyse, or a stored value is not readable under
        /// the format the workspace writes. Nothing is repaired and nothing is skipped.
        /// </exception>
        internal static MediaEvidenceIndex Read(
            SqliteConnection connection, CancellationToken cancellationToken)
        {
            var assets = new Dictionary<long, AssetFacts>();
            var supporting = new Dictionary<(long, DateOnly), SupportingCopies>();
            var sources = new Dictionary<long, SourceFacts>();

            var withFileDate = 0;
            var withoutFileDate = 0;

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    f.MediaFileID,
                    f.MediaSourceID,
                    f.SHA256,
                    f.FileDate,
                    f.IsSent,
                    l.MediaAssetID,
                    a.SHA256,
                    a.MediaType,
                    a.SizeBytes
                FROM MediaFile AS f
                LEFT JOIN MediaAssetFile AS l ON l.MediaFileID = f.MediaFileID
                LEFT JOIN MediaAsset AS a ON a.MediaAssetID = l.MediaAssetID
                ORDER BY f.MediaFileID;
                """;

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mediaFileID = reader.GetInt64(0);
                var mediaSourceID = reader.GetInt64(1);

                RequireHashedFile(reader, mediaFileID);
                RequireAssetLink(reader, mediaFileID);
                RequireMatchingHashes(reader, mediaFileID);

                var fileDate = ReadFileDate(reader, mediaFileID);
                var isSent = reader.IsDBNull(4) ? null : (bool?)(reader.GetInt64(4) != 0);
                var mediaAssetID = reader.GetInt64(5);

                var asset = ResolveAsset(assets, reader, mediaAssetID);

                asset.PhysicalCopyCount++;
                asset.Sources.Add(mediaSourceID);

                var source = ResolveSource(sources, mediaSourceID);
                source.MediaFileCount++;

                if (fileDate is not { } date)
                {
                    withoutFileDate++;
                    continue;
                }

                withFileDate++;
                source.MediaFileWithFileDateCount++;
                asset.HasDatedCopy = true;

                // Zero-byte assets are counted above like any other file, because they are part of
                // the archive, but they never become date evidence: an asset with no payload cannot
                // be the media an attachment lost.
                if (asset.SizeBytes == 0)
                {
                    continue;
                }

                source.DistinctEligibleAssetsWithFileDate.Add(mediaAssetID);

                RecordSupportingCopy(supporting, mediaAssetID, date, mediaSourceID, isSent);
            }

            return Build(assets, supporting, sources, withFileDate, withoutFileDate);
        }

        /// <summary>
        /// Turns the accumulated facts into the finished lookups.
        /// </summary>
        /// <remarks>
        /// The per-date asset lists are sorted once here rather than per attachment, which is what
        /// makes candidate order deterministic without paying for a sort on every lookup.
        /// </remarks>
        private static MediaEvidenceIndex Build(
            Dictionary<long, AssetFacts> assets,
            Dictionary<(long, DateOnly), SupportingCopies> supporting,
            Dictionary<long, SourceFacts> sources,
            int withFileDate,
            int withoutFileDate)
        {
            var eligibleAssetsByDate = new Dictionary<DateOnly, List<long>>();

            foreach (var ((mediaAssetID, fileDate), _) in supporting)
            {
                if (!eligibleAssetsByDate.TryGetValue(fileDate, out var assetsOnDate))
                {
                    assetsOnDate = [];
                    eligibleAssetsByDate[fileDate] = assetsOnDate;
                }

                assetsOnDate.Add(mediaAssetID);
            }

            foreach (var assetsOnDate in eligibleAssetsByDate.Values)
            {
                assetsOnDate.Sort();
            }

            var zeroByteAssets = 0;
            var zeroByteFiles = 0;
            var noDateEvidenceAssets = 0;

            foreach (var asset in assets.Values)
            {
                if (asset.SizeBytes == 0)
                {
                    zeroByteAssets++;
                    zeroByteFiles += asset.PhysicalCopyCount;

                    continue;
                }

                if (!asset.HasDatedCopy)
                {
                    noDateEvidenceAssets++;
                }
            }

            return new MediaEvidenceIndex(
                assets,
                eligibleAssetsByDate,
                supporting,
                sources,
                withFileDate,
                withoutFileDate,
                zeroByteAssets,
                zeroByteFiles,
                noDateEvidenceAssets);
        }

        private static AssetFacts ResolveAsset(
            Dictionary<long, AssetFacts> assets, SqliteDataReader reader, long mediaAssetID)
        {
            if (assets.TryGetValue(mediaAssetID, out var existing))
            {
                return existing;
            }

            var asset = new AssetFacts
            {
                MediaAssetID = mediaAssetID,
                MediaType = MediaTypeText.Parse(reader.GetString(7)),
                SizeBytes = reader.GetInt64(8),
            };

            assets[mediaAssetID] = asset;

            return asset;
        }

        private static SourceFacts ResolveSource(
            Dictionary<long, SourceFacts> sources, long mediaSourceID)
        {
            if (sources.TryGetValue(mediaSourceID, out var existing))
            {
                return existing;
            }

            var source = new SourceFacts { MediaSourceID = mediaSourceID };
            sources[mediaSourceID] = source;

            return source;
        }

        private static void RecordSupportingCopy(
            Dictionary<(long, DateOnly), SupportingCopies> supporting,
            long mediaAssetID,
            DateOnly fileDate,
            long mediaSourceID,
            bool? isSent)
        {
            var key = (mediaAssetID, fileDate);

            if (!supporting.TryGetValue(key, out var copies))
            {
                copies = new SupportingCopies();
                supporting[key] = copies;
            }

            copies.CopyCount++;
            copies.Sources.Add(mediaSourceID);

            switch (isSent)
            {
                case true:
                    copies.HasSentFolderCopy = true;
                    break;

                case false:
                    copies.HasNotUnderSentFolderCopy = true;
                    break;

                default:
                    copies.HasDirectionUnknownCopy = true;
                    break;
            }
        }

        /// <remarks>
        /// A file with no hash has not been through Phase 5. Skipping it quietly would leave the
        /// census describing whatever fraction of the archive happened to be hashed as though it
        /// were the archive, which is precisely the kind of confident wrong answer this phase
        /// exists to avoid.
        /// </remarks>
        private static void RequireHashedFile(SqliteDataReader reader, long mediaFileID)
        {
            if (!reader.IsDBNull(2))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has no SHA-256, so media hashing is incomplete and " +
                $"Phase 5 has not finished for this workspace. Matching analysis needs every " +
                $"physical file to carry a content identity, because an unhashed file belongs to " +
                $"no asset and would silently drop out of the evidence. Run the hashing pass to " +
                $"completion first. Nothing has been analysed and the workspace is unchanged.");
        }

        private static void RequireAssetLink(SqliteDataReader reader, long mediaFileID)
        {
            if (reader.IsDBNull(5))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is hashed but linked to no MediaAsset, so " +
                    $"deduplication is incomplete for this workspace. Every hashed file belongs to " +
                    $"exactly one asset once Phase 5 has finished. Nothing has been analysed and " +
                    $"the workspace is unchanged.");
            }

            // The asset half of the join is null while the link itself is not: a MediaAssetFile row
            // pointing at an asset that does not exist. The foreign key forbids it, so reaching
            // this means the workspace was written by something that had foreign keys turned off.
            if (reader.IsDBNull(6))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is linked to a MediaAsset that does not exist. The " +
                    $"workspace's foreign keys forbid this, so it was written with enforcement " +
                    $"disabled and its media relationships cannot be trusted. Nothing has been " +
                    $"analysed and the workspace is unchanged.");
            }
        }

        /// <remarks>
        /// Compared with <see cref="StringComparison.OrdinalIgnoreCase"/> because both hash columns
        /// are declared <c>COLLATE NOCASE</c>: to the database a hash written in lower case and the
        /// same hash written in upper case are one value, and a case-sensitive comparison here
        /// would report a perfectly sound workspace as corrupt.
        /// </remarks>
        private static void RequireMatchingHashes(SqliteDataReader reader, long mediaFileID)
        {
            if (string.Equals(reader.GetString(2), reader.GetString(6), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} records a different SHA-256 from the MediaAsset it is " +
                $"linked to, so the file's content identity and its deduplication disagree. One of " +
                $"them is wrong and this analysis cannot tell which. Nothing has been analysed and " +
                $"the workspace is unchanged.");
        }

        private static DateOnly? ReadFileDate(SqliteDataReader reader, long mediaFileID)
        {
            if (reader.IsDBNull(3))
            {
                return null;
            }

            var stored = reader.GetString(3);

            if (WorkspaceDateFormats.TryParseFileDate(stored, out var fileDate))
            {
                return fileDate;
            }

            // The stored text is not quoted back: a date is harmless, but a diagnostic that repeats
            // stored media values is a habit rather than a special case.
            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has a FileDate that is not the calendar date format this " +
                $"workspace writes. It is not guessed at under another format, because a date read " +
                $"the wrong way is indistinguishable afterwards from one the archive really " +
                $"recorded. Nothing has been analysed and the workspace is unchanged.");
        }

        /// <summary>What is true of one unique payload wherever it survives.</summary>
        internal sealed class AssetFacts
        {
            internal required long MediaAssetID { get; init; }

            internal required MediaType MediaType { get; init; }

            internal required long SizeBytes { get; init; }

            /// <summary>How many physical files carry this payload, in total.</summary>
            internal int PhysicalCopyCount { get; set; }

            /// <summary>Which sources hold a copy, in total.</summary>
            internal HashSet<long> Sources { get; } = [];

            /// <summary>Whether any copy carries a naming-derived date.</summary>
            internal bool HasDatedCopy { get; set; }
        }

        /// <summary>
        /// The copies of one asset that share one calendar date, which are the only copies entitled
        /// to speak for a candidate relationship resting on that date.
        /// </summary>
        internal sealed class SupportingCopies
        {
            internal int CopyCount { get; set; }

            internal HashSet<long> Sources { get; } = [];

            internal bool HasSentFolderCopy { get; set; }

            internal bool HasNotUnderSentFolderCopy { get; set; }

            internal bool HasDirectionUnknownCopy { get; set; }
        }

        /// <summary>What one media source holds, and what it went on to support.</summary>
        internal sealed class SourceFacts
        {
            internal required long MediaSourceID { get; init; }

            internal int MediaFileCount { get; set; }

            internal int MediaFileWithFileDateCount { get; set; }

            internal HashSet<long> DistinctEligibleAssetsWithFileDate { get; } = [];

            internal int ExactCandidateRelationsContributed { get; set; }
        }
    }
}
