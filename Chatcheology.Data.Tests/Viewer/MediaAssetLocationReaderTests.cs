using Chatcheology.Data.Media;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Viewer;

namespace Chatcheology.Data.Tests.Viewer
{
    /// <summary>
    /// What the media-location reader returns for an already-resolved asset, and in what order.
    /// </summary>
    /// <remarks>
    /// Nothing here is matching or evidence. The reader is handed asset identifiers that are
    /// already resolved and answers only where the workspace records copies of them.
    /// </remarks>
    public class MediaAssetLocationReaderTests
    {
        private static readonly DateOnly Day = new(2026, 4, 6);

        [Fact]
        public void OneAssetWithOneCopy_ReturnsTheStoredFacts()
        {
            using var workspace = new ViewerTestWorkspace();
            var source = workspace.Workspace.AddMediaSource("Store one");

            var asset = workspace.Workspace.AddAssetWithCopy(
                source, MatchingTestData.Hash(1), Day, extension: ".jpg");

            using var connection = workspace.OpenReadOnly();

            var location = Assert.Single(
                new MediaAssetLocationReader().Read(connection, [asset]));

            Assert.Equal(asset, location.MediaAssetID);
            Assert.Equal(source, location.MediaSourceID);
            Assert.Equal("Store one", location.MediaSourceDisplayName);
            Assert.Equal(MediaType.Image, location.MediaType);
            Assert.Equal(".jpg", location.Extension);
            Assert.Equal(1024, location.SizeBytes);
            Assert.False(string.IsNullOrWhiteSpace(location.FileName));
            Assert.False(string.IsNullOrWhiteSpace(location.RootPath));
            Assert.False(string.IsNullOrWhiteSpace(location.RelativePath));
        }

        [Fact]
        public void OneAssetWithSeveralCopies_ReturnsEveryCopy()
        {
            using var workspace = new ViewerTestWorkspace();
            var first = workspace.Workspace.AddMediaSource("Store one");
            var second = workspace.Workspace.AddMediaSource("Store two");
            var hash = MatchingTestData.Hash(1);

            var asset = workspace.Workspace.AddMediaAsset(hash);
            workspace.Workspace.AddMediaFile(first, asset, hash);
            workspace.Workspace.AddMediaFile(first, asset, hash);
            workspace.Workspace.AddMediaFile(second, asset, hash);

            using var connection = workspace.OpenReadOnly();

            var locations = new MediaAssetLocationReader().Read(connection, [asset]);

            Assert.Equal(3, locations.Count);
            Assert.All(locations, location => Assert.Equal(asset, location.MediaAssetID));
            Assert.Equal(3, locations.Select(location => location.MediaFileID).Distinct().Count());
        }

        /// <remarks>
        /// Insertion order of three surrogate keys, and nothing else. No <c>FileDate</c>, no
        /// <c>IsSent</c>, no name: none of the facts a matching decision is built from takes any
        /// part in it, which is what keeps the order presentation rather than a preference.
        /// </remarks>
        [Fact]
        public void SeveralAssets_AreOrderedByAssetThenSourceThenFile()
        {
            using var workspace = new ViewerTestWorkspace();
            var first = workspace.Workspace.AddMediaSource("Store one");
            var second = workspace.Workspace.AddMediaSource("Store two");

            var hashOne = MatchingTestData.Hash(1);
            var hashTwo = MatchingTestData.Hash(2);

            var assetOne = workspace.Workspace.AddMediaAsset(hashOne);
            var assetTwo = workspace.Workspace.AddMediaAsset(hashTwo);

            // Written in an order that is not the order they should come back in.
            workspace.Workspace.AddMediaFile(second, assetTwo, hashTwo);
            workspace.Workspace.AddMediaFile(second, assetOne, hashOne);
            workspace.Workspace.AddMediaFile(first, assetTwo, hashTwo);
            workspace.Workspace.AddMediaFile(first, assetOne, hashOne);

            using var connection = workspace.OpenReadOnly();

            // Requested in the reverse order too, so the result is the reader's ordering rather
            // than the caller's.
            var locations = new MediaAssetLocationReader().Read(connection, [assetTwo, assetOne]);

            Assert.Equal(
                locations
                    .OrderBy(location => location.MediaAssetID)
                    .ThenBy(location => location.MediaSourceID)
                    .ThenBy(location => location.MediaFileID)
                    .Select(location => location.MediaFileID),
                locations.Select(location => location.MediaFileID));

            Assert.Equal(assetOne, locations[0].MediaAssetID);
        }

        [Fact]
        public void OrderingIsStableAcrossRepeatedReads()
        {
            using var workspace = new ViewerTestWorkspace();
            var source = workspace.Workspace.AddMediaSource();
            var hash = MatchingTestData.Hash(1);
            var asset = workspace.Workspace.AddMediaAsset(hash);

            for (var copy = 0; copy < 5; copy++)
            {
                workspace.Workspace.AddMediaFile(source, asset, hash);
            }

            using var connection = workspace.OpenReadOnly();
            var reader = new MediaAssetLocationReader();

            var first = reader.Read(connection, [asset]).Select(l => l.MediaFileID).ToList();
            var second = reader.Read(connection, [asset]).Select(l => l.MediaFileID).ToList();

            Assert.Equal(first, second);
        }

        /// <remarks>
        /// The relative path is stored with <c>/</c> whatever platform wrote it, so composition
        /// translates the separator rather than assuming it already matches.
        /// </remarks>
        [Fact]
        public void FullPath_ComposesTheStoredRootAndRelativePath()
        {
            using var workspace = new ViewerTestWorkspace();
            var source = workspace.Workspace.AddMediaSource();
            var hash = MatchingTestData.Hash(1);
            var asset = workspace.Workspace.AddAssetWithCopy(source, hash, Day);

            using var connection = workspace.OpenReadOnly();

            var location = Assert.Single(new MediaAssetLocationReader().Read(connection, [asset]));

            Assert.Equal(
                Path.Combine(
                    location.RootPath,
                    location.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                location.FullPath);
        }

        /// <remarks>
        /// Composition names a path; it does not claim a file is there. Both halves matter to the
        /// viewer, which must tell a resolved attachment whose copy is reachable from one whose
        /// copy is not.
        /// </remarks>
        [Fact]
        public void FullPath_FindsACopyThatIsThereAndNotOneThatIsNot()
        {
            using var workspace = new ViewerTestWorkspace();
            using var directory = new Media.TemporaryMediaDirectory();

            var source = workspace.Workspace.AddMediaSource();

            workspace.Execute(
                $"""
                UPDATE MediaSource
                SET RootPath = '{directory.RootPath.Replace("'", "''")}'
                WHERE MediaSourceID = {source};
                """);

            var present = workspace.Workspace.AddMediaAsset(MatchingTestData.Hash(1));
            var absent = workspace.Workspace.AddMediaAsset(MatchingTestData.Hash(2));

            workspace.Workspace.AddMediaFile(source, present, MatchingTestData.Hash(1));
            workspace.Workspace.AddMediaFile(source, absent, MatchingTestData.Hash(2));

            using var connection = workspace.OpenReadOnly();

            var locations = new MediaAssetLocationReader().Read(connection, [present, absent]);

            var reachable = locations.Single(location => location.MediaAssetID == present);
            var unreachable = locations.Single(location => location.MediaAssetID == absent);

            Directory.CreateDirectory(Path.GetDirectoryName(reachable.FullPath)!);
            File.WriteAllBytes(reachable.FullPath, [1, 2, 3]);

            Assert.True(File.Exists(reachable.FullPath));
            Assert.False(File.Exists(unreachable.FullPath));
        }

        [Fact]
        public void AFileThatHasNotBeenHashed_IsNotReturned()
        {
            using var workspace = new ViewerTestWorkspace();
            var source = workspace.Workspace.AddMediaSource();
            var hash = MatchingTestData.Hash(1);
            var asset = workspace.Workspace.AddMediaAsset(hash);

            workspace.Workspace.AddMediaFile(source, asset, hash, link: true);
            workspace.Workspace.AddMediaFile(source, asset, hash, link: false);

            using var connection = workspace.OpenReadOnly();

            Assert.Single(new MediaAssetLocationReader().Read(connection, [asset]));
        }

        [Fact]
        public void AnAssetWithNoRecordedCopy_ContributesNothing()
        {
            using var workspace = new ViewerTestWorkspace();
            var asset = workspace.Workspace.AddMediaAsset(MatchingTestData.Hash(1));

            using var connection = workspace.OpenReadOnly();

            Assert.Empty(new MediaAssetLocationReader().Read(connection, [asset]));
        }

        [Fact]
        public void AnEmptyRequest_ReturnsNothingRatherThanThrowing()
        {
            using var workspace = new ViewerTestWorkspace();

            using var connection = workspace.OpenReadOnly();

            Assert.Empty(new MediaAssetLocationReader().Read(connection, []));
        }

        [Fact]
        public void ARepeatedAssetIdentifier_IsRefused()
        {
            using var workspace = new ViewerTestWorkspace();
            var asset = workspace.Workspace.AddMediaAsset(MatchingTestData.Hash(1));

            using var connection = workspace.OpenReadOnly();

            Assert.Throws<ArgumentException>(
                () => new MediaAssetLocationReader().Read(connection, [asset, asset]));
        }

        /// <remarks>
        /// More identifiers than one statement binds, so the request crosses the internal chunk
        /// boundary. The combined result is ordered as a whole, not merely within a chunk, which is
        /// what makes the chunking invisible to a caller.
        /// </remarks>
        [Fact]
        public void MoreAssetsThanOneChunk_AreReadAndOrderedAsOneResult()
        {
            using var workspace = new ViewerTestWorkspace();
            var source = workspace.Workspace.AddMediaSource();

            var assets = new List<long>();
            var count = MediaAssetLocationReader.ChunkSize + 20;

            for (var index = 0; index < count; index++)
            {
                var hash = MatchingTestData.Hash(index + 1);
                assets.Add(workspace.Workspace.AddAssetWithCopy(source, hash, Day));
            }

            using var connection = workspace.OpenReadOnly();

            // Requested newest first, so the ordering cannot come from the request.
            assets.Reverse();

            var locations = new MediaAssetLocationReader().Read(connection, assets);

            Assert.Equal(count, locations.Count);

            Assert.Equal(
                locations.Select(location => location.MediaAssetID).Order(),
                locations.Select(location => location.MediaAssetID));
        }

        [Fact]
        public void Reading_ChangesNothingInTheWorkspace()
        {
            using var workspace = new ViewerTestWorkspace();
            var source = workspace.Workspace.AddMediaSource();
            var hash = MatchingTestData.Hash(1);
            var asset = workspace.Workspace.AddAssetWithCopy(source, hash, Day);

            var before = workspace.ScalarLongReadOnly(
                """
                SELECT (SELECT COUNT(*) FROM MediaFile)
                     + (SELECT COUNT(*) FROM MediaAsset)
                     + (SELECT COUNT(*) FROM MediaAssetFile)
                     + (SELECT COUNT(*) FROM Attachment);
                """);

            using (var connection = workspace.OpenReadOnly())
            {
                new MediaAssetLocationReader().Read(connection, [asset]);
            }

            var after = workspace.ScalarLongReadOnly(
                """
                SELECT (SELECT COUNT(*) FROM MediaFile)
                     + (SELECT COUNT(*) FROM MediaAsset)
                     + (SELECT COUNT(*) FROM MediaAssetFile)
                     + (SELECT COUNT(*) FROM Attachment);
                """);

            Assert.Equal(before, after);
        }
    }
}
