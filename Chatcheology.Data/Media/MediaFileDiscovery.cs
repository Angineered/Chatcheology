namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Walks a media root and describes every physical file beneath it, reading nothing but
    /// directory metadata.
    /// </summary>
    /// <remarks>
    /// The raw source is treated as read-only throughout. Nothing here creates, renames, moves,
    /// deletes or opens a file; the walk asks the directory what is there and stops.
    /// </remarks>
    internal static class MediaFileDiscovery
    {
        /// <summary>
        /// How the tree is walked, and the one place those choices are made.
        /// </summary>
        /// <remarks>
        /// <c>AttributesToSkip = ReparsePoint</c> does two things at once, and both are wanted.
        /// It excludes junctions and symbolic links from the results, and — because the enumerator
        /// applies the same filter when deciding what to descend into — it stops the walk following
        /// one out of the selected tree. A source root the user picked is a promise about which
        /// files are being read, and a junction inside it must not be able to quietly extend that
        /// promise to somewhere else on the disk.
        /// <para>
        /// It also replaces the default value, which is <c>Hidden | System</c>. That is deliberate:
        /// hidden and system files are inventoried like any others. Skipping them would leave parts
        /// of an archive silently missing from a record whose whole purpose is completeness, and a
        /// file's attributes say nothing about whether its content matters.
        /// </para>
        /// <para>
        /// <c>IgnoreInaccessible = false</c> means a directory that cannot be read stops the walk
        /// with an exception instead of being skipped. A short inventory that failed loudly is
        /// recoverable; one that quietly omitted an unreadable folder looks exactly like a complete
        /// one.
        /// </para>
        /// </remarks>
        private static readonly EnumerationOptions InventoryEnumerationOptions = new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };

        /// <summary>
        /// Finds every file beneath <paramref name="normalisedRoot"/>, in the order the workspace
        /// stores them.
        /// </summary>
        /// <remarks>
        /// The results are sorted by canonical relative path rather than left in filesystem order,
        /// so two inventories of one unchanged tree produce the same rows in the same sequence
        /// whatever order the volume happened to hand them back.
        /// <para>
        /// <see cref="DirectoryInfo.EnumerateFiles(string, EnumerationOptions)"/> rather than the
        /// path-returning overload, because the length and attributes then come from the directory
        /// data the walk already read. Asking the filesystem again for each of tens of thousands of
        /// files would be the same answer at several times the cost.
        /// </para>
        /// </remarks>
        internal static List<DiscoveredMediaFile> Discover(
            string normalisedRoot, string sourceType, CancellationToken cancellationToken)
        {
            var discovered = new List<DiscoveredMediaFile>();

            foreach (var file in new DirectoryInfo(normalisedRoot)
                         .EnumerateFiles("*", InventoryEnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath =
                    MediaSourcePath.ToCanonicalRelativePath(normalisedRoot, file.FullName);

                var extension = MediaClassification.NormaliseExtension(file.Name);
                var attributes = file.Attributes;

                discovered.Add(new DiscoveredMediaFile
                {
                    RelativePath = relativePath,
                    FileName = file.Name,
                    Extension = extension,
                    SizeBytes = file.Length,
                    MediaType = MediaClassification.Classify(extension, file.Length),
                    FileDate = MediaClassification.DeriveFileDate(sourceType, file.Name),
                    IsSent = MediaClassification.DeriveIsSent(sourceType, relativePath),
                    IsHidden = attributes.HasFlag(FileAttributes.Hidden),
                    IsSystem = attributes.HasFlag(FileAttributes.System),
                });
            }

            discovered.Sort(static (left, right) =>
                string.CompareOrdinal(left.RelativePath, right.RelativePath));

            return discovered;
        }

        /// <summary>
        /// Counts a discovered file list into the aggregate summary callers see.
        /// </summary>
        internal static MediaDiscoverySummary Summarise(List<DiscoveredMediaFile> files)
        {
            var totalSizeBytes = 0L;
            var imageCount = 0;
            var videoCount = 0;
            var audioCount = 0;
            var documentCount = 0;
            var unknownCount = 0;
            var sentCount = 0;
            var nonSentCount = 0;
            var directionUnknownCount = 0;
            var fileDateCount = 0;
            var zeroByteFileCount = 0;
            var hiddenFileCount = 0;
            var systemFileCount = 0;

            var unknownExtensions = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var file in files)
            {
                totalSizeBytes += file.SizeBytes;

                switch (file.MediaType)
                {
                    case MediaType.Image:
                        imageCount++;
                        break;

                    case MediaType.Video:
                        videoCount++;
                        break;

                    case MediaType.Audio:
                        audioCount++;
                        break;

                    case MediaType.Document:
                        documentCount++;
                        break;

                    default:
                        unknownCount++;

                        var label = MediaClassification.ExtensionLabel(file.Extension);
                        unknownExtensions[label] =
                            unknownExtensions.TryGetValue(label, out var existing) ? existing + 1 : 1;

                        break;
                }

                switch (file.IsSent)
                {
                    case true:
                        sentCount++;
                        break;

                    case false:
                        nonSentCount++;
                        break;

                    default:
                        directionUnknownCount++;
                        break;
                }

                if (file.FileDate is not null)
                {
                    fileDateCount++;
                }

                if (file.SizeBytes == 0)
                {
                    zeroByteFileCount++;
                }

                if (file.IsHidden)
                {
                    hiddenFileCount++;
                }

                if (file.IsSystem)
                {
                    systemFileCount++;
                }
            }

            return new MediaDiscoverySummary
            {
                FileCount = files.Count,
                TotalSizeBytes = totalSizeBytes,
                ImageCount = imageCount,
                VideoCount = videoCount,
                AudioCount = audioCount,
                DocumentCount = documentCount,
                UnknownCount = unknownCount,
                SentCount = sentCount,
                NonSentCount = nonSentCount,
                DirectionUnknownCount = directionUnknownCount,
                FileDateCount = fileDateCount,
                ZeroByteFileCount = zeroByteFileCount,
                HiddenFileCount = hiddenFileCount,
                SystemFileCount = systemFileCount,

                // Most frequent first, then by extension, so the ordering is total and a rerun
                // reports the same list rather than one that depends on dictionary internals.
                UnknownExtensionCounts = unknownExtensions
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new MediaExtensionCount
                    {
                        Extension = entry.Key,
                        Count = entry.Value,
                    })
                    .ToArray(),
            };
        }
    }
}
