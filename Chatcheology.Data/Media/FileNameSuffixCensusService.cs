using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Counts what recovered file names look like after the <c>-YYYYMMDD-WA</c> marker Phase 5
    /// already recognises.
    /// </summary>
    /// <remarks>
    /// Read-only, in the sense SQLite itself enforces: the connection is opened <c>Mode=ReadOnly</c>,
    /// so a stray write fails rather than succeeding quietly. Nothing is persisted, no schema is
    /// touched, and no media file is opened.
    /// <para>
    /// This is a syntax census and nothing more. It reaches no conclusion about any attachment, it
    /// generates no candidates, it measures no ordering, and it defines no supported grammar — the
    /// whole point is to produce the distributions a grammar decision can then be argued from.
    /// </para>
    /// <para>
    /// It is a separate path from the matching engine by design. <c>WorkspaceMatchingService</c> and
    /// its tests stay exactly as they are, including the test proving that matching reads no path or
    /// file name evidence. This service is the one place permitted to read <c>MediaFile.FileName</c>
    /// and <c>MediaFile.Extension</c>; it still reads no <c>RootPath</c> and no <c>RelativePath</c>.
    /// </para>
    /// </remarks>
    public sealed class FileNameSuffixCensusService
    {
        /// <summary>How many name shapes are reported individually before the rest are pooled.</summary>
        private const int ReportedSignatureCount = 20;

        /// <summary>
        /// Censuses every file name in the workspace at <paramref name="databasePath"/>.
        /// </summary>
        /// <param name="databasePath">
        /// An existing workspace at the current schema version, opened read-only. Never created,
        /// never migrated.
        /// </param>
        /// <exception cref="FileNotFoundException">There is no workspace at that path.</exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version, or its media state is not one this
        /// census can describe — hashing incomplete, a file linked to no asset or to one that does
        /// not exist, a file and its asset disagreeing about their hash, or a file carrying more
        /// than one asset link. Nothing is repaired.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was signalled. No census is returned.
        /// </exception>
        public FileNameSuffixCensus Analyse(
            string databasePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            // Checked before any work, so a cancelled call cannot return a census merely because
            // the workspace it was pointed at happened to hold nothing to iterate.
            cancellationToken.ThrowIfCancellationRequested();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(databasePath);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a file name census");

            var accumulator = new CensusAccumulator(ReadMediaSources(connection));

            ReadMediaFiles(connection, accumulator, cancellationToken);

            return accumulator.Build(ReportedSignatureCount);
        }

        private static Dictionary<long, string> ReadMediaSources(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT MediaSourceID, SourceType FROM MediaSource ORDER BY MediaSourceID;";

            var sources = new Dictionary<long, string>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                sources[reader.GetInt64(0)] = reader.GetString(1);
            }

            return sources;
        }

        /// <summary>
        /// Reads and validates every media row in one ordered pass, classifying each file once.
        /// </summary>
        /// <remarks>
        /// Only the columns this census is entitled to use are selected. No root path and no
        /// relative path appears in the statement, which keeps that boundary a property of the query
        /// rather than a promise about the code above it.
        /// <para>
        /// The join to <c>MediaAssetFile</c> could in principle return a file twice, if a workspace
        /// somehow held two links for one file. The rows are ordered by <c>MediaFileID</c> so such a
        /// pair would be adjacent, and the second is refused before any counter moves: a corrupt
        /// cardinality must fail the run, never silently count one physical file as two.
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
                    mediaSourceID: reader.GetInt64(1),
                    fileName: reader.GetString(2),
                    extension: reader.IsDBNull(3) ? null : reader.GetString(3),
                    hasFileDate: !reader.IsDBNull(4),
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
                $"carries one payload, and the workspace's own unique constraint says so, which " +
                $"makes this a workspace written by something that did not enforce it. Counting " +
                $"the file once per link would quietly inflate this census. Nothing has been " +
                $"censused and the workspace is unchanged.");
        }

        /// <remarks>
        /// These four checks are the same completed-Phase-5 rules the matching engine applies, and
        /// they are deliberately a second copy rather than a shared helper: extracting one would
        /// mean editing the frozen matching path to serve this census, which is a worse trade than
        /// maintaining two copies of a rule that is itself short and covered by tests on both sides.
        /// </remarks>
        private static void RequireHashedFile(SqliteDataReader reader, long mediaFileID)
        {
            if (!reader.IsDBNull(5))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has no SHA-256, so media hashing is incomplete and " +
                $"Phase 5 has not finished for this workspace. A census taken now would describe " +
                $"whatever fraction of the archive happened to be hashed as though it were the " +
                $"archive. Nothing has been censused and the workspace is unchanged.");
        }

        private static void RequireAssetLink(SqliteDataReader reader, long mediaFileID)
        {
            if (reader.IsDBNull(6))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is hashed but linked to no MediaAsset, so " +
                    $"deduplication is incomplete for this workspace. Nothing has been censused " +
                    $"and the workspace is unchanged.");
            }

            if (reader.IsDBNull(7))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is linked to a MediaAsset that does not exist. The " +
                    $"workspace's foreign keys forbid this, so it was written with enforcement " +
                    $"disabled and its media relationships cannot be trusted. Nothing has been " +
                    $"censused and the workspace is unchanged.");
            }
        }

        /// <remarks>
        /// Compared ignoring case because both hash columns are declared <c>COLLATE NOCASE</c>: to
        /// the database a hash written in either case is one value, and a case-sensitive comparison
        /// here would report a sound workspace as corrupt.
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
                $"linked to, so the file's content identity and its deduplication disagree. " +
                $"Nothing has been censused and the workspace is unchanged.");
        }

        /// <summary>
        /// Accumulates the census while the media rows stream past.
        /// </summary>
        /// <remarks>
        /// What is held is bounded by the archive: one entry per asset, one per observed width, one
        /// per observed shape. No per-file record is retained, and nothing here grows with any
        /// combination of files.
        /// </remarks>
        private sealed class CensusAccumulator
        {
            private readonly Dictionary<long, SourceState> _sources = [];
            private readonly Dictionary<long, AssetState> _assets = [];
            private readonly Dictionary<SuffixSyntaxClass, ClassState> _classes = [];
            private readonly Dictionary<int, WidthState> _pureWidths = [];
            private readonly Dictionary<int, WidthState> _decoratedWidths = [];
            private readonly HashSet<string> _distinctPureDigitStrings = [];
            private readonly HashSet<string> _distinctDecoratedDigitStrings = [];
            private readonly Dictionary<string, SignatureState> _suffixSignatures = [];
            private readonly Dictionary<string, SignatureState> _undatedSignatures = [];
            private readonly UndatedFeatureState _undatedFeatures = new();

            private int _mediaFileCount;
            private int _datedFileCount;
            private int _undatedFileCount;
            private int _markerLocatedCount;
            private int _datedFilesWithNoLocatableMarker;
            private int _nullExtensionCount;
            private int _extensionMismatchCount;

            internal CensusAccumulator(Dictionary<long, string> sourceTypes)
            {
                foreach (var (mediaSourceID, sourceType) in sourceTypes)
                {
                    _sources[mediaSourceID] = new SourceState(sourceType);
                }
            }

            internal void Add(
                long mediaSourceID,
                string fileName,
                string? extension,
                bool hasFileDate,
                long mediaAssetID,
                long assetSizeBytes)
            {
                _mediaFileCount++;

                var source = ResolveSource(mediaSourceID);
                source.MediaFileCount++;

                var asset = ResolveAsset(mediaAssetID, assetSizeBytes);
                asset.PhysicalCopyCount++;
                asset.DistinctNames.Add(fileName);
                asset.DistinctNamesIgnoringCase.Add(fileName);

                var extensionMatchesEnding =
                    extension is not null
                    && fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

                if (extension is null)
                {
                    _nullExtensionCount++;
                }
                else if (!extensionMatchesEnding)
                {
                    _extensionMismatchCount++;
                }

                if (!hasFileDate)
                {
                    _undatedFileCount++;
                    source.MediaFileWithNullFileDateCount++;

                    AddUndatedName(source, mediaAssetID, fileName, extension, extensionMatchesEnding);

                    return;
                }

                _datedFileCount++;
                source.MediaFileWithFileDateCount++;

                if (!WhatsAppNameMarker.TryLocate(fileName, out var suffixStart, out _))
                {
                    _datedFilesWithNoLocatableMarker++;

                    return;
                }

                _markerLocatedCount++;

                AddSuffix(
                    source,
                    asset,
                    mediaAssetID,
                    StripExtension(fileName[suffixStart..], extension, extensionMatchesEnding));
            }

            /// <summary>
            /// Files one suffix into its syntax class and the measurements that class carries.
            /// </summary>
            private void AddSuffix(
                SourceState source, AssetState asset, long mediaAssetID, string suffix)
            {
                var digitLength = LeadingDigitLength(suffix);

                var suffixClass = suffix.Length == 0
                    ? SuffixSyntaxClass.EmptySuffix
                    : digitLength == 0
                        ? SuffixSyntaxClass.NonNumericSuffix
                        : digitLength == suffix.Length
                            ? SuffixSyntaxClass.PurelyNumericSuffix
                            : SuffixSyntaxClass.NumericPrefixWithTrailingDecoration;

                ResolveClass(_classes, suffixClass).Add(mediaAssetID);
                ResolveClass(source.Classes, suffixClass).Add(mediaAssetID);

                switch (suffixClass)
                {
                    case SuffixSyntaxClass.PurelyNumericSuffix:
                        AddWidth(_pureWidths, _distinctPureDigitStrings, suffix);

                        asset.PureNumericObservationCount++;
                        asset.PureDigitStrings.Add(suffix);
                        asset.PureNumericValues.Add(CanonicalValue(suffix));

                        break;

                    case SuffixSyntaxClass.NumericPrefixWithTrailingDecoration:
                        AddWidth(
                            _decoratedWidths,
                            _distinctDecoratedDigitStrings,
                            suffix[..digitLength]);

                        AddSignature(_suffixSignatures, suffix, mediaAssetID);

                        break;

                    case SuffixSyntaxClass.NonNumericSuffix:
                        AddSignature(_suffixSignatures, suffix, mediaAssetID);

                        break;
                }
            }

            /// <summary>
            /// Records the syntactic features of one name that carries no committed date.
            /// </summary>
            private void AddUndatedName(
                SourceState source,
                long mediaAssetID,
                string fileName,
                string? extension,
                bool extensionMatchesEnding)
            {
                _undatedFeatures.Add(fileName);
                source.UndatedFeatures.Add(fileName);

                AddSignature(
                    _undatedSignatures,
                    StripExtension(fileName, extension, extensionMatchesEnding),
                    mediaAssetID);
            }

            /// <summary>
            /// Removes the committed extension from the end of <paramref name="value"/>.
            /// </summary>
            /// <remarks>
            /// The recorded extension is used rather than a fresh cut at the last full stop, so the
            /// census follows what the workspace actually stored. A file whose extension is null,
            /// or whose recorded extension is not how its name ends, keeps its whole remainder — and
            /// both of those populations are counted, so neither is silently absorbed.
            /// </remarks>
            private static string StripExtension(
                string value, string? extension, bool extensionMatchesEnding) =>
                extensionMatchesEnding && extension is not null && value.Length >= extension.Length
                    ? value[..^extension.Length]
                    : value;

            /// <summary>How many ASCII digits <paramref name="value"/> opens with.</summary>
            private static int LeadingDigitLength(string value)
            {
                var length = 0;

                while (length < value.Length && char.IsAsciiDigit(value[length]))
                {
                    length++;
                }

                return length;
            }

            /// <summary>
            /// The comparison form of a digit string: leading zeroes removed, an all-zero string
            /// becoming <c>0</c>.
            /// </summary>
            /// <remarks>
            /// Built for comparison only and never reported. The original digit string and its width
            /// are what the census carries, because a width convention that changed part way through
            /// the archive is itself a finding. Nothing is parsed into a number, so a digit run of
            /// any length is handled without overflow.
            /// </remarks>
            private static string CanonicalValue(string digits)
            {
                var firstSignificant = 0;

                while (firstSignificant < digits.Length - 1 && digits[firstSignificant] == '0')
                {
                    firstSignificant++;
                }

                return digits[firstSignificant..];
            }

            private static void AddWidth(
                Dictionary<int, WidthState> widths, HashSet<string> distinct, string digits)
            {
                distinct.Add(digits);

                if (!widths.TryGetValue(digits.Length, out var width))
                {
                    width = new WidthState(digits);
                    widths[digits.Length] = width;
                }

                width.Add(digits);
            }

            private static void AddSignature(
                Dictionary<string, SignatureState> signatures, string value, long mediaAssetID)
            {
                var signature = FileNameShapeSignature.Normalise(value);

                if (!signatures.TryGetValue(signature, out var state))
                {
                    state = new SignatureState();
                    signatures[signature] = state;
                }

                state.Add(mediaAssetID);
            }

            private static ClassState ResolveClass(
                Dictionary<SuffixSyntaxClass, ClassState> classes, SuffixSyntaxClass suffixClass)
            {
                if (!classes.TryGetValue(suffixClass, out var state))
                {
                    state = new ClassState();
                    classes[suffixClass] = state;
                }

                return state;
            }

            private SourceState ResolveSource(long mediaSourceID)
            {
                // A media file whose source is not in MediaSource cannot exist: the column is a
                // foreign key. Creating the entry rather than indexing blindly keeps the census
                // describable even for a workspace written without enforcement.
                if (!_sources.TryGetValue(mediaSourceID, out var source))
                {
                    source = new SourceState("<unrecorded>");
                    _sources[mediaSourceID] = source;
                }

                return source;
            }

            private AssetState ResolveAsset(long mediaAssetID, long sizeBytes)
            {
                if (!_assets.TryGetValue(mediaAssetID, out var asset))
                {
                    asset = new AssetState(sizeBytes);
                    _assets[mediaAssetID] = asset;
                }

                return asset;
            }

            internal FileNameSuffixCensus Build(int reportedSignatureCount)
            {
                var agreement = new int[3];
                var digitStringBuckets = new Dictionary<int, int>();
                var valueBuckets = new Dictionary<int, int>();

                var oneName = 0;
                var severalNames = 0;
                var caseOnly = 0;
                var withPureNumeric = 0;
                var zeroByteAssets = 0;
                var zeroByteFiles = 0;
                var zeroByteNames = 0;

                foreach (var asset in _assets.Values)
                {
                    if (asset.DistinctNames.Count == 1)
                    {
                        oneName++;
                    }
                    else
                    {
                        severalNames++;

                        if (asset.DistinctNamesIgnoringCase.Count == 1)
                        {
                            caseOnly++;
                        }
                    }

                    if (asset.SizeBytes == 0)
                    {
                        zeroByteAssets++;
                        zeroByteFiles += asset.PhysicalCopyCount;
                        zeroByteNames += asset.DistinctNames.Count;
                    }

                    if (asset.PureNumericObservationCount == 0)
                    {
                        continue;
                    }

                    withPureNumeric++;

                    Increment(digitStringBuckets, asset.PureDigitStrings.Count);
                    Increment(valueBuckets, asset.PureNumericValues.Count);

                    if (asset.PureNumericObservationCount > 1)
                    {
                        agreement[(int)Agreement(asset)]++;
                    }
                }

                return BuildCensus(
                    reportedSignatureCount,
                    agreement,
                    digitStringBuckets,
                    valueBuckets,
                    oneName,
                    severalNames,
                    caseOnly,
                    withPureNumeric,
                    zeroByteAssets,
                    zeroByteFiles,
                    zeroByteNames);
            }

            /// <summary>
            /// Which of the three outcomes one asset's several numeric observations fall into.
            /// </summary>
            /// <remarks>
            /// By precedence, so an asset with three or more observations lands in exactly one
            /// state: identical strings first, then identical values across differing strings, then
            /// differing values. Nothing is normalised into agreement — a value seen at two widths
            /// is its own outcome, not a match.
            /// </remarks>
            private static NumericSuffixAgreement Agreement(AssetState asset) =>
                asset.PureDigitStrings.Count == 1
                    ? NumericSuffixAgreement.ExactSameDigitString
                    : asset.PureNumericValues.Count == 1
                        ? NumericSuffixAgreement.SameValueDifferentWidth
                        : NumericSuffixAgreement.DifferentNumericValue;

            private static void Increment(Dictionary<int, int> buckets, int key) =>
                buckets[key] = buckets.GetValueOrDefault(key) + 1;

            private FileNameSuffixCensus BuildCensus(
                int reportedSignatureCount,
                int[] agreement,
                Dictionary<int, int> digitStringBuckets,
                Dictionary<int, int> valueBuckets,
                int oneName,
                int severalNames,
                int caseOnly,
                int withPureNumeric,
                int zeroByteAssets,
                int zeroByteFiles,
                int zeroByteNames) => new()
            {
                MediaSources = _sources
                    .OrderBy(source => source.Key)
                    .Select(source => source.Value.Build(source.Key))
                    .ToList(),

                MediaFileCount = _mediaFileCount,
                MediaFileWithFileDateCount = _datedFileCount,
                MediaFileWithNullFileDateCount = _undatedFileCount,

                MarkerLocatedCount = _markerLocatedCount,
                DatedFilesWithNoLocatableMarker = _datedFilesWithNoLocatableMarker,
                NullExtensionCount = _nullExtensionCount,
                ExtensionDoesNotMatchNameEndingCount = _extensionMismatchCount,

                SuffixClasses = BuildClasses(_classes),
                PurelyNumericWidths = BuildWidths(_pureWidths, _distinctPureDigitStrings),
                DecoratedNumericPrefixWidths =
                    BuildWidths(_decoratedWidths, _distinctDecoratedDigitStrings),

                SuffixShapeSignatures = TopSignatures(_suffixSignatures, reportedSignatureCount),
                DistinctSuffixShapeSignatures = _suffixSignatures.Count,
                SuffixShapeSignaturesSeenOnce = SeenOnce(_suffixSignatures),

                AssetsWithOneDistinctFileName = oneName,
                AssetsWithSeveralDistinctFileNames = severalNames,
                AssetsWhoseNamesDifferOnlyByCase = caseOnly,
                AssetsWithAnyPurelyNumericSuffix = withPureNumeric,
                AssetsWithNoPurelyNumericSuffix = _assets.Count - withPureNumeric,

                NumericAgreement = new NumericAgreementCounts
                {
                    ExactSameDigitString =
                        agreement[(int)NumericSuffixAgreement.ExactSameDigitString],
                    SameValueDifferentWidth =
                        agreement[(int)NumericSuffixAgreement.SameValueDifferentWidth],
                    DifferentNumericValue =
                        agreement[(int)NumericSuffixAgreement.DifferentNumericValue],
                },

                DistinctDigitStringsPerAsset = BuildBuckets(digitStringBuckets),
                DistinctNumericValuesPerAsset = BuildBuckets(valueBuckets),

                ZeroByteAssetCount = zeroByteAssets,
                ZeroBytePhysicalFileCount = zeroByteFiles,
                ZeroByteAssetDistinctNameCount = zeroByteNames,

                UndatedFeatures = _undatedFeatures.Build(),
                UndatedBasenameShapeSignatures =
                    TopSignatures(_undatedSignatures, reportedSignatureCount),
                DistinctUndatedBasenameShapeSignatures = _undatedSignatures.Count,
                UndatedBasenameShapeSignaturesSeenOnce = SeenOnce(_undatedSignatures),
            };

            private static IReadOnlyList<SuffixClassCounts> BuildClasses(
                Dictionary<SuffixSyntaxClass, ClassState> classes) =>
                Enum.GetValues<SuffixSyntaxClass>()
                    .Select(suffixClass =>
                    {
                        var found = classes.TryGetValue(suffixClass, out var state);

                        return new SuffixClassCounts
                        {
                            SuffixClass = suffixClass,
                            FileCount = found ? state!.FileCount : 0,
                            AssetCount = found ? state!.Assets.Count : 0,
                        };
                    })
                    .ToList();

            private static DigitWidthDistribution BuildWidths(
                Dictionary<int, WidthState> widths, HashSet<string> distinctDigitStrings)
            {
                var ordered = widths.OrderBy(width => width.Key).ToList();

                // Ties go to the smaller width, so the dominant width does not depend on
                // dictionary enumeration order.
                var dominant = ordered
                    .OrderByDescending(width => width.Value.FileCount)
                    .ThenBy(width => width.Key)
                    .Select(width => (Width: width.Key, width.Value.FileCount))
                    .FirstOrDefault();

                return new DigitWidthDistribution
                {
                    Widths = ordered
                        .Select(width => new DigitWidthCount
                        {
                            Width = width.Key,
                            FileCount = width.Value.FileCount,
                            MinimumDigitString = width.Value.Minimum,
                            MaximumDigitString = width.Value.Maximum,
                        })
                        .ToList(),
                    TotalObservations = ordered.Sum(width => width.Value.FileCount),
                    DominantWidth = dominant.Width,
                    DominantWidthCount = dominant.FileCount,
                    DistinctDigitStrings = distinctDigitStrings.Count,
                };
            }

            private static IReadOnlyList<DistinctCountBucket> BuildBuckets(
                Dictionary<int, int> buckets) =>
                buckets
                    .OrderBy(bucket => bucket.Key)
                    .Select(bucket => new DistinctCountBucket
                    {
                        DistinctCount = bucket.Key,
                        AssetCount = bucket.Value,
                    })
                    .ToList();

            private static int SeenOnce(Dictionary<string, SignatureState> signatures) =>
                signatures.Count(signature => signature.Value.FileCount == 1);

            /// <summary>
            /// The most frequent shapes, with everything else pooled into one row.
            /// </summary>
            /// <remarks>
            /// Ordered by file count descending and then by the signature itself, so a tie cannot
            /// make the table depend on enumeration order. The pooled row is named <c>Other</c>,
            /// which no real signature can collide with: a signature is built only from the letters
            /// <c>A</c>, <c>D</c> and <c>X</c>, digits, and retained punctuation.
            /// </remarks>
            private static IReadOnlyList<ShapeSignatureCount> TopSignatures(
                Dictionary<string, SignatureState> signatures, int limit)
            {
                var ordered = signatures
                    .OrderByDescending(signature => signature.Value.FileCount)
                    .ThenBy(signature => signature.Key, StringComparer.Ordinal)
                    .ToList();

                var reported = ordered
                    .Take(limit)
                    .Select(signature => new ShapeSignatureCount
                    {
                        Signature = signature.Key,
                        FileCount = signature.Value.FileCount,
                        AssetCount = signature.Value.Assets.Count,
                    })
                    .ToList();

                if (ordered.Count <= limit)
                {
                    return reported;
                }

                var pooledFiles = 0;
                var pooledAssets = new HashSet<long>();

                foreach (var signature in ordered.Skip(limit))
                {
                    pooledFiles += signature.Value.FileCount;
                    pooledAssets.UnionWith(signature.Value.Assets);
                }

                reported.Add(new ShapeSignatureCount
                {
                    Signature = "Other",
                    FileCount = pooledFiles,
                    AssetCount = pooledAssets.Count,
                });

                return reported;
            }

            private sealed class ClassState
            {
                internal int FileCount { get; private set; }

                internal HashSet<long> Assets { get; } = [];

                internal void Add(long mediaAssetID)
                {
                    FileCount++;
                    Assets.Add(mediaAssetID);
                }
            }

            private sealed class WidthState
            {
                internal WidthState(string digits)
                {
                    Minimum = digits;
                    Maximum = digits;
                }

                internal int FileCount { get; private set; }

                internal string Minimum { get; private set; }

                internal string Maximum { get; private set; }

                /// <remarks>
                /// Compared ordinally. Every string reaching one instance has the same width, so an
                /// ordinal comparison and a numeric one give the same order, and no parse is needed.
                /// </remarks>
                internal void Add(string digits)
                {
                    FileCount++;

                    if (string.CompareOrdinal(digits, Minimum) < 0)
                    {
                        Minimum = digits;
                    }

                    if (string.CompareOrdinal(digits, Maximum) > 0)
                    {
                        Maximum = digits;
                    }
                }
            }

            private sealed class SignatureState
            {
                internal int FileCount { get; private set; }

                internal HashSet<long> Assets { get; } = [];

                internal void Add(long mediaAssetID)
                {
                    FileCount++;
                    Assets.Add(mediaAssetID);
                }
            }

            private sealed class AssetState(long sizeBytes)
            {
                internal long SizeBytes { get; } = sizeBytes;

                internal int PhysicalCopyCount { get; set; }

                internal int PureNumericObservationCount { get; set; }

                internal HashSet<string> DistinctNames { get; } = new(StringComparer.Ordinal);

                internal HashSet<string> DistinctNamesIgnoringCase { get; } =
                    new(StringComparer.OrdinalIgnoreCase);

                internal HashSet<string> PureDigitStrings { get; } = new(StringComparer.Ordinal);

                internal HashSet<string> PureNumericValues { get; } = new(StringComparer.Ordinal);
            }

            private sealed class SourceState(string sourceType)
            {
                internal int MediaFileCount { get; set; }

                internal int MediaFileWithFileDateCount { get; set; }

                internal int MediaFileWithNullFileDateCount { get; set; }

                internal Dictionary<SuffixSyntaxClass, ClassState> Classes { get; } = [];

                internal UndatedFeatureState UndatedFeatures { get; } = new();

                internal MediaSourceNameSummary Build(long mediaSourceID) => new()
                {
                    MediaSourceID = mediaSourceID,
                    SourceType = sourceType,
                    IsWhatsAppMediaDirectory = string.Equals(
                        sourceType, MediaSourceTypes.WhatsAppMediaDirectory, StringComparison.Ordinal),
                    MediaFileCount = MediaFileCount,
                    MediaFileWithFileDateCount = MediaFileWithFileDateCount,
                    MediaFileWithNullFileDateCount = MediaFileWithNullFileDateCount,
                    SuffixClasses = BuildClasses(Classes),
                    UndatedFeatures = UndatedFeatures.Build(),
                };
            }

            /// <summary>
            /// Counts the syntactic features of names carrying no committed date.
            /// </summary>
            /// <remarks>
            /// Every feature is examined for every name, because they overlap by design: one name
            /// can hold an eight-digit run, a <c>-WA</c> marker and the complete structure at once,
            /// and forcing it into a single category would throw away the fact that it does.
            /// </remarks>
            private sealed class UndatedFeatureState
            {
                private int _nameCount;
                private int _eightDigitRun;
                private int _hyphenPrefixedRun;
                private int _validDateRun;
                private int _dashWAMarker;
                private int _waFollowedByDigits;
                private int _fullStructure;
                private int _fullStructureInvalidDate;
                private int _matchedNoFeature;

                internal void Add(string fileName)
                {
                    _nameCount++;

                    var matched = false;

                    matched |= Count(NameFeatures.ContainsEightDigitRun(fileName), ref _eightDigitRun);
                    matched |= Count(
                        NameFeatures.ContainsHyphenPrefixedEightDigitRun(fileName),
                        ref _hyphenPrefixedRun);
                    matched |= Count(
                        NameFeatures.ContainsValidCalendarDateRun(fileName), ref _validDateRun);
                    matched |= Count(
                        fileName.Contains(WhatsAppNameMarker.TrailingMarker, StringComparison.Ordinal),
                        ref _dashWAMarker);
                    matched |= Count(
                        NameFeatures.ContainsWAFollowedByDigits(fileName), ref _waFollowedByDigits);
                    matched |= Count(
                        WhatsAppNameMarker.ContainsFullStructure(fileName, withInvalidDateOnly: false),
                        ref _fullStructure);
                    matched |= Count(
                        WhatsAppNameMarker.ContainsFullStructure(fileName, withInvalidDateOnly: true),
                        ref _fullStructureInvalidDate);

                    if (!matched)
                    {
                        _matchedNoFeature++;
                    }
                }

                internal UndatedNameFeatureCounts Build() => new()
                {
                    NameCount = _nameCount,
                    ContainsEightDigitRun = _eightDigitRun,
                    ContainsHyphenPrefixedEightDigitRun = _hyphenPrefixedRun,
                    ContainsValidCalendarDateRun = _validDateRun,
                    ContainsDashWAMarker = _dashWAMarker,
                    ContainsWAFollowedByDigits = _waFollowedByDigits,
                    ContainsFullStructure = _fullStructure,
                    ContainsFullStructureWithInvalidDate = _fullStructureInvalidDate,
                    MatchedNoFeature = _matchedNoFeature,
                };

                private static bool Count(bool present, ref int counter)
                {
                    if (present)
                    {
                        counter++;
                    }

                    return present;
                }
            }
        }
    }
}
