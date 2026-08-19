namespace Chatcheology.Data.Media
{
    /// <summary>
    /// The bounded result of one Stage A file name suffix census.
    /// </summary>
    /// <remarks>
    /// Counts, distributions and normalised shapes only. No file name, path or other recovered text
    /// leaves this type: the shape signatures are letter-free and digit-free by construction, and
    /// nothing else here is a string taken from the archive.
    /// <para>
    /// This census says nothing about any attachment. It measures how recovered names are shaped
    /// after the marker the Phase 5 classifier already recognises, so that a supported grammar can
    /// be argued from evidence rather than proposed from an example.
    /// </para>
    /// </remarks>
    public sealed class FileNameSuffixCensus
    {
        /// <summary>Every media source, ordered by identifier.</summary>
        public required IReadOnlyList<MediaSourceNameSummary> MediaSources { get; init; }

        /// <summary>Physical files examined.</summary>
        public required int MediaFileCount { get; init; }

        /// <summary>Files carrying a committed <c>FileDate</c>.</summary>
        public required int MediaFileWithFileDateCount { get; init; }

        /// <summary>Files carrying none.</summary>
        public required int MediaFileWithNullFileDateCount { get; init; }

        /// <summary>Dated files whose marker this census located.</summary>
        public required int MarkerLocatedCount { get; init; }

        /// <summary>
        /// Dated files whose marker this census could <em>not</em> locate.
        /// </summary>
        /// <remarks>
        /// Must be zero. Any other value means this census and the committed classifier disagree
        /// about which eight digits are a date, and no figure below can be trusted until that is
        /// explained.
        /// </remarks>
        public required int DatedFilesWithNoLocatableMarker { get; init; }

        /// <summary>Files whose recorded extension is null, which keep their whole remainder.</summary>
        public required int NullExtensionCount { get; init; }

        /// <summary>
        /// Files whose recorded extension is not how the name actually ends, compared ordinally
        /// ignoring case.
        /// </summary>
        /// <remarks>
        /// Expected to be zero for a workspace this code wrote, because the extension is derived
        /// from the name. A non-zero count means the two were written by different rules, and the
        /// suffix for those files is cut at the end of the name instead.
        /// </remarks>
        public required int ExtensionDoesNotMatchNameEndingCount { get; init; }

        /// <summary>Suffix syntax classes across all dated files, in enum order.</summary>
        public required IReadOnlyList<SuffixClassCounts> SuffixClasses { get; init; }

        /// <summary>Widths of the purely numeric suffixes.</summary>
        public required DigitWidthDistribution PurelyNumericWidths { get; init; }

        /// <summary>
        /// Widths of the leading digits of decorated suffixes, kept separate so a decorated
        /// population cannot inflate the dominant width of the purely numeric one.
        /// </summary>
        public required DigitWidthDistribution DecoratedNumericPrefixWidths { get; init; }

        /// <summary>Shapes of the decorated and non-numeric suffixes, top of the table plus a pooled row.</summary>
        public required IReadOnlyList<ShapeSignatureCount> SuffixShapeSignatures { get; init; }

        /// <summary>Distinct suffix shapes seen.</summary>
        public required int DistinctSuffixShapeSignatures { get; init; }

        /// <summary>Suffix shapes seen exactly once.</summary>
        public required int SuffixShapeSignaturesSeenOnce { get; init; }

        /// <summary>Assets whose files all share one name, compared ordinally.</summary>
        public required int AssetsWithOneDistinctFileName { get; init; }

        /// <summary>Assets carrying more than one distinct name.</summary>
        public required int AssetsWithSeveralDistinctFileNames { get; init; }

        /// <summary>
        /// Assets whose several names collapse to one when letter case is ignored.
        /// </summary>
        public required int AssetsWhoseNamesDifferOnlyByCase { get; init; }

        /// <summary>Assets with at least one purely numeric suffix observation.</summary>
        public required int AssetsWithAnyPurelyNumericSuffix { get; init; }

        /// <summary>Assets with none.</summary>
        public required int AssetsWithNoPurelyNumericSuffix { get; init; }

        /// <summary>The three-way outcome for assets carrying several such observations.</summary>
        public required NumericAgreementCounts NumericAgreement { get; init; }

        /// <summary>Distinct digit strings per asset, ascending.</summary>
        public required IReadOnlyList<DistinctCountBucket> DistinctDigitStringsPerAsset { get; init; }

        /// <summary>Distinct numeric values per asset, ascending.</summary>
        public required IReadOnlyList<DistinctCountBucket> DistinctNumericValuesPerAsset { get; init; }

        /// <summary>Assets holding no payload at all.</summary>
        public required int ZeroByteAssetCount { get; init; }

        /// <summary>Physical files those assets represent.</summary>
        public required int ZeroBytePhysicalFileCount { get; init; }

        /// <summary>Distinct names those assets carry.</summary>
        /// <remarks>
        /// Reported so the zero-byte contribution can be subtracted from the per-asset name figures
        /// without re-running anything. It is included in them, not excluded: silently dropping the
        /// largest single-payload name cluster in the archive would distort exactly the figures it
        /// most affects.
        /// </remarks>
        public required int ZeroByteAssetDistinctNameCount { get; init; }

        /// <summary>Features of the names carrying no <c>FileDate</c>.</summary>
        public required UndatedNameFeatureCounts UndatedFeatures { get; init; }

        /// <summary>Shapes of those names' basenames, top of the table plus a pooled row.</summary>
        public required IReadOnlyList<ShapeSignatureCount> UndatedBasenameShapeSignatures { get; init; }

        /// <summary>Distinct undated basename shapes seen.</summary>
        public required int DistinctUndatedBasenameShapeSignatures { get; init; }

        /// <summary>Undated basename shapes seen exactly once.</summary>
        public required int UndatedBasenameShapeSignaturesSeenOnce { get; init; }
    }
}
