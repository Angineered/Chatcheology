using Chatcheology.Data.Matching;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The unit the licensed within-direction order model is built on: one calendar date and one
    /// message direction.
    /// </summary>
    /// <remarks>
    /// Exactly the key <c>WithinDirectionAssignmentCensusService.GroupCollector</c> uses. It is the
    /// unit of analysis because the frozen matching analysis derives an attachment's candidates from
    /// its message's date and direction alone, so every member of one group necessarily exposes the
    /// identical compatible candidate set — an invariant this stage asserts rather than assumes.
    /// </remarks>
    /// <param name="Date">The message's local calendar date.</param>
    /// <param name="Direction">
    /// The message's direction. <see cref="MessageDirection.Unknown"/> never forms a group: an
    /// attachment with no known direction is out of scope for the order model altogether.
    /// </param>
    internal readonly record struct Phase7GroupKey(DateOnly Date, MessageDirection Direction);
}
