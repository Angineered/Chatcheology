using System.Globalization;
using System.Numerics;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Measures what the approved four-digit WA sequence does across date, acquisition source, device
    /// group and physical copy.
    /// </summary>
    /// <remarks>
    /// Read-only, in the sense SQLite itself enforces: the connection is opened <c>Mode=ReadOnly</c>, so a
    /// stray write fails rather than succeeding quietly. Nothing is persisted, no schema is touched, no
    /// index is created, and no media file is opened.
    /// <para>
    /// This is a behaviour census and nothing more. It reaches no conclusion about any attachment, it
    /// generates no candidates, it measures no ordering against message order, and it names no scope as
    /// the counter's namespace. Which added scope component reduces ambiguity is what it measures.
    /// </para>
    /// <para>
    /// A separate path from both the matching engine and the Stage A suffix census.
    /// <c>WorkspaceMatchingService</c> and <c>FileNameSuffixCensusService</c> stay exactly as they are —
    /// the Stage A census cannot serve this one in any case, because its accumulator carries only whether
    /// a file had a date and never which date it was.
    /// </para>
    /// <para>
    /// Every hard gate here aborts. A mapping failure, a completed-Phase-5 failure, a marker or date
    /// disagreement, a monotonic gate, a population reconciliation or a date-set accounting throws, and
    /// no census — not even a partial or flagged one — is returned.
    /// </para>
    /// </remarks>
    public sealed class WaSequenceScopeCensusService
    {
        /// <summary>The approved grammar's exact suffix width.</summary>
        private const int SupportedTokenLength = 4;

        /// <summary>How many values four ASCII digits can hold, which bounds every token.</summary>
        private const int TokenDomainSize = 10_000;

        /// <summary>
        /// Censuses the supported sequence evidence in the workspace named by <paramref name="request"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The request names no workspace.</exception>
        /// <exception cref="FileNotFoundException">There is no workspace at that path.</exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version; the device-group mapping does not name
        /// every source exactly once; the media state is not one this census can describe; a recovered
        /// name and its persisted date disagree; or a reconciliation or monotonic gate failed. Nothing is
        /// repaired and no census is returned.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was signalled. No census is returned.
        /// </exception>
        public WaSequenceScopeCensus Analyse(
            WaSequenceScopeCensusRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);
            ArgumentNullException.ThrowIfNull(request.DeviceGroups);

            // Checked before any work, so a cancelled call cannot return a census merely because the
            // workspace it was pointed at happened to hold nothing to iterate.
            cancellationToken.ThrowIfCancellationRequested();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(request.DatabasePath);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a sequence scope census");

            var sources = ReadMediaSources(connection);
            var groupBySource = ResolveDeviceGroups(request.DeviceGroups, sources);

            var accumulator = new CensusAccumulator(sources, groupBySource);

            ReadMediaFiles(connection, accumulator, cancellationToken);

            return accumulator.Build(cancellationToken);
        }

        private static List<(long MediaSourceID, string SourceType)> ReadMediaSources(
            SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT MediaSourceID, SourceType FROM MediaSource ORDER BY MediaSourceID;";

            var sources = new List<(long, string)>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                sources.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            return sources;
        }

        /// <summary>
        /// Turns the caller's assignments into a source-to-group lookup, refusing anything that is not a
        /// total, single-valued function over exactly the sources present.
        /// </summary>
        /// <remarks>
        /// There is deliberately no default. A silent one-group-per-source fallback would reinstate the
        /// assumption that an acquisition store is a numbering authority, which is the assumption this
        /// census exists to test rather than to make.
        /// <para>
        /// Validated here, before <c>MediaFile</c> is opened, so a malformed mapping cannot move a counter
        /// before it is caught.
        /// </para>
        /// </remarks>
        private static Dictionary<long, long> ResolveDeviceGroups(
            IReadOnlyList<DeviceGroupAssignment> assignments,
            List<(long MediaSourceID, string SourceType)> sources)
        {
            if (assignments.Count == 0)
            {
                throw new InvalidOperationException(
                    "No device-group assignment was supplied. This census measures whether acquisition " +
                    "sources share a numbering authority, so it will not assume one group per source on " +
                    "the caller's behalf: a caller who wants that must say so by supplying it. Nothing " +
                    "has been censused and the workspace is unchanged.");
            }

            var groupBySource = new Dictionary<long, long>();

            foreach (var assignment in assignments)
            {
                ArgumentNullException.ThrowIfNull(assignment);

                if (!groupBySource.TryAdd(assignment.MediaSourceID, assignment.DeviceGroupID))
                {
                    throw new InvalidOperationException(
                        $"MediaSource {assignment.MediaSourceID} is assigned to a device group more " +
                        $"than once. One source belongs to one numbering authority, and a mapping that " +
                        $"says otherwise cannot be applied. Nothing has been censused and the workspace " +
                        $"is unchanged.");
                }
            }

            foreach (var (mediaSourceID, _) in sources)
            {
                if (!groupBySource.ContainsKey(mediaSourceID))
                {
                    throw new InvalidOperationException(
                        $"MediaSource {mediaSourceID} exists in the workspace but was assigned to no " +
                        $"device group. Every source must be assigned, because a source left out would " +
                        $"be silently absent from every device-scoped figure. Nothing has been censused " +
                        $"and the workspace is unchanged.");
                }
            }

            foreach (var mediaSourceID in groupBySource.Keys)
            {
                if (!sources.Exists(source => source.MediaSourceID == mediaSourceID))
                {
                    throw new InvalidOperationException(
                        $"The device-group mapping names MediaSource {mediaSourceID}, which this " +
                        $"workspace does not contain. A mapping written for a different workspace would " +
                        $"describe groups this one cannot fill. Nothing has been censused and the " +
                        $"workspace is unchanged.");
                }
            }

            return groupBySource;
        }

        /// <summary>
        /// Reads and validates every media row in one ordered pass, recording each supported file once.
        /// </summary>
        /// <remarks>
        /// Only the columns this census is entitled to use are selected. No root path, relative path,
        /// display name, device description, message, attachment, conversation or participant appears in
        /// the statement, which keeps that boundary a property of the query rather than a promise about
        /// the code above it. The two hash columns are read for one purpose — proving a file and its asset
        /// agree about the payload — and are never emitted.
        /// <para>
        /// The join to <c>MediaAssetFile</c> could in principle return a file twice, if a workspace
        /// somehow held two links for one file. The rows are ordered by <c>MediaFileID</c> so such a pair
        /// would be adjacent, and the second is refused before any counter moves.
        /// </para>
        /// </remarks>
        private static void ReadMediaFiles(
            SqliteConnection connection,
            CensusAccumulator accumulator,
            CancellationToken cancellationToken)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    f.MediaFileID,
                    f.MediaSourceID,
                    f.FileName,
                    f.Extension,
                    f.FileDate,
                    f.SHA256,
                    l.MediaAssetID,
                    a.SHA256,
                    a.SizeBytes
                FROM MediaFile AS f
                LEFT JOIN MediaAssetFile AS l ON l.MediaFileID = f.MediaFileID
                LEFT JOIN MediaAsset AS a ON a.MediaAssetID = l.MediaAssetID
                ORDER BY f.MediaFileID;
                """;

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

                accumulator.Add(
                    mediaFileID: mediaFileID,
                    mediaSourceID: reader.GetInt64(1),
                    fileName: reader.GetString(2),
                    extension: reader.IsDBNull(3) ? null : reader.GetString(3),
                    fileDateText: reader.IsDBNull(4) ? null : reader.GetString(4),
                    mediaAssetID: reader.GetInt64(6),
                    assetSizeBytes: reader.GetInt64(8));
            }
        }

        private static void RequireSingleAssetLink(long mediaFileID, long previousMediaFileID)
        {
            if (mediaFileID != previousMediaFileID)
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} is linked to more than one MediaAsset. One physical file " +
                $"carries one payload, and the workspace's own unique constraint says so, which makes " +
                $"this a workspace written by something that did not enforce it. Counting the file once " +
                $"per link would quietly inflate every collision figure in this census. Nothing has " +
                $"been censused and the workspace is unchanged.");
        }

        /// <remarks>
        /// These four checks are the same completed-Phase-5 rules the matching engine and the Stage A
        /// census apply, and they are deliberately a third copy rather than a shared helper: extracting
        /// one would mean editing a frozen path to serve this census, and a helper would still leave
        /// three statements of a short rule in the tree. Tests cover it on every side.
        /// </remarks>
        private static void RequireHashedFile(SqliteDataReader reader, long mediaFileID)
        {
            if (!reader.IsDBNull(5))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has no SHA-256, so media hashing is incomplete and Phase 5 " +
                $"has not finished for this workspace. A census taken now would describe whatever " +
                $"fraction of the archive happened to be hashed as though it were the archive. Nothing " +
                $"has been censused and the workspace is unchanged.");
        }

        private static void RequireAssetLink(SqliteDataReader reader, long mediaFileID)
        {
            if (reader.IsDBNull(6))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is hashed but linked to no MediaAsset, so deduplication " +
                    $"is incomplete for this workspace. Nothing has been censused and the workspace is " +
                    $"unchanged.");
            }

            if (reader.IsDBNull(7))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is linked to a MediaAsset that does not exist. The " +
                    $"workspace's foreign keys forbid this, so it was written with enforcement off. " +
                    $"Nothing has been censused and the workspace is unchanged.");
            }
        }

        private static void RequireMatchingHashes(SqliteDataReader reader, long mediaFileID)
        {
            var fileHash = reader.GetString(5);
            var assetHash = reader.GetString(7);

            if (string.Equals(fileHash, assetHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} and the MediaAsset it is linked to record different " +
                $"SHA-256 values, so the workspace disagrees with itself about which payload this file " +
                $"holds. Deduplication cannot be trusted, and neither can any figure derived from it. " +
                $"Nothing has been censused and the workspace is unchanged.");
        }

        /// <summary>
        /// Holds one compact record per supported physical file, and derives every figure from it.
        /// </summary>
        /// <remarks>
        /// The shape is deliberate. A record per supported file is bounded by the archive; a collection
        /// per scope key would be bounded by the number of keys, which is the quantity that grows fastest
        /// and the one this design removes. Every level, group scope, curve, agreement question and
        /// locality class is computed by sorting an index array over these records and walking contiguous
        /// runs.
        /// <para>
        /// Records are appended in <c>MediaFileID</c> order, so a record's own index is the final
        /// deterministic tie-breaker in every comparator. No identifier is carried for that purpose.
        /// </para>
        /// <para>
        /// Two questions the brief asks turn out to need no sets at all. "Do these observations agree?"
        /// is <c>min == max</c> over the run. "How contiguous is this group?" is a population count, a
        /// lowest and a highest over one reused bitset covering the token domain.
        /// </para>
        /// </remarks>
        private sealed class CensusAccumulator
        {
            private static readonly string[] CountBandLabels =
                ["1", "2", "3-5", "6-10", "11-25", ">25"];

            private static readonly string[] GapBandLabels =
                ["0", "1", "2", "3-5", "6-10", ">10"];

            private static readonly string[] TokenBandLabels =
            [
                "0000", "0001-0002", "0003-0005", "0006-0010", "0011-0025", "0026-0050", "0051-0100",
                "0101+",
            ];

            private static readonly string[] NameShapeLabels =
                ["OneDistinctFileName", "SeveralDistinctFileNames"];

            private static readonly string[] ExtensionLabels =
                ["ExtensionsAllEqual", "ExtensionsDiffer"];

            private static readonly string[] ZeroByteLabels =
                ["ZeroByteInvolved", "ZeroByteNotInvolved"];

            private static readonly string[] PerfectPrefixLabels =
                ["MaximumPlusOneEqualsDistinctCount", "Otherwise"];

            private readonly List<SourceState> _sources = [];
            private readonly Dictionary<long, int> _sourceIndexByID = [];
            private readonly List<long> _deviceGroupIDs = [];
            private readonly Dictionary<long, int> _deviceGroupIndexByID = [];

            private readonly Dictionary<long, int> _assetIndexByID = [];
            private readonly List<AssetState> _assets = [];
            private readonly Dictionary<DateOnly, int> _dateIndexByDate = [];
            private readonly List<DateState> _dates = [];
            private readonly Dictionary<string, int> _nameIndex = new(StringComparer.Ordinal);
            private readonly List<int> _foldedNameByName = [];
            private readonly Dictionary<string, int> _foldedNameIndex =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _extensionIndex = new(StringComparer.Ordinal);

            private readonly List<SupportedRow> _rows = [];

            private readonly HashSet<int> _scratchAssets = [];
            private readonly HashSet<int> _scratchNames = [];
            private readonly HashSet<int> _scratchFolded = [];
            private readonly ulong[] _tokenBits = new ulong[(TokenDomainSize + 63) / 64];

            private int _mediaFileCount;
            private int _datedFileCount;
            private int _undatedFileCount;
            private int _supportedFileCount;
            private int _unsupportedDatedFileCount;
            private int _supportedFromNullExtensionCount;

            /// <summary>The index reserved for a file whose recorded extension is null.</summary>
            private readonly int _nullExtensionIndex;

            internal CensusAccumulator(
                List<(long MediaSourceID, string SourceType)> sources,
                Dictionary<long, long> groupBySource)
            {
                foreach (var (mediaSourceID, sourceType) in sources)
                {
                    var deviceGroupID = groupBySource[mediaSourceID];

                    if (!_deviceGroupIndexByID.ContainsKey(deviceGroupID))
                    {
                        _deviceGroupIndexByID[deviceGroupID] = 0;
                    }

                    _sourceIndexByID[mediaSourceID] = _sources.Count;
                    _sources.Add(
                        new SourceState(_sources.Count, mediaSourceID, sourceType, deviceGroupID));
                }

                // Group indices are assigned in ascending identifier order rather than in the order the
                // sources happened to be read, so every per-group table is ordered by the caller's own
                // identifiers and two runs agree.
                _deviceGroupIDs.AddRange(_deviceGroupIndexByID.Keys);
                _deviceGroupIDs.Sort();

                for (var index = 0; index < _deviceGroupIDs.Count; index++)
                {
                    _deviceGroupIndexByID[_deviceGroupIDs[index]] = index;
                }

                foreach (var source in _sources)
                {
                    source.DeviceGroupIndex = _deviceGroupIndexByID[source.DeviceGroupID];
                }

                // A file with no recorded extension keeps its whole remainder, and for the homogeneity
                // test "no extension" is its own value rather than a match for everything.
                _nullExtensionIndex = 0;
                _extensionIndex[MediaClassification.NoExtensionLabel] = _nullExtensionIndex;
            }

            /// <summary>Records one physical file.</summary>
            internal void Add(
                long mediaFileID,
                long mediaSourceID,
                string fileName,
                string? extension,
                string? fileDateText,
                long mediaAssetID,
                long assetSizeBytes)
            {
                _mediaFileCount++;

                if (!_sourceIndexByID.TryGetValue(mediaSourceID, out var sourceIndex))
                {
                    throw new InvalidOperationException(
                        $"MediaFile {mediaFileID} belongs to MediaSource {mediaSourceID}, which the " +
                        $"workspace's MediaSource table does not contain. The workspace's foreign keys " +
                        $"forbid this, so it was written with enforcement off, and the file belongs to " +
                        $"no device group. Nothing has been censused and the workspace is unchanged.");
                }

                var source = _sources[sourceIndex];
                source.MediaFileCount++;

                var assetIndex = ResolveAsset(mediaAssetID, assetSizeBytes);
                var asset = _assets[assetIndex];
                asset.PhysicalFileCount++;

                var nameIndex = ResolveName(fileName);

                if (asset.IsZeroByte)
                {
                    asset.ZeroByteNames ??= [];
                    asset.ZeroByteNames.Add(nameIndex);
                }

                if (fileDateText is null)
                {
                    _undatedFileCount++;
                    source.UndatedFileCount++;

                    RequireNoMarkerOnAnUndatedFile(mediaFileID, fileName, source);

                    return;
                }

                if (!WorkspaceDateFormats.TryParseFileDate(fileDateText, out var storedDate))
                {
                    throw new InvalidOperationException(
                        $"MediaFile {mediaFileID} records a FileDate this workspace's own writer could " +
                        $"not have produced, so it cannot be read under the one sanctioned format. A " +
                        $"date guessed from a near miss would be indistinguishable afterwards from one " +
                        $"the archive really recorded. Nothing has been censused and the workspace is " +
                        $"unchanged.");
                }

                if (!WhatsAppNameMarker.TryLocate(fileName, out var suffixStart, out var markerDate))
                {
                    throw new InvalidOperationException(
                        $"MediaFile {mediaFileID} carries a FileDate but no locatable " +
                        $"-YYYYMMDD-WA marker, so this census and the committed classifier disagree " +
                        $"about which characters are a date. Every grouping below rests on that " +
                        $"agreement. Nothing has been censused and the workspace is unchanged.");
                }

                if (markerDate != storedDate)
                {
                    throw new InvalidOperationException(
                        $"MediaFile {mediaFileID} records a FileDate that is not the date its own name " +
                        $"encodes. The grouping key of this entire census is that date, so a workspace " +
                        $"where the two disagree cannot be grouped by it. Nothing has been censused and " +
                        $"the workspace is unchanged.");
                }

                _datedFileCount++;
                source.DatedFileCount++;

                var dateIndex = ResolveDate(storedDate);
                var date = _dates[dateIndex];

                var extensionMatchesEnding =
                    extension is not null
                    && fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

                var suffix = StripExtension(
                    fileName[suffixStart..], extension, extensionMatchesEnding);

                var supported = IsSupportedToken(suffix);

                if (!asset.IsZeroByte)
                {
                    date.DatedNonZeroByteFileCount++;

                    if (!supported)
                    {
                        date.DatedUnsupportedNonZeroByteFileCount++;
                    }
                }

                if (!supported)
                {
                    _unsupportedDatedFileCount++;

                    return;
                }

                _supportedFileCount++;
                source.SupportedFileCount++;
                asset.SupportedFileCount++;
                asset.HasSupportedEvidence = true;
                date.SupportedFileCount++;

                if (extension is null)
                {
                    _supportedFromNullExtensionCount++;
                }

                if (asset.IsZeroByte)
                {
                    date.SupportedFileCountOnZeroByteAsset++;
                }

                var tokenValue = ParseToken(suffix);

                if (asset.IsZeroByte)
                {
                    asset.ZeroByteTokens ??= [];
                    asset.ZeroByteTokens.Add(tokenValue);
                    asset.ZeroByteDates ??= [];
                    asset.ZeroByteDates.Add(dateIndex);
                }

                _rows.Add(new SupportedRow(
                    AssetIndex: assetIndex,
                    DateIndex: dateIndex,
                    TokenValue: tokenValue,
                    SourceIndex: source.Index,
                    DeviceGroupIndex: source.DeviceGroupIndex,
                    NameIndex: nameIndex,
                    ExtensionIndex: ResolveExtension(extension)));
            }

            /// <summary>
            /// Refuses an undated file whose name still carries the committed marker.
            /// </summary>
            /// <remarks>
            /// Applied only to sources the committed classifier reads dates from. For a source of another
            /// type the classifier returns null whatever the name looks like, so a locatable marker there
            /// is a legitimate consequence of the source's type rather than a disagreement, and refusing
            /// it would make such a workspace un-analysable for no reason.
            /// </remarks>
            private static void RequireNoMarkerOnAnUndatedFile(
                long mediaFileID, string fileName, SourceState source)
            {
                if (!IsWhatsAppMediaDirectory(source.SourceType))
                {
                    return;
                }

                if (!WhatsAppNameMarker.TryLocate(fileName, out _, out _))
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} carries no FileDate, yet its name holds a locatable " +
                    $"-YYYYMMDD-WA marker and its source is one the committed classifier reads dates " +
                    $"from. This census and that classifier therefore disagree about the same name. " +
                    $"Nothing has been censused and the workspace is unchanged.");
            }

            /// <summary>
            /// Where the suffix ends: the recorded extension is stripped rather than a fresh cut being
            /// made at the last full stop.
            /// </summary>
            /// <remarks>
            /// A second copy of the Stage A rule, kept here rather than shared, because sharing it would
            /// mean editing the Stage A census whose evidence is preserved. A file whose extension is
            /// null, or whose recorded extension is not how its name ends, keeps its whole remainder.
            /// </remarks>
            private static string StripExtension(
                string value, string? extension, bool extensionMatchesEnding) =>
                extensionMatchesEnding && extension is not null && value.Length >= extension.Length
                    ? value[..^extension.Length]
                    : value;

            /// <summary>The approved grammar: exactly four ASCII digits, and nothing else.</summary>
            private static bool IsSupportedToken(string suffix)
            {
                if (suffix.Length != SupportedTokenLength)
                {
                    return false;
                }

                foreach (var character in suffix)
                {
                    if (!char.IsAsciiDigit(character))
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>
            /// The token's value, which at fixed width orders identically to the digit string.
            /// </summary>
            /// <remarks>
            /// Carried as a <see cref="ushort"/> because four ASCII digits cannot exceed 9,999, and
            /// rendered back through invariant <c>D4</c>, which for that domain is a lossless round trip
            /// of the original four characters.
            /// </remarks>
            private static ushort ParseToken(string suffix) => (ushort)(
                ((suffix[0] - '0') * 1000)
                + ((suffix[1] - '0') * 100)
                + ((suffix[2] - '0') * 10)
                + (suffix[3] - '0'));

            private static string RenderToken(int tokenValue) =>
                tokenValue.ToString("D4", CultureInfo.InvariantCulture);

            private int ResolveAsset(long mediaAssetID, long sizeBytes)
            {
                if (_assetIndexByID.TryGetValue(mediaAssetID, out var index))
                {
                    return index;
                }

                index = _assets.Count;
                _assetIndexByID[mediaAssetID] = index;
                _assets.Add(new AssetState(sizeBytes));

                return index;
            }

            private int ResolveDate(DateOnly date)
            {
                if (_dateIndexByDate.TryGetValue(date, out var index))
                {
                    return index;
                }

                index = _dates.Count;
                _dateIndexByDate[date] = index;
                _dates.Add(new DateState());

                return index;
            }

            /// <remarks>
            /// Names are interned rather than stored per row, and only the dictionary's keys keep the
            /// text: no recovered name is ever emitted, so no reverse lookup is needed. The
            /// case-insensitive index is kept beside it so the ignore-case figure can be reported without
            /// a second pass, and never gated.
            /// </remarks>
            private int ResolveName(string fileName)
            {
                if (_nameIndex.TryGetValue(fileName, out var index))
                {
                    return index;
                }

                index = _nameIndex.Count;
                _nameIndex[fileName] = index;

                if (!_foldedNameIndex.TryGetValue(fileName, out var folded))
                {
                    folded = _foldedNameIndex.Count;
                    _foldedNameIndex[fileName] = folded;
                }

                _foldedNameByName.Add(folded);

                return index;
            }

            private int ResolveExtension(string? extension)
            {
                if (extension is null)
                {
                    return _nullExtensionIndex;
                }

                if (_extensionIndex.TryGetValue(extension, out var index))
                {
                    return index;
                }

                index = _extensionIndex.Count;
                _extensionIndex[extension] = index;

                return index;
            }

            // -------------------------------------------------------------------------------------
            // Building.
            // -------------------------------------------------------------------------------------

            internal WaSequenceScopeCensus Build(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var reconciliation = BuildReconciliation();
                var datePopulation = BuildDatePopulation();

                cancellationToken.ThrowIfCancellationRequested();

                var keyLevels = new List<ScopeKeyUniqueness>();
                var collisions = new List<CollisionCharacterisation>();

                foreach (var level in new[]
                         {
                             ScopeLevel.Token, ScopeLevel.DateToken, ScopeLevel.DeviceGroupDateToken,
                             ScopeLevel.SourceDateToken,
                         })
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var (uniqueness, collision) = BuildKeyLevel(level);

                    keyLevels.Add(uniqueness);

                    if (collision is not null)
                    {
                        collisions.Add(collision);
                    }
                }

                RequireMonotonicGates(keyLevels);

                cancellationToken.ThrowIfCancellationRequested();

                var dateGroups = BuildGroupRecords(ScopeLevel.Date);
                var deviceGroupDateGroups = BuildGroupRecords(ScopeLevel.DeviceGroupDate);
                var sourceDateGroups = BuildGroupRecords(ScopeLevel.SourceDate);

                var populations = new List<ScopeGroupPopulation>();
                populations.AddRange(BuildPopulations(ScopeLevel.Date, dateGroups));
                populations.AddRange(BuildPopulations(ScopeLevel.DeviceGroupDate, deviceGroupDateGroups));
                populations.AddRange(BuildPopulations(ScopeLevel.SourceDate, sourceDateGroups));

                var continuity = new List<ContinuityByGroupSize>();
                continuity.AddRange(BuildContinuity(ScopeLevel.Date, dateGroups));
                continuity.AddRange(BuildContinuity(ScopeLevel.DeviceGroupDate, deviceGroupDateGroups));
                continuity.AddRange(BuildContinuity(ScopeLevel.SourceDate, sourceDateGroups));

                var joints = new List<JointDistribution>();
                joints.AddRange(BuildJoints(ScopeLevel.DeviceGroupDate, deviceGroupDateGroups));
                joints.AddRange(BuildJoints(ScopeLevel.SourceDate, sourceDateGroups));

                cancellationToken.ThrowIfCancellationRequested();

                var curve = BuildTokenCurve(deviceGroupDateGroups.Count);
                var reuse = BuildTokenReuse();

                cancellationToken.ThrowIfCancellationRequested();

                var perAsset = BuildPerAsset();

                RequireLocalityClassesReconcile(perAsset.Locality);

                var maximumTokenGroup = DescribeMaximumTokenGroup(deviceGroupDateGroups);

                return new WaSequenceScopeCensus
                {
                    Reconciliation = reconciliation,
                    DatePopulation = datePopulation,
                    MediaSources = BuildSourceSummaries(),
                    KeyUniqueness = keyLevels,
                    Collisions = collisions,
                    TokenCurve = curve,
                    GroupPopulations = populations,
                    Continuity = continuity,
                    RangeAndPopulationJoints = joints,
                    DeviceGroupDateAssetsMinusTokens = SignedDistribution(deviceGroupDateGroups),
                    SourceDateAssetsMinusTokens = SignedDistribution(sourceDateGroups),
                    DeviceGroupDateGroupsWhereRangeTokensAndAssetsAllAgree =
                        CountPerfectAgreement(deviceGroupDateGroups),
                    SourceDateGroupsWhereRangeTokensAndAssetsAllAgree =
                        CountPerfectAgreement(sourceDateGroups),
                    MaximumTokenGroupDistinctTokenCount = maximumTokenGroup.DistinctTokens,
                    MaximumTokenGroupInclusiveWidth = maximumTokenGroup.InclusiveWidth,
                    MaximumTokenGroupDistinctAssetCount = maximumTokenGroup.DistinctAssets,
                    MaximumTokenGroupPhysicalFileCount = maximumTokenGroup.Files,
                    TokenReuse = reuse,
                    SameAssetAgreement = perAsset.Agreement,
                    NumericValueLocality = perAsset.Locality,
                    DuplicateCopies = perAsset.DuplicateCopies,
                    ZeroByte = BuildZeroByteContribution(),
                };
            }

            private SequenceReconciliation BuildReconciliation()
            {
                var assetsWithEvidence = _assets.Count(asset => asset.HasSupportedEvidence);

                if (_datedFileCount + _undatedFileCount != _mediaFileCount
                    || _supportedFileCount + _unsupportedDatedFileCount != _datedFileCount
                    || _rows.Count != _supportedFileCount)
                {
                    throw new InvalidOperationException(
                        "The file population does not reconcile: dated and undated files must sum to " +
                        "the files examined, supported and unsupported dated files must sum to the " +
                        "dated files, and one supported record must exist per supported file. A census " +
                        "whose own totals disagree cannot be trusted, so none is returned.");
                }

                return new SequenceReconciliation
                {
                    MediaFileCount = _mediaFileCount,
                    DatedFileCount = _datedFileCount,
                    UndatedFileCount = _undatedFileCount,
                    SupportedFileCount = _supportedFileCount,
                    UnsupportedDatedFileCount = _unsupportedDatedFileCount,
                    SupportedObservationsFromNullExtensionFiles = _supportedFromNullExtensionCount,
                    MediaAssetCount = _assets.Count,
                    AssetsWithSupportedEvidence = assetsWithEvidence,
                    AssetsWithoutSupportedEvidence = _assets.Count - assetsWithEvidence,
                    MediaSourceCount = _sources.Count,
                    DeviceGroupCount = _deviceGroupIDs.Count,
                };
            }

            /// <remarks>
            /// Both differences are required to be fully accounted for. Every dated file is either
            /// supported or unsupported, so a date carried by supported files but by no dated file with a
            /// payload can only be carried by empty payloads, and a date carried by a dated file with a
            /// payload but by no supported file can only be carried by unsupported dated files. A
            /// residual contradicts that partition, so it aborts rather than being reported.
            /// </remarks>
            private DatePopulationReconciliation BuildDatePopulation()
            {
                var supported = 0;
                var datedNonZeroByte = 0;
                var intersection = 0;
                var supportedOnly = 0;
                var datedNonZeroByteOnly = 0;
                var supportedOnlyAccounted = 0;
                var datedOnlyAccounted = 0;

                foreach (var date in _dates)
                {
                    var inSupported = date.SupportedFileCount > 0;
                    var inDatedNonZeroByte = date.DatedNonZeroByteFileCount > 0;

                    if (inSupported)
                    {
                        supported++;
                    }

                    if (inDatedNonZeroByte)
                    {
                        datedNonZeroByte++;
                    }

                    if (inSupported && inDatedNonZeroByte)
                    {
                        intersection++;
                    }
                    else if (inSupported)
                    {
                        supportedOnly++;

                        if (date.SupportedFileCount == date.SupportedFileCountOnZeroByteAsset)
                        {
                            supportedOnlyAccounted++;
                        }
                    }
                    else if (inDatedNonZeroByte)
                    {
                        datedNonZeroByteOnly++;

                        if (date.DatedNonZeroByteFileCount
                            == date.DatedUnsupportedNonZeroByteFileCount)
                        {
                            datedOnlyAccounted++;
                        }
                    }
                }

                if (supportedOnly != supportedOnlyAccounted
                    || datedNonZeroByteOnly != datedOnlyAccounted)
                {
                    throw new InvalidOperationException(
                        "The two date sets do not reconcile. Every supported-only date must be carried " +
                        "entirely by empty payloads, and every dated-non-empty-only date entirely by " +
                        "unsupported dated files; a residual contradicts the population partition this " +
                        "census rests on. No census is returned.");
                }

                return new DatePopulationReconciliation
                {
                    SupportedDateCount = supported,
                    DatedNonZeroByteDateCount = datedNonZeroByte,
                    IntersectionCount = intersection,
                    SupportedOnlyCount = supportedOnly,
                    DatedNonZeroByteOnlyCount = datedNonZeroByteOnly,
                    SupportedOnlyAccountedByZeroByteAsset = supportedOnlyAccounted,
                    DatedNonZeroByteOnlyAccountedByUnsupportedFiles = datedOnlyAccounted,
                };
            }

            private List<MediaSourceScopeSummary> BuildSourceSummaries() =>
                [.. _sources.Select(source => new MediaSourceScopeSummary
                {
                    MediaSourceID = source.MediaSourceID,
                    SourceType = source.SourceType,
                    IsWhatsAppMediaDirectory = IsWhatsAppMediaDirectory(source.SourceType),
                    DeviceGroupID = source.DeviceGroupID,
                    MediaFileCount = source.MediaFileCount,
                    DatedFileCount = source.DatedFileCount,
                    UndatedFileCount = source.UndatedFileCount,
                    SupportedFileCount = source.SupportedFileCount,
                })];

            // -------------------------------------------------------------------------------------
            // Scope ladder.
            // -------------------------------------------------------------------------------------

            private (ScopeKeyUniqueness Uniqueness, CollisionCharacterisation? Collision) BuildKeyLevel(
                ScopeLevel level)
            {
                var order = SortedOrder(level);

                var keyCount = 0;
                var keysOneFile = 0;
                var keysOneName = 0;
                var keysOneAsset = 0;
                var maximumFiles = 0;
                var maximumNames = 0;
                var maximumAssets = 0;
                var filesOnSingleAssetKeys = 0;
                var filesOnMultiAssetKeys = 0;
                var ignoringCaseChanges = 0;
                var multiAssetKeysWithZeroByte = 0;

                var assetsPerKey = new List<int>();

                var included = new MetricAccumulator();
                var excluded = new MetricAccumulator();

                var collision = new CollisionAccumulator(level);

                foreach (var (start, end) in Runs(order, level))
                {
                    keyCount++;

                    var files = end - start;
                    var summary = SummariseRun(order, start, end);

                    var assets = summary.DistinctAssets;
                    var assetsExcludingZeroByte = assets - summary.ZeroByteAssets;
                    var filesExcludingZeroByte = files - summary.ZeroByteFiles;

                    assetsPerKey.Add(assets);

                    if (files == 1)
                    {
                        keysOneFile++;
                    }

                    if (summary.DistinctNames == 1)
                    {
                        keysOneName++;
                    }

                    if (summary.DistinctNames != summary.DistinctFoldedNames)
                    {
                        ignoringCaseChanges++;
                    }

                    maximumFiles = Math.Max(maximumFiles, files);
                    maximumNames = Math.Max(maximumNames, summary.DistinctNames);
                    maximumAssets = Math.Max(maximumAssets, assets);

                    if (assets == 1)
                    {
                        keysOneAsset++;
                        filesOnSingleAssetKeys += files;
                    }
                    else
                    {
                        filesOnMultiAssetKeys += files;

                        if (summary.ZeroByteAssets > 0)
                        {
                            multiAssetKeysWithZeroByte++;
                        }

                        collision.Add(files, summary);
                    }

                    included.Add(assets, files, summary.AssetIndices, includeZeroByte: true, this);
                    excluded.Add(
                        assetsExcludingZeroByte,
                        filesExcludingZeroByte,
                        summary.AssetIndices,
                        includeZeroByte: false,
                        this);
                }

                var uniqueness = new ScopeKeyUniqueness
                {
                    Level = level,
                    KeyCount = keyCount,
                    KeysWithOnePhysicalFile = keysOneFile,
                    KeysWithSeveralPhysicalFiles = keyCount - keysOneFile,
                    KeysWithOneDistinctFileName = keysOneName,
                    KeysWithSeveralDistinctFileNames = keyCount - keysOneName,
                    KeysWithOneMediaAsset = keysOneAsset,
                    KeysWithSeveralMediaAssets = keyCount - keysOneAsset,
                    MaximumPhysicalFilesOnOneKey = maximumFiles,
                    MaximumDistinctFileNamesOnOneKey = maximumNames,
                    MaximumDistinctMediaAssetsOnOneKey = maximumAssets,
                    DistinctMediaAssetsPerKey = Summarise(assetsPerKey),
                    FilesOnSingleAssetKeys = filesOnSingleAssetKeys,
                    FilesOnMultiAssetKeys = filesOnMultiAssetKeys,
                    KeysWhereIgnoringCaseChangesTheNameCount = ignoringCaseChanges,
                    Ambiguity = included.Build(),
                    AmbiguityExcludingZeroByte = excluded.Build(),
                    MultiAssetKeysInvolvingZeroByteAsset = multiAssetKeysWithZeroByte,
                };

                // The token-only level's cross-tab is deliberately absent: its keys each span a large
                // share of the archive's assets, so the table would carry no readable structure. Its
                // full summary above, pair magnitude included, is still reported.
                return (uniqueness, level == ScopeLevel.Token ? null : collision.Build());
            }

            /// <remarks>
            /// The gates are theorems under refinement, so a violation is a defect in this code rather
            /// than a finding about the archive. Enforced on both variants: removing one asset uniformly
            /// from every key preserves the subset relation between a child key and its parent, so each
            /// gate is monotone in the excluded variant for the same reason it is monotone in the
            /// included one.
            /// </remarks>
            private static void RequireMonotonicGates(List<ScopeKeyUniqueness> levels)
            {
                for (var index = 1; index < levels.Count; index++)
                {
                    RequireNonIncreasing(levels[index - 1], levels[index], variant => variant.Ambiguity);
                    RequireNonIncreasing(
                        levels[index - 1], levels[index], variant => variant.AmbiguityExcludingZeroByte);
                }
            }

            private static void RequireNonIncreasing(
                ScopeKeyUniqueness parent,
                ScopeKeyUniqueness child,
                Func<ScopeKeyUniqueness, ScopeAmbiguityMetrics> select)
            {
                var above = select(parent);
                var below = select(child);

                Require(
                    above.FilesInMultiAssetKeys >= below.FilesInMultiAssetKeys,
                    "G1", parent.Level, child.Level);
                Require(
                    above.MaximumDistinctAssetsOnOneKey >= below.MaximumDistinctAssetsOnOneKey,
                    "G2", parent.Level, child.Level);
                Require(
                    above.AssetsInMultiAssetKeys >= below.AssetsInMultiAssetKeys,
                    "G4", parent.Level, child.Level);
            }

            private static void Require(bool holds, string gate, ScopeLevel parent, ScopeLevel child)
            {
                if (holds)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"{gate} increased from {parent} to {child}. A child key's asset set is always a " +
                    $"subset of its parent's, so this quantity cannot rise as scope is added: the rise " +
                    $"is a defect in this census rather than a fact about the archive. No census is " +
                    $"returned.");
            }

            // -------------------------------------------------------------------------------------
            // Group scopes.
            // -------------------------------------------------------------------------------------

            private List<GroupRecord> BuildGroupRecords(ScopeLevel scope)
            {
                var order = SortedOrder(scope);
                var records = new List<GroupRecord>();

                foreach (var (start, end) in Runs(order, scope))
                {
                    var summary = SummariseRun(order, start, end);

                    records.Add(new GroupRecord(
                        PartitionID: PartitionOf(scope, _rows[order[start]]),
                        Files: end - start,
                        DistinctAssets: summary.DistinctAssets,
                        DistinctTokens: summary.DistinctTokens,
                        DistinctNames: summary.DistinctNames,
                        MinToken: summary.MinimumToken,
                        MaxToken: summary.MaximumToken,
                        MissingInside: summary.MaximumToken - summary.MinimumToken + 1
                                       - summary.DistinctTokens));
                }

                return records;
            }

            private long? PartitionOf(ScopeLevel scope, SupportedRow row) => scope switch
            {
                ScopeLevel.DeviceGroupDate => _deviceGroupIDs[row.DeviceGroupIndex],
                ScopeLevel.SourceDate => _sources[row.SourceIndex].MediaSourceID,
                _ => null,
            };

            private List<ScopeGroupPopulation> BuildPopulations(
                ScopeLevel scope, List<GroupRecord> records)
            {
                var populations = new List<ScopeGroupPopulation>
                {
                    BuildPopulation(scope, null, records),
                };

                if (scope == ScopeLevel.Date)
                {
                    return populations;
                }

                foreach (var partition in records
                             .Where(record => record.PartitionID is not null)
                             .Select(record => record.PartitionID!.Value)
                             .Distinct()
                             .Order())
                {
                    populations.Add(BuildPopulation(
                        scope, partition, [.. records.Where(record => record.PartitionID == partition)]));
                }

                return populations;
            }

            private static ScopeGroupPopulation BuildPopulation(
                ScopeLevel scope, long? partition, List<GroupRecord> records) => new()
                {
                    Scope = scope,
                    PartitionID = partition,
                    GroupCount = records.Count,
                    FilesPerGroup = Summarise([.. records.Select(record => record.Files)]),
                    DistinctMediaAssetsPerGroup =
                        Summarise([.. records.Select(record => record.DistinctAssets)]),
                    DistinctTokensPerGroup =
                        Summarise([.. records.Select(record => record.DistinctTokens)]),
                    DistinctFileNamesPerGroup =
                        Summarise([.. records.Select(record => record.DistinctNames)]),
                };

            private static List<ContinuityByGroupSize> BuildContinuity(
                ScopeLevel scope, List<GroupRecord> records)
            {
                var continuity = new List<ContinuityByGroupSize>();

                foreach (var band in Enum.GetValues<GroupSizeBand>())
                {
                    var inBand = records.Where(record => BandOf(record.DistinctTokens) == band).ToList();
                    var minima = new Dictionary<int, int>();

                    foreach (var record in inBand)
                    {
                        minima[record.MinToken] = minima.GetValueOrDefault(record.MinToken) + 1;
                    }

                    continuity.Add(new ContinuityByGroupSize
                    {
                        Scope = scope,
                        Band = band,
                        GroupCount = inBand.Count,
                        GroupsStartingAtLowestToken = inBand.Count(record => record.MinToken == 0),
                        GroupsStartingAtSecondToken = inBand.Count(record => record.MinToken == 1),
                        GroupsStartingHigher = inBand.Count(record => record.MinToken > 1),
                        ObservedMinima = ToValueCounts(minima),
                        GroupsWithNoInternalMissingTokens =
                            inBand.Count(record => record.MissingInside == 0),
                        GroupsWithInternalMissingTokens =
                            inBand.Count(record => record.MissingInside > 0),
                        GapCountZero = inBand.Count(record => record.MissingInside == 0),
                        GapCountOne = inBand.Count(record => record.MissingInside == 1),
                        GapCountTwo = inBand.Count(record => record.MissingInside == 2),
                        GapCountThreeToFive =
                            inBand.Count(record => record.MissingInside is >= 3 and <= 5),
                        GapCountSixToTen =
                            inBand.Count(record => record.MissingInside is >= 6 and <= 10),
                        GapCountMoreThanTen = inBand.Count(record => record.MissingInside > 10),
                        TotalObservedDistinctTokens = inBand.Sum(record => (long)record.DistinctTokens),
                        TotalUnobservedValuesInsideRanges =
                            inBand.Sum(record => (long)record.MissingInside),
                        MaximumInternalGapCount =
                            inBand.Count == 0 ? 0 : inBand.Max(record => record.MissingInside),
                        GroupsWhereMaximumPlusOneEqualsDistinctCount =
                            inBand.Count(record => record.MaxToken + 1 == record.DistinctTokens),
                        GroupsContiguousButNotStartingAtLowestToken = inBand.Count(record =>
                            record.MissingInside == 0 && record.MinToken > 0),
                    });
                }

                return continuity;
            }

            private static List<JointDistribution> BuildJoints(
                ScopeLevel scope, List<GroupRecord> records)
            {
                var tokensByMaximum = new int[CountBandLabels.Length, TokenBandLabels.Length];
                var tokensByGaps = new int[CountBandLabels.Length, GapBandLabels.Length];
                var tokensByPerfect = new int[CountBandLabels.Length, PerfectPrefixLabels.Length];
                var tokensByAssets = new int[CountBandLabels.Length, CountBandLabels.Length];
                var tokensByFiles = new int[CountBandLabels.Length, CountBandLabels.Length];

                foreach (var record in records)
                {
                    var tokenBand = CountBandIndex(record.DistinctTokens);

                    tokensByMaximum[tokenBand, TokenBandIndex(record.MaxToken)]++;
                    tokensByGaps[tokenBand, GapBandIndex(record.MissingInside)]++;
                    tokensByPerfect[
                        tokenBand, record.MaxToken + 1 == record.DistinctTokens ? 0 : 1]++;
                    tokensByAssets[tokenBand, CountBandIndex(record.DistinctAssets)]++;
                    tokensByFiles[tokenBand, CountBandIndex(record.Files)]++;
                }

                return
                [
                    Joint("DistinctTokenCountByMaximumToken", scope,
                        CountBandLabels, TokenBandLabels, tokensByMaximum),
                    Joint("DistinctTokenCountByInternalGapCount", scope,
                        CountBandLabels, GapBandLabels, tokensByGaps),
                    Joint("DistinctTokenCountByMaximumPlusOneEqualsDistinctCount", scope,
                        CountBandLabels, PerfectPrefixLabels, tokensByPerfect),
                    Joint("DistinctTokenCountByDistinctMediaAssetCount", scope,
                        CountBandLabels, CountBandLabels, tokensByAssets),
                    Joint("DistinctTokenCountByPhysicalFileCount", scope,
                        CountBandLabels, CountBandLabels, tokensByFiles),
                ];
            }

            private static List<ValueCount> SignedDistribution(List<GroupRecord> records)
            {
                var counts = new Dictionary<int, int>();

                foreach (var record in records)
                {
                    var difference = record.DistinctAssets - record.DistinctTokens;
                    counts[difference] = counts.GetValueOrDefault(difference) + 1;
                }

                return ToValueCounts(counts);
            }

            private static int CountPerfectAgreement(List<GroupRecord> records) =>
                records.Count(record =>
                    record.MaxToken + 1 == record.DistinctTokens
                    && record.DistinctTokens == record.DistinctAssets);

            /// <remarks>
            /// A reconciliation of the highest observed token against the population that produced it, not
            /// an exemplar: no date and no surrogate identifier is emitted. Where several groups hold that
            /// token the one with the most distinct tokens is described, ties broken by the widest range
            /// and then the most files, so the choice is deterministic.
            /// </remarks>
            private static GroupRecord DescribeMaximumTokenGroup(List<GroupRecord> records)
            {
                if (records.Count == 0)
                {
                    return new GroupRecord(null, 0, 0, 0, 0, 0, -1, 0);
                }

                var highest = records.Max(record => record.MaxToken);

                return records
                    .Where(record => record.MaxToken == highest)
                    .OrderByDescending(record => record.DistinctTokens)
                    .ThenByDescending(record => record.InclusiveWidth)
                    .ThenByDescending(record => record.Files)
                    .First();
            }

            // -------------------------------------------------------------------------------------
            // Token curve and reuse.
            // -------------------------------------------------------------------------------------

            private SequenceTokenCurve BuildTokenCurve(int totalDeviceGroupDateGroups)
            {
                var order = SortedOrder(ScopeLevel.Token);
                var rows = new List<SequenceTokenCurveRow>();

                var dates = new HashSet<int>();
                var groupDates = new HashSet<long>();
                var sourceDates = new HashSet<long>();
                var perGroupFiles = new int[Math.Max(_deviceGroupIDs.Count, 1)];
                var perGroupDates = new HashSet<int>[Math.Max(_deviceGroupIDs.Count, 1)];

                for (var index = 0; index < perGroupDates.Length; index++)
                {
                    perGroupDates[index] = [];
                }

                foreach (var (start, end) in Runs(order, ScopeLevel.Token))
                {
                    _scratchAssets.Clear();
                    dates.Clear();
                    groupDates.Clear();
                    sourceDates.Clear();
                    Array.Clear(perGroupFiles);

                    foreach (var set in perGroupDates)
                    {
                        set.Clear();
                    }

                    for (var index = start; index < end; index++)
                    {
                        var row = _rows[order[index]];

                        _scratchAssets.Add(row.AssetIndex);
                        dates.Add(row.DateIndex);
                        groupDates.Add(Pack(row.DeviceGroupIndex, row.DateIndex));
                        sourceDates.Add(Pack(row.SourceIndex, row.DateIndex));
                        perGroupFiles[row.DeviceGroupIndex]++;
                        perGroupDates[row.DeviceGroupIndex].Add(row.DateIndex);
                    }

                    var token = _rows[order[start]].TokenValue;

                    rows.Add(new SequenceTokenCurveRow
                    {
                        Token = RenderToken(token),
                        TokenValue = token,
                        PhysicalFileCount = end - start,
                        DistinctMediaAssetCount = _scratchAssets.Count,
                        DistinctFileDateCount = dates.Count,
                        DistinctDeviceGroupDateGroupCount = groupDates.Count,
                        DistinctSourceDateGroupCount = sourceDates.Count,
                        PerDeviceGroup = BuildPerDeviceGroup(perGroupFiles, perGroupDates),
                    });
                }

                var lowest = rows.FirstOrDefault(row => row.TokenValue == 0);
                var ascents = 0;
                var largestAscent = 0;

                for (var index = 1; index < rows.Count; index++)
                {
                    var rise = rows[index].DistinctDeviceGroupDateGroupCount
                               - rows[index - 1].DistinctDeviceGroupDateGroupCount;

                    if (rise > 0)
                    {
                        ascents++;
                        largestAscent = Math.Max(largestAscent, rise);
                    }
                }

                return new SequenceTokenCurve
                {
                    Rows = rows,
                    DistinctTokenCount = rows.Count,
                    MinimumToken = rows.Count == 0 ? null : rows[0].Token,
                    MaximumToken = rows.Count == 0 ? null : rows[^1].Token,
                    ObservedSetIsContiguous = rows.Count == 0
                        || rows[^1].TokenValue - rows[0].TokenValue + 1 == rows.Count,
                    TotalDeviceGroupDateGroups = totalDeviceGroupDateGroups,
                    GroupsContainingLowestToken = lowest?.DistinctDeviceGroupDateGroupCount ?? 0,
                    GroupsContainingSecondToken = rows
                        .FirstOrDefault(row => row.TokenValue == 1)
                        ?.DistinctDeviceGroupDateGroupCount ?? 0,
                    AdjacentAscentCount = ascents,
                    LargestAscent = largestAscent,
                    TokensExceedingLowestToken = lowest is null
                        ? 0
                        : rows.Count(row => row.DistinctDeviceGroupDateGroupCount
                                            > lowest.DistinctDeviceGroupDateGroupCount),
                };
            }

            private List<DeviceGroupTokenCounts> BuildPerDeviceGroup(
                int[] files, HashSet<int>[] dates)
            {
                var counts = new List<DeviceGroupTokenCounts>();

                for (var index = 0; index < _deviceGroupIDs.Count; index++)
                {
                    if (files[index] == 0)
                    {
                        continue;
                    }

                    counts.Add(new DeviceGroupTokenCounts
                    {
                        DeviceGroupID = _deviceGroupIDs[index],
                        PhysicalFileCount = files[index],
                        DistinctFileDateCount = dates[index].Count,
                    });
                }

                return counts;
            }

            private List<TokenReuseSummary> BuildTokenReuse()
            {
                var summaries = new List<TokenReuseSummary>
                {
                    BuildReuse(ScopeLevel.Date, null, _ => true),
                };

                foreach (var deviceGroupID in _deviceGroupIDs)
                {
                    var index = _deviceGroupIndexByID[deviceGroupID];

                    summaries.Add(BuildReuse(
                        ScopeLevel.DeviceGroupDate, deviceGroupID, row => row.DeviceGroupIndex == index));
                }

                foreach (var source in _sources)
                {
                    var index = source.Index;

                    summaries.Add(BuildReuse(
                        ScopeLevel.SourceDate, source.MediaSourceID, row => row.SourceIndex == index));
                }

                return summaries;
            }

            private TokenReuseSummary BuildReuse(
                ScopeLevel scope, long? partition, Func<SupportedRow, bool> include)
            {
                var datesByToken = new Dictionary<int, HashSet<int>>();

                foreach (var row in _rows)
                {
                    if (!include(row))
                    {
                        continue;
                    }

                    if (!datesByToken.TryGetValue(row.TokenValue, out var dates))
                    {
                        dates = [];
                        datesByToken[row.TokenValue] = dates;
                    }

                    dates.Add(row.DateIndex);
                }

                var counts = datesByToken.Values.Select(dates => dates.Count).ToList();

                return new TokenReuseSummary
                {
                    Scope = scope,
                    PartitionID = partition,
                    TokensOnOneDateOnly = counts.Count(count => count == 1),
                    TokensOnSeveralDates = counts.Count(count => count > 1),
                    DistinctDatesPerToken = Summarise(counts),
                };
            }

            // -------------------------------------------------------------------------------------
            // Per-asset behaviour: C8, C9 and the locality decomposition, in one walk.
            // -------------------------------------------------------------------------------------

            /// <remarks>
            /// Sorted by asset, then date, then device group, then source, then token, so that within one
            /// asset the date runs contain the device-group runs which contain the source runs. Every
            /// "do these agree?" question is then <c>min == max</c> over the relevant run, which needs no
            /// set and no pairwise comparison.
            /// </remarks>
            private PerAssetResults BuildPerAsset()
            {
                var order = SortedOrder(ScopeLevel.SourceDateToken, byAsset: true);

                var agreement = new AgreementAccumulator();
                var locality = new LocalityAccumulator();
                var sourceDuplicates = new DuplicateAccumulator(ScopeLevel.SourceDate);
                var groupDuplicates = new DuplicateAccumulator(ScopeLevel.DeviceGroupDate);

                var index = 0;

                while (index < order.Count)
                {
                    var assetIndex = _rows[order[index]].AssetIndex;
                    var assetEnd = index;

                    while (assetEnd < order.Count && _rows[order[assetEnd]].AssetIndex == assetIndex)
                    {
                        assetEnd++;
                    }

                    WalkAsset(
                        order, index, assetEnd, agreement, locality, sourceDuplicates, groupDuplicates);

                    index = assetEnd;
                }

                return new PerAssetResults(
                    agreement.Build(),
                    locality.Build(),
                    [sourceDuplicates.Build(), groupDuplicates.Build()]);
            }

            private void WalkAsset(
                List<int> order,
                int assetStart,
                int assetEnd,
                AgreementAccumulator agreement,
                LocalityAccumulator locality,
                DuplicateAccumulator sourceDuplicates,
                DuplicateAccumulator groupDuplicates)
            {
                var assetMinimum = int.MaxValue;
                var assetMaximum = int.MinValue;
                var differsWithinSourceAndDate = false;
                var differsWithinDeviceGroupAndDate = false;
                var differsWithinDate = false;

                var dateStart = assetStart;

                while (dateStart < assetEnd)
                {
                    var dateIndex = _rows[order[dateStart]].DateIndex;
                    var dateEnd = dateStart;

                    while (dateEnd < assetEnd && _rows[order[dateEnd]].DateIndex == dateIndex)
                    {
                        dateEnd++;
                    }

                    var perSource = new List<SourceObservation>();
                    var dateMinimum = int.MaxValue;
                    var dateMaximum = int.MinValue;
                    var distinctTokensOnDate = new HashSet<int>();
                    var deviceGroups = new HashSet<int>();

                    var groupStart = dateStart;

                    while (groupStart < dateEnd)
                    {
                        var groupIndex = _rows[order[groupStart]].DeviceGroupIndex;
                        var groupEnd = groupStart;

                        while (groupEnd < dateEnd
                               && _rows[order[groupEnd]].DeviceGroupIndex == groupIndex)
                        {
                            groupEnd++;
                        }

                        deviceGroups.Add(groupIndex);

                        var groupMinimum = int.MaxValue;
                        var groupMaximum = int.MinValue;

                        var sourceStart = groupStart;

                        while (sourceStart < groupEnd)
                        {
                            var sourceIndex = _rows[order[sourceStart]].SourceIndex;
                            var sourceEnd = sourceStart;

                            while (sourceEnd < groupEnd
                                   && _rows[order[sourceEnd]].SourceIndex == sourceIndex)
                            {
                                sourceEnd++;
                            }

                            var sourceMinimum = int.MaxValue;
                            var sourceMaximum = int.MinValue;

                            for (var cursor = sourceStart; cursor < sourceEnd; cursor++)
                            {
                                var token = _rows[order[cursor]].TokenValue;

                                sourceMinimum = Math.Min(sourceMinimum, token);
                                sourceMaximum = Math.Max(sourceMaximum, token);
                                distinctTokensOnDate.Add(token);
                            }

                            if (sourceMinimum != sourceMaximum)
                            {
                                differsWithinSourceAndDate = true;
                            }

                            sourceDuplicates.Add(sourceEnd - sourceStart, sourceMinimum, sourceMaximum);

                            perSource.Add(new SourceObservation(
                                sourceIndex, groupIndex, sourceMinimum, sourceMaximum));

                            groupMinimum = Math.Min(groupMinimum, sourceMinimum);
                            groupMaximum = Math.Max(groupMaximum, sourceMaximum);

                            sourceStart = sourceEnd;
                        }

                        if (groupMinimum != groupMaximum)
                        {
                            differsWithinDeviceGroupAndDate = true;
                        }

                        groupDuplicates.Add(groupEnd - groupStart, groupMinimum, groupMaximum);

                        dateMinimum = Math.Min(dateMinimum, groupMinimum);
                        dateMaximum = Math.Max(dateMaximum, groupMaximum);

                        groupStart = groupEnd;
                    }

                    if (dateMinimum != dateMaximum)
                    {
                        differsWithinDate = true;
                    }

                    assetMinimum = Math.Min(assetMinimum, dateMinimum);
                    assetMaximum = Math.Max(assetMaximum, dateMaximum);

                    agreement.AddAssetDate(
                        perSource, deviceGroups.Count, distinctTokensOnDate.Count,
                        dateMinimum == dateMaximum);

                    dateStart = dateEnd;
                }

                locality.Add(
                    assetMinimum != assetMaximum,
                    differsWithinSourceAndDate,
                    differsWithinDeviceGroupAndDate,
                    differsWithinDate);
            }

            private static void RequireLocalityClassesReconcile(NumericValueLocalityCounts locality)
            {
                if (locality.ClassTotal == locality.AssetsWithSeveralDistinctTokens)
                {
                    return;
                }

                throw new InvalidOperationException(
                    "The locality classes do not sum to the payloads carrying several distinct tokens. " +
                    "The four classes are assigned by most-local-wins precedence and the last is the " +
                    "complement of the others, so they must partition that population exactly. No " +
                    "census is returned.");
            }

            private ZeroByteContribution BuildZeroByteContribution()
            {
                var names = new HashSet<int>();
                var tokens = new HashSet<int>();
                var dates = new HashSet<int>();
                var assets = 0;
                var files = 0;
                var supported = 0;

                foreach (var asset in _assets.Where(asset => asset.IsZeroByte))
                {
                    assets++;
                    files += asset.PhysicalFileCount;
                    supported += asset.SupportedFileCount;

                    if (asset.ZeroByteNames is { } assetNames)
                    {
                        names.UnionWith(assetNames);
                    }

                    if (asset.ZeroByteTokens is { } assetTokens)
                    {
                        tokens.UnionWith(assetTokens);
                    }

                    if (asset.ZeroByteDates is { } assetDates)
                    {
                        dates.UnionWith(assetDates);
                    }
                }

                return new ZeroByteContribution
                {
                    ZeroByteAssetCount = assets,
                    PhysicalFileCount = files,
                    SupportedFileCount = supported,
                    DistinctFileNameCount = names.Count,
                    DistinctTokenCount = tokens.Count,
                    DistinctFileDateCount = dates.Count,
                    AllSupportedObservationsCarryTheSameToken =
                        supported < 2 ? null : tokens.Count == 1,
                };
            }

            // -------------------------------------------------------------------------------------
            // Sorting and run enumeration.
            // -------------------------------------------------------------------------------------

            /// <remarks>
            /// Every comparator ends in the record's own index, which makes each ordering total and stops
            /// any figure depending on sort stability. Records were appended in <c>MediaFileID</c> order,
            /// so that index is the file order and no identifier need be carried to provide it.
            /// </remarks>
            private List<int> SortedOrder(ScopeLevel scope, bool byAsset = false)
            {
                var order = new int[_rows.Count];

                for (var index = 0; index < order.Length; index++)
                {
                    order[index] = index;
                }

                Array.Sort(order, (left, right) =>
                {
                    var a = _rows[left];
                    var b = _rows[right];

                    int comparison;

                    if (byAsset)
                    {
                        comparison = a.AssetIndex.CompareTo(b.AssetIndex);

                        if (comparison != 0)
                        {
                            return comparison;
                        }

                        comparison = a.DateIndex.CompareTo(b.DateIndex);

                        if (comparison != 0)
                        {
                            return comparison;
                        }

                        comparison = a.DeviceGroupIndex.CompareTo(b.DeviceGroupIndex);

                        if (comparison != 0)
                        {
                            return comparison;
                        }

                        comparison = a.SourceIndex.CompareTo(b.SourceIndex);

                        if (comparison != 0)
                        {
                            return comparison;
                        }

                        comparison = a.TokenValue.CompareTo(b.TokenValue);

                        return comparison != 0 ? comparison : left.CompareTo(right);
                    }

                    comparison = LeadingComparison(scope, a, b);

                    if (comparison != 0)
                    {
                        return comparison;
                    }

                    comparison = a.TokenValue.CompareTo(b.TokenValue);

                    return comparison != 0 ? comparison : left.CompareTo(right);
                });

                return [.. order];
            }

            private static int LeadingComparison(ScopeLevel scope, SupportedRow a, SupportedRow b)
            {
                switch (scope)
                {
                    case ScopeLevel.Token:
                        return 0;

                    case ScopeLevel.Date:
                    case ScopeLevel.DateToken:
                        return a.DateIndex.CompareTo(b.DateIndex);

                    case ScopeLevel.DeviceGroupDate:
                    case ScopeLevel.DeviceGroupDateToken:
                    {
                        var comparison = a.DeviceGroupIndex.CompareTo(b.DeviceGroupIndex);

                        return comparison != 0 ? comparison : a.DateIndex.CompareTo(b.DateIndex);
                    }

                    case ScopeLevel.SourceDate:
                    case ScopeLevel.SourceDateToken:
                    {
                        var comparison = a.SourceIndex.CompareTo(b.SourceIndex);

                        return comparison != 0 ? comparison : a.DateIndex.CompareTo(b.DateIndex);
                    }

                    default:
                        return 0;
                }
            }

            private IEnumerable<(int Start, int End)> Runs(List<int> order, ScopeLevel scope)
            {
                var start = 0;

                while (start < order.Count)
                {
                    var end = start + 1;

                    while (end < order.Count && SameKey(scope, _rows[order[start]], _rows[order[end]]))
                    {
                        end++;
                    }

                    yield return (start, end);

                    start = end;
                }
            }

            private static bool SameKey(ScopeLevel scope, SupportedRow a, SupportedRow b)
            {
                if (LeadingComparison(scope, a, b) != 0)
                {
                    return false;
                }

                return scope switch
                {
                    ScopeLevel.Date or ScopeLevel.DeviceGroupDate or ScopeLevel.SourceDate => true,
                    _ => a.TokenValue == b.TokenValue,
                };
            }

            /// <summary>
            /// Everything one contiguous run carries, from one walk over it.
            /// </summary>
            /// <remarks>
            /// The token figures come from a bitset covering the whole four-digit domain, reused and
            /// cleared per run: a population count gives the distinct tokens, and the observed bounds give
            /// the range whose width minus that count is the missing values inside it. Extension
            /// homogeneity is a minimum and a maximum over interned indices, which needs no set at all.
            /// </remarks>
            private RunSummary SummariseRun(List<int> order, int start, int end)
            {
                _scratchAssets.Clear();
                _scratchNames.Clear();
                _scratchFolded.Clear();
                Array.Clear(_tokenBits);

                var minimumToken = int.MaxValue;
                var maximumToken = int.MinValue;
                var minimumExtension = int.MaxValue;
                var maximumExtension = int.MinValue;
                var zeroByteFiles = 0;

                for (var index = start; index < end; index++)
                {
                    var row = _rows[order[index]];

                    _scratchAssets.Add(row.AssetIndex);
                    _scratchNames.Add(row.NameIndex);
                    _scratchFolded.Add(_foldedNameByName[row.NameIndex]);

                    _tokenBits[row.TokenValue >> 6] |= 1UL << (row.TokenValue & 63);

                    minimumToken = Math.Min(minimumToken, row.TokenValue);
                    maximumToken = Math.Max(maximumToken, row.TokenValue);
                    minimumExtension = Math.Min(minimumExtension, row.ExtensionIndex);
                    maximumExtension = Math.Max(maximumExtension, row.ExtensionIndex);

                    if (_assets[row.AssetIndex].IsZeroByte)
                    {
                        zeroByteFiles++;
                    }
                }

                var distinctTokens = 0;

                foreach (var word in _tokenBits)
                {
                    distinctTokens += BitOperations.PopCount(word);
                }

                var zeroByteAssets = _scratchAssets.Count(index => _assets[index].IsZeroByte);

                return new RunSummary(
                    DistinctAssets: _scratchAssets.Count,
                    DistinctNames: _scratchNames.Count,
                    DistinctFoldedNames: _scratchFolded.Count,
                    DistinctTokens: distinctTokens,
                    MinimumToken: minimumToken,
                    MaximumToken: maximumToken,
                    ExtensionsAllEqual: minimumExtension == maximumExtension,
                    ZeroByteAssets: zeroByteAssets,
                    ZeroByteFiles: zeroByteFiles,
                    AssetIndices: [.. _scratchAssets]);
            }

            private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

            private static bool IsWhatsAppMediaDirectory(string sourceType) =>
                string.Equals(
                    sourceType, MediaSourceTypes.WhatsAppMediaDirectory, StringComparison.Ordinal);

            // -------------------------------------------------------------------------------------
            // Bands and small builders.
            // -------------------------------------------------------------------------------------

            private static GroupSizeBand BandOf(int count) => count switch
            {
                <= 1 => GroupSizeBand.One,
                2 => GroupSizeBand.Two,
                <= 5 => GroupSizeBand.ThreeToFive,
                <= 10 => GroupSizeBand.SixToTen,
                <= 25 => GroupSizeBand.ElevenToTwentyFive,
                _ => GroupSizeBand.MoreThanTwentyFive,
            };

            private static int CountBandIndex(int count) => (int)BandOf(count);

            private static int GapBandIndex(int gaps) => gaps switch
            {
                <= 0 => 0,
                1 => 1,
                2 => 2,
                <= 5 => 3,
                <= 10 => 4,
                _ => 5,
            };

            /// <remarks>
            /// The top band is open-ended rather than closed at the highest token this archive happens to
            /// contain, because production must describe any workspace.
            /// </remarks>
            private static int TokenBandIndex(int token) => token switch
            {
                <= 0 => 0,
                <= 2 => 1,
                <= 5 => 2,
                <= 10 => 3,
                <= 25 => 4,
                <= 50 => 5,
                <= 100 => 6,
                _ => 7,
            };

            private static CountSummary Summarise(List<int> values)
            {
                if (values.Count == 0)
                {
                    return new CountSummary
                    {
                        Population = 0,
                        Minimum = 0,
                        Median = 0,
                        Maximum = 0,
                        One = 0,
                        Two = 0,
                        ThreeToFive = 0,
                        SixToTen = 0,
                        ElevenToTwentyFive = 0,
                        MoreThanTwentyFive = 0,
                    };
                }

                var sorted = new List<int>(values);
                sorted.Sort();

                return new CountSummary
                {
                    Population = sorted.Count,
                    Minimum = sorted[0],

                    // The lower median: the lower of the two middle values when the population is even,
                    // so two runs are comparable and no fractional count is printed.
                    Median = sorted[(sorted.Count - 1) / 2],
                    Maximum = sorted[^1],
                    One = sorted.Count(value => value <= 1),
                    Two = sorted.Count(value => value == 2),
                    ThreeToFive = sorted.Count(value => value is >= 3 and <= 5),
                    SixToTen = sorted.Count(value => value is >= 6 and <= 10),
                    ElevenToTwentyFive = sorted.Count(value => value is >= 11 and <= 25),
                    MoreThanTwentyFive = sorted.Count(value => value > 25),
                };
            }

            private static List<ValueCount> ToValueCounts(Dictionary<int, int> counts) =>
                [.. counts
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new ValueCount { Value = pair.Key, Count = pair.Value })];

            private static JointDistribution Joint(
                string name, ScopeLevel scope, string[] rows, string[] columns, int[,] counts)
            {
                var cells = new List<JointCell>();

                for (var row = 0; row < rows.Length; row++)
                {
                    for (var column = 0; column < columns.Length; column++)
                    {
                        if (counts[row, column] == 0)
                        {
                            continue;
                        }

                        cells.Add(new JointCell
                        {
                            Row = rows[row],
                            Column = columns[column],
                            Count = counts[row, column],
                        });
                    }
                }

                return new JointDistribution { Name = name, Scope = scope, Cells = cells };
            }

            // -------------------------------------------------------------------------------------
            // State.
            // -------------------------------------------------------------------------------------

            private readonly record struct SupportedRow(
                int AssetIndex,
                int DateIndex,
                ushort TokenValue,
                int SourceIndex,
                int DeviceGroupIndex,
                int NameIndex,
                int ExtensionIndex);

            private readonly record struct RunSummary(
                int DistinctAssets,
                int DistinctNames,
                int DistinctFoldedNames,
                int DistinctTokens,
                int MinimumToken,
                int MaximumToken,
                bool ExtensionsAllEqual,
                int ZeroByteAssets,
                int ZeroByteFiles,
                int[] AssetIndices);

            private readonly record struct GroupRecord(
                long? PartitionID,
                int Files,
                int DistinctAssets,
                int DistinctTokens,
                int DistinctNames,
                int MinToken,
                int MaxToken,
                int MissingInside)
            {
                internal int InclusiveWidth => MaxToken - MinToken + 1;
            }

            private readonly record struct SourceObservation(
                int SourceIndex, int DeviceGroupIndex, int MinimumToken, int MaximumToken);

            private readonly record struct PerAssetResults(
                SameAssetAgreement Agreement,
                NumericValueLocalityCounts Locality,
                List<DuplicateCopyAgreement> DuplicateCopies);

            /// <remarks>
            /// The index is supplied rather than counted by the type. A static counter would be shared
            /// between censuses, so a second analysis in the same process would index its sources from
            /// wherever the first one stopped.
            /// </remarks>
            private sealed class SourceState(
                int index, long mediaSourceID, string sourceType, long deviceGroupID)
            {
                internal int Index { get; } = index;

                internal long MediaSourceID { get; } = mediaSourceID;

                internal string SourceType { get; } = sourceType;

                internal long DeviceGroupID { get; } = deviceGroupID;

                internal int DeviceGroupIndex { get; set; }

                internal int MediaFileCount { get; set; }

                internal int DatedFileCount { get; set; }

                internal int UndatedFileCount { get; set; }

                internal int SupportedFileCount { get; set; }
            }

            private sealed class AssetState(long sizeBytes)
            {
                internal bool IsZeroByte { get; } = sizeBytes == 0;

                internal int PhysicalFileCount { get; set; }

                internal int SupportedFileCount { get; set; }

                internal bool HasSupportedEvidence { get; set; }

                /// <remarks>
                /// Allocated only for an empty payload, whose contribution has to be reportable
                /// separately, so no per-asset set is held for the other forty-five thousand.
                /// </remarks>
                internal HashSet<int>? ZeroByteNames { get; set; }

                internal HashSet<int>? ZeroByteTokens { get; set; }

                internal HashSet<int>? ZeroByteDates { get; set; }
            }

            private sealed class DateState
            {
                internal int SupportedFileCount { get; set; }

                internal int SupportedFileCountOnZeroByteAsset { get; set; }

                internal int DatedNonZeroByteFileCount { get; set; }

                internal int DatedUnsupportedNonZeroByteFileCount { get; set; }
            }

            private sealed class MetricAccumulator
            {
                private readonly HashSet<int> _assetsInCollisions = [];

                private int _filesInMultiAssetKeys;
                private int _maximumAssets;
                private int _multiAssetKeys;
                private long _pairMagnitude;
                private long _excess;

                internal void Add(
                    int assets, int files, int[] assetIndices, bool includeZeroByte,
                    CensusAccumulator owner)
                {
                    _maximumAssets = Math.Max(_maximumAssets, assets);

                    if (assets < 2)
                    {
                        return;
                    }

                    _filesInMultiAssetKeys += files;
                    _multiAssetKeys++;
                    _pairMagnitude += (long)assets * (assets - 1) / 2;
                    _excess += assets - 1;

                    foreach (var index in assetIndices)
                    {
                        if (!includeZeroByte && owner._assets[index].IsZeroByte)
                        {
                            continue;
                        }

                        _assetsInCollisions.Add(index);
                    }
                }

                internal ScopeAmbiguityMetrics Build() => new()
                {
                    FilesInMultiAssetKeys = _filesInMultiAssetKeys,
                    MaximumDistinctAssetsOnOneKey = _maximumAssets,
                    AssetsInMultiAssetKeys = _assetsInCollisions.Count,
                    MultiAssetKeyCount = _multiAssetKeys,
                    AssetPairMagnitude = _pairMagnitude,
                    ExcessAmbiguity = _excess,
                };
            }

            private sealed class CollisionAccumulator(ScopeLevel level)
            {
                private readonly List<int> _files = [];
                private readonly List<int> _names = [];
                private readonly List<int> _assets = [];
                private readonly int[,] _nameByExtension =
                    new int[NameShapeLabels.Length, ExtensionLabels.Length];
                private readonly int[,] _nameByZeroByte =
                    new int[NameShapeLabels.Length, ZeroByteLabels.Length];

                private int _oneName;
                private int _extensionsEqual;
                private int _zeroByteInvolved;

                internal void Add(int files, RunSummary summary)
                {
                    _files.Add(files);
                    _names.Add(summary.DistinctNames);
                    _assets.Add(summary.DistinctAssets);

                    var nameShape = summary.DistinctNames == 1 ? 0 : 1;
                    var zeroByte = summary.ZeroByteAssets > 0 ? 0 : 1;

                    if (nameShape == 0)
                    {
                        _oneName++;
                    }

                    if (summary.ExtensionsAllEqual)
                    {
                        _extensionsEqual++;
                    }

                    if (zeroByte == 0)
                    {
                        _zeroByteInvolved++;
                    }

                    _nameByExtension[nameShape, summary.ExtensionsAllEqual ? 0 : 1]++;
                    _nameByZeroByte[nameShape, zeroByte]++;
                }

                internal CollisionCharacterisation Build() => new()
                {
                    Level = level,
                    MultiAssetKeyCount = _files.Count,
                    KeysWithOneDistinctFileName = _oneName,
                    KeysWithSeveralDistinctFileNames = _files.Count - _oneName,
                    KeysWhereExtensionsAreAllEqual = _extensionsEqual,
                    KeysWhereExtensionsDiffer = _files.Count - _extensionsEqual,
                    KeysInvolvingZeroByteAsset = _zeroByteInvolved,
                    KeysNotInvolvingZeroByteAsset = _files.Count - _zeroByteInvolved,
                    PhysicalFilesPerKey = Summarise(_files),
                    DistinctFileNamesPerKey = Summarise(_names),
                    DistinctMediaAssetsPerKey = Summarise(_assets),
                    NameShapeByExtensionHomogeneity = Joint(
                        "NameShapeByExtensionHomogeneity", level,
                        NameShapeLabels, ExtensionLabels, _nameByExtension),
                    NameShapeByZeroByteInvolvement = Joint(
                        "NameShapeByZeroByteInvolvement", level,
                        NameShapeLabels, ZeroByteLabels, _nameByZeroByte),
                };
            }

            private sealed class AgreementAccumulator
            {
                private int _assetDates;
                private int _inOneSource;
                private int _inOneGroup;
                private int _multiSourceEqual;
                private int _multiSourceDiffer;
                private int _multiGroupEqual;
                private int _multiGroupDiffer;
                private int _maximumAcrossSources;
                private int _maximumAcrossGroups;
                private int _pairs;
                private int _sameGroupEqual;
                private int _sameGroupDiffer;
                private int _crossGroupEqual;
                private int _crossGroupDiffer;

                internal void AddAssetDate(
                    List<SourceObservation> perSource,
                    int deviceGroupCount,
                    int distinctTokens,
                    bool allEqual)
                {
                    _assetDates++;

                    if (perSource.Count == 1)
                    {
                        _inOneSource++;
                    }
                    else
                    {
                        if (allEqual)
                        {
                            _multiSourceEqual++;
                        }
                        else
                        {
                            _multiSourceDiffer++;
                        }

                        _maximumAcrossSources = Math.Max(_maximumAcrossSources, distinctTokens);
                    }

                    if (deviceGroupCount == 1)
                    {
                        _inOneGroup++;
                    }
                    else
                    {
                        if (allEqual)
                        {
                            _multiGroupEqual++;
                        }
                        else
                        {
                            _multiGroupDiffer++;
                        }

                        _maximumAcrossGroups = Math.Max(_maximumAcrossGroups, distinctTokens);
                    }

                    // Unordered pairs, each once. A payload represented in three sources on one date
                    // contributes three observations, which is why these are reported as pair-level
                    // statistics and never added to the asset-and-date counts above.
                    for (var left = 0; left < perSource.Count; left++)
                    {
                        for (var right = left + 1; right < perSource.Count; right++)
                        {
                            var a = perSource[left];
                            var b = perSource[right];

                            _pairs++;

                            var unionHoldsOneToken =
                                a.MinimumToken == a.MaximumToken
                                && b.MinimumToken == b.MaximumToken
                                && a.MinimumToken == b.MinimumToken;

                            if (a.DeviceGroupIndex == b.DeviceGroupIndex)
                            {
                                if (unionHoldsOneToken)
                                {
                                    _sameGroupEqual++;
                                }
                                else
                                {
                                    _sameGroupDiffer++;
                                }
                            }
                            else if (unionHoldsOneToken)
                            {
                                _crossGroupEqual++;
                            }
                            else
                            {
                                _crossGroupDiffer++;
                            }
                        }
                    }
                }

                internal SameAssetAgreement Build() => new()
                {
                    AssetDateCount = _assetDates,
                    AssetDatesInOneSource = _inOneSource,
                    AssetDatesInSeveralSources = _assetDates - _inOneSource,
                    AssetDatesInOneDeviceGroup = _inOneGroup,
                    AssetDatesInSeveralDeviceGroups = _assetDates - _inOneGroup,
                    MultiSourceAssetDatesAllTokensEqual = _multiSourceEqual,
                    MultiSourceAssetDatesTokensDiffer = _multiSourceDiffer,
                    MultiDeviceGroupAssetDatesAllTokensEqual = _multiGroupEqual,
                    MultiDeviceGroupAssetDatesTokensDiffer = _multiGroupDiffer,
                    MaximumDistinctTokensOnOneAssetDateAcrossSources = _maximumAcrossSources,
                    MaximumDistinctTokensOnOneAssetDateAcrossDeviceGroups = _maximumAcrossGroups,
                    SourcePairCount = _pairs,
                    SameDeviceGroupPairsAllTokensEqual = _sameGroupEqual,
                    SameDeviceGroupPairsTokensDiffer = _sameGroupDiffer,
                    CrossDeviceGroupPairsAllTokensEqual = _crossGroupEqual,
                    CrossDeviceGroupPairsTokensDiffer = _crossGroupDiffer,
                };
            }

            /// <remarks>
            /// Most-local-wins precedence, evaluated in order, so every payload carrying several tokens
            /// lands in exactly one class. The last class is the complement: if no single date holds two
            /// tokens while the payload holds two overall, the disagreement can only lie across dates.
            /// </remarks>
            private sealed class LocalityAccumulator
            {
                private int _population;
                private int _withinSourceAndDate;
                private int _withinGroupAndDate;
                private int _acrossGroups;
                private int _acrossDates;

                internal void Add(
                    bool severalTokens,
                    bool differsWithinSourceAndDate,
                    bool differsWithinDeviceGroupAndDate,
                    bool differsWithinDate)
                {
                    if (!severalTokens)
                    {
                        return;
                    }

                    _population++;

                    if (differsWithinSourceAndDate)
                    {
                        _withinSourceAndDate++;
                    }
                    else if (differsWithinDeviceGroupAndDate)
                    {
                        _withinGroupAndDate++;
                    }
                    else if (differsWithinDate)
                    {
                        _acrossGroups++;
                    }
                    else
                    {
                        _acrossDates++;
                    }
                }

                internal NumericValueLocalityCounts Build() => new()
                {
                    AssetsWithSeveralDistinctTokens = _population,
                    WithinOneSourceAndDate = _withinSourceAndDate,
                    WithinOneDeviceGroupAndDate = _withinGroupAndDate,
                    AcrossDeviceGroupsOnly = _acrossGroups,
                    AcrossDatesOnly = _acrossDates,
                };
            }

            private sealed class DuplicateAccumulator(ScopeLevel scope)
            {
                private int _groups;
                private int _same;

                internal void Add(int copies, int minimumToken, int maximumToken)
                {
                    if (copies < 2)
                    {
                        return;
                    }

                    _groups++;

                    if (minimumToken == maximumToken)
                    {
                        _same++;
                    }
                }

                internal DuplicateCopyAgreement Build() => new()
                {
                    Scope = scope,
                    GroupsWithSeveralSupportedCopies = _groups,
                    GroupsWhereAllCopiesCarryTheSameToken = _same,
                    GroupsWhereCopiesCarryDifferentTokens = _groups - _same,
                };
            }
        }
    }
}
