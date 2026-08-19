namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// How a candidate's supporting physical copies sit against the direction of the message being
    /// analysed.
    /// </summary>
    /// <remarks>
    /// These names describe compatibility with folder evidence, not proof of who sent or received
    /// the payload. <c>MediaFile.IsSent = 1</c> means a copy was found beneath a <c>Sent</c>
    /// directory; <c>0</c> means its source has <c>Sent</c> structure and this copy was not beneath
    /// it. That second fact is weaker than "received", and nothing here promotes it.
    /// <para>
    /// Every value is judged from the supporting copies of one attachment/candidate relationship —
    /// the copies whose <c>FileDate</c> put the asset into that candidate set — never from copies
    /// of the same payload on unrelated dates.
    /// </para>
    /// <para>
    /// None of these is a confidence level, and they do not rank against each other.
    /// <see cref="ContradictoryOnly"/> in particular is not an exclusion: a duplicate payload can
    /// survive in another chat's or another device's history while the copy that belonged to this
    /// conversation is gone.
    /// </para>
    /// </remarks>
    public enum DirectionCompatibility
    {
        /// <summary>
        /// The message direction is unknown, or every supporting copy records no folder direction
        /// at all. There is nothing to agree or disagree with.
        /// </summary>
        Unknown,

        /// <summary>
        /// At least one supporting copy agrees with the message direction and none disagrees.
        /// </summary>
        Compatible,

        /// <summary>
        /// Supporting copies disagree among themselves: at least one agrees and at least one does
        /// not. Both are facts about surviving copies; neither outvotes the other.
        /// </summary>
        Mixed,

        /// <summary>
        /// Supporting copies carry folder direction and none of it agrees with the message
        /// direction.
        /// </summary>
        ContradictoryOnly,
    }
}
