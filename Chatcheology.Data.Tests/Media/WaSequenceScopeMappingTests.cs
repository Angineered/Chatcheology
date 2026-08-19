using Chatcheology.Data.Media;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// Tests the one input the sequence scope census cannot derive for itself: which acquisition
    /// sources belong to the same numbering authority.
    /// </summary>
    /// <remarks>
    /// A <c>MediaSourceID</c> is a store someone copied, not a device that wrote a counter. Nothing in
    /// the workspace records which stores came from the same handset, so the census takes that from the
    /// caller and refuses anything it cannot apply — and refuses to invent it, because a default would
    /// reinstate the assumption the census exists to test.
    /// </remarks>
    public class WaSequenceScopeMappingTests
    {
        private const string HashA = "AAAA000000000000000000000000000000000000000000000000000000000001";
        private const string HashB = "BBBB000000000000000000000000000000000000000000000000000000000002";

        [Fact]
        public void AMappingNamingEverySourceOnce_IsAccepted()
        {
            using var workspace = new NameCensusTestWorkspace();
            var first = workspace.AddMediaSource();
            var second = workspace.AddMediaSource();
            workspace.AddAssetWithFile(first, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(second, HashB, "IMG-20260724-WA0005.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (first, 1), (second, 2));

            Assert.Equal(2, census.Reconciliation.MediaSourceCount);
            Assert.Equal(2, census.Reconciliation.DeviceGroupCount);
        }

        /// <remarks>
        /// The absence of a default is the point. A caller who genuinely wants one group per source can
        /// say so in one line; a caller who forgot would otherwise be handed a census that had quietly
        /// assumed every store numbered its own media.
        /// </remarks>
        [Fact]
        public void NoAssignments_AreRefusedRatherThanDefaultedToOneGroupPerSource()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var exception = Assert.Throws<InvalidOperationException>(
                () => new WaSequenceScopeCensusService().Analyse(new WaSequenceScopeCensusRequest
                {
                    DatabasePath = workspace.DatabasePath,
                    DeviceGroups = [],
                }));

            Assert.Contains("will not assume one group per source", exception.Message);
        }

        [Fact]
        public void ASourceAssignedTwice_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1), (sourceID, 2)));

            Assert.Contains("more than once", exception.Message);
        }

        [Fact]
        public void AnAssignmentNamingASourceTheWorkspaceDoesNotHold_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1), (sourceID + 99, 2)));

            Assert.Contains("does not contain", exception.Message);
        }

        [Fact]
        public void ASourceLeftOutOfTheMapping_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var first = workspace.AddMediaSource();
            var second = workspace.AddMediaSource();
            workspace.AddAssetWithFile(first, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(second, HashB, "IMG-20260724-WA0005.jpg");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (first, 1)));

            Assert.Contains("assigned to no device group", exception.Message);
        }

        /// <remarks>
        /// The shape the real archive has: two stores copied from one handset, and a third from another.
        /// Grouping the first two makes the device level coarser than the source level, which is exactly
        /// the difference the ladder exists to measure — and the difference a per-source key would have
        /// hidden by splitting one handset's numbering in two.
        /// </remarks>
        [Fact]
        public void TwoSourcesInOneGroup_MakeTheDeviceLevelCoarserThanTheSourceLevel()
        {
            using var workspace = new NameCensusTestWorkspace();
            var legacy = workspace.AddMediaSource();
            var current = workspace.AddMediaSource();
            var other = workspace.AddMediaSource();

            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(legacy, asset, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddMediaFile(current, asset, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddMediaFile(other, asset, HashA, "IMG-20260724-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(
                workspace, (legacy, 1), (current, 1), (other, 2));

            Assert.Equal(2, census.Reconciliation.DeviceGroupCount);
            Assert.Equal(2, Level(census, ScopeLevel.DeviceGroupDateToken).KeyCount);
            Assert.Equal(3, Level(census, ScopeLevel.SourceDateToken).KeyCount);
        }

        /// <remarks>
        /// The same three sources treated as three authorities. Nothing refuses it — the census measures
        /// the scope it is given rather than arguing with it — but the device level then stops being a
        /// distinct measurement, which is what makes the explicit mapping worth requiring.
        /// </remarks>
        [Fact]
        public void OneGroupPerSource_IsAcceptedWhenTheCallerSaysSo()
        {
            using var workspace = new NameCensusTestWorkspace();
            var first = workspace.AddMediaSource();
            var second = workspace.AddMediaSource();

            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(first, asset, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddMediaFile(second, asset, HashA, "IMG-20260724-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (first, 1), (second, 2));

            Assert.Equal(2, census.Reconciliation.DeviceGroupCount);
            Assert.Equal(
                Level(census, ScopeLevel.SourceDateToken).KeyCount,
                Level(census, ScopeLevel.DeviceGroupDateToken).KeyCount);
        }

        private static ScopeKeyUniqueness Level(WaSequenceScopeCensus census, ScopeLevel level) =>
            census.KeyUniqueness.Single(uniqueness => uniqueness.Level == level);
    }
}
