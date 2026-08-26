namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Where one Phase 7 evidence run got to.
    /// </summary>
    /// <remarks>
    /// A lifecycle, not an outcome. Only <see cref="Completed"/> may ever be read as evidence:
    /// <see cref="Running"/> and <see cref="Failed"/> are both permanently non-current, and no
    /// reader may fall back to one of them because no completed run exists.
    /// <para>
    /// There is no timeout, lease or sweep that turns a <see cref="Running"/> run into a
    /// <see cref="Failed"/> one. A process that died holding a run leaves it <see cref="Running"/>
    /// for ever, which is inert and honest; a rule that flipped somebody else's run would be a
    /// mechanism that can be wrong.
    /// </para>
    /// </remarks>
    public enum Phase7RunStatus
    {
        /// <summary>
        /// The run began and has not published. Never current, whether it is still executing or was
        /// abandoned by a process that stopped.
        /// </summary>
        Running,

        /// <summary>
        /// The run published its evidence atomically. The only status a reader may draw evidence
        /// from.
        /// </summary>
        Completed,

        /// <summary>
        /// The run began and did not publish. Retained for diagnosis; it holds no evidence row at
        /// all, by construction rather than by cleanup.
        /// </summary>
        Failed,
    }
}
