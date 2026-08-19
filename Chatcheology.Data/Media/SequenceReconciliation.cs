namespace Chatcheology.Data.Media
{
    /// <summary>
    /// The population figures a Stage B1 run must be reconciled against before anything else is
    /// believed.
    /// </summary>
    /// <remarks>
    /// The service enforces the <em>internal</em> identities — that supported and unsupported dated files
    /// sum to the dated files, that dated and undated sum to the total, and that assets with and without
    /// supported evidence sum to the assets — and refuses to return a census when one fails. The
    /// archive-specific values a real run expects are pinned by that run's harness, not compiled in
    /// here, because production must describe any workspace rather than this one.
    /// </remarks>
    public sealed class SequenceReconciliation
    {
        /// <summary>Physical files examined.</summary>
        public required int MediaFileCount { get; init; }

        /// <summary>Files carrying a committed <c>FileDate</c>.</summary>
        public required int DatedFileCount { get; init; }

        /// <summary>Files carrying none.</summary>
        public required int UndatedFileCount { get; init; }

        /// <summary>Dated files whose suffix is exactly four ASCII digits.</summary>
        public required int SupportedFileCount { get; init; }

        /// <summary>Dated files whose suffix is anything else.</summary>
        public required int UnsupportedDatedFileCount { get; init; }

        /// <summary>Supported observations from files whose recorded extension is null.</summary>
        public required int SupportedObservationsFromNullExtensionFiles { get; init; }

        /// <summary>Assets examined.</summary>
        public required int MediaAssetCount { get; init; }

        /// <summary>Assets with at least one supported observation.</summary>
        public required int AssetsWithSupportedEvidence { get; init; }

        /// <summary>Assets with none.</summary>
        public required int AssetsWithoutSupportedEvidence { get; init; }

        /// <summary>Sources examined.</summary>
        public required int MediaSourceCount { get; init; }

        /// <summary>Device groups the caller assigned those sources to.</summary>
        public required int DeviceGroupCount { get; init; }
    }
}
