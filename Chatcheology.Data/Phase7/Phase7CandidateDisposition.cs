namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// What a human recorded about one specific candidate row of one specific run.
    /// </summary>
    /// <remarks>
    /// One value, and it is a statement about an individual candidate on an individual basis. There is
    /// no value meaning "ranked badly", because a ranking disagreement is not a disproof.
    /// </remarks>
    public enum Phase7CandidateDisposition
    {
        /// <summary>
        /// This candidate has been ruled out on a stated basis of its own, independent of ranking.
        /// </summary>
        IndependentlyDisproven,
    }
}
