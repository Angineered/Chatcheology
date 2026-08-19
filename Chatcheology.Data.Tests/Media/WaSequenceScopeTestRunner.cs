using Chatcheology.Data.Media;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// Runs the sequence scope census over a synthetic workspace with an explicit device grouping.
    /// </summary>
    /// <remarks>
    /// The grouping is spelled out at every call site rather than defaulted here. Which sources share a
    /// numbering authority is the variable these tests are about, so a helper that guessed it would hide
    /// the thing being tested.
    /// </remarks>
    internal static class WaSequenceScopeTestRunner
    {
        internal static WaSequenceScopeCensus Analyse(
            NameCensusTestWorkspace workspace,
            params (long MediaSourceID, long DeviceGroupID)[] deviceGroups) =>
            Analyse(workspace, CancellationToken.None, deviceGroups);

        internal static WaSequenceScopeCensus Analyse(
            NameCensusTestWorkspace workspace,
            CancellationToken cancellationToken,
            params (long MediaSourceID, long DeviceGroupID)[] deviceGroups) =>
            new WaSequenceScopeCensusService().Analyse(
                new WaSequenceScopeCensusRequest
                {
                    DatabasePath = workspace.DatabasePath,
                    DeviceGroups = [.. deviceGroups.Select(group => new DeviceGroupAssignment
                    {
                        MediaSourceID = group.MediaSourceID,
                        DeviceGroupID = group.DeviceGroupID,
                    })],
                },
                cancellationToken);
    }
}
