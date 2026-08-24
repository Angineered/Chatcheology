namespace Chatcheology.Data.Sequence
{
    /// <summary>Which neighbouring calendar date an adjacent-date control pairs a message pattern with.</summary>
    /// <remarks>
    /// The immediately previous or next calendar date, never the nearest date that happens to hold
    /// tokens: skipping a gap would quietly turn a wrong-date control into a comparison with whatever
    /// activity was closest, which is a different question.
    /// <para>
    /// The two are always reported separately and never averaged. A systematic difference between them is
    /// itself informative about drift or archive structure rather than noise to be cancelled.
    /// </para>
    /// </remarks>
    public enum DirectionSequenceAdjacentOffset
    {
        /// <summary>The immediately previous calendar date.</summary>
        PreviousDay = -1,

        /// <summary>The immediately next calendar date.</summary>
        NextDay = 1,
    }
}
