namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// How one physical copy's recovered token position sits against the monotone
    /// within-direction order hypothesis, for the attachment that copy is offered to.
    /// </summary>
    /// <remarks>
    /// Scoped to one <c>MediaFile</c>, never to a payload and never to an attachment. Two supporting
    /// copies of one payload on one date legitimately carry different tokens, land on different
    /// positions and therefore read differently; a single asset-level verdict would be a property no
    /// file ever had.
    /// <para>
    /// The hypothesis itself was never confirmed. Stage B2A terminated with insufficient power and
    /// Stage B2C-1 supports it at population level only, so every value here is a statement about a
    /// consequence of the hypothesis rather than a confirmation of it.
    /// </para>
    /// <para>
    /// None of these is a confidence level and none is an exclusion. At ranking time
    /// <see cref="NotAssessable"/> is dropped rather than counted against a candidate,
    /// <see cref="CompatibleWithAmbiguity"/> and <see cref="UniquelyConstrained"/> carry
    /// <em>identical</em> strength, and <see cref="NoCompatibleArrangement"/> is weaker without ever
    /// removing anything.
    /// </para>
    /// </remarks>
    public enum OrderConsistencyAnnotation
    {
        /// <summary>
        /// The question could not be asked: the occurrence's group is out of scope for this
        /// computation, its name yields no supported token, or its
        /// <c>(FileDate, SequenceToken)</c> key collides across payloads.
        /// </summary>
        /// <remarks>
        /// Explicitly not a negative reading. An infeasible group — no compatible candidate asset, no
        /// supported token position, or fewer positions than messages — reads this way too, because a
        /// group-level shortfall whose cause the evidence cannot separate must not become
        /// per-candidate negative evidence.
        /// </remarks>
        NotAssessable,

        /// <summary>
        /// The question was asked and no complete increasing arrangement of the group places this
        /// attachment's rank at this occurrence's token position.
        /// </summary>
        /// <remarks>
        /// The one genuinely weaker reading, and still never an exclusion.
        /// </remarks>
        NoCompatibleArrangement,

        /// <summary>
        /// At least two complete increasing arrangements place this attachment's rank at this
        /// occurrence's token position.
        /// </summary>
        CompatibleWithAmbiguity,

        /// <summary>
        /// Exactly one complete increasing arrangement places this attachment's rank at this
        /// occurrence's token position.
        /// </summary>
        /// <remarks>
        /// A statement about token positions, never about identity, and it earns no ranking bonus
        /// whatsoever over <see cref="CompatibleWithAmbiguity"/>. Uniqueness here is commonly forced
        /// by the group's shape rather than earned — a single-member group, or the first rank at its
        /// highest admissible position — and treating a forced position as a match would be reading
        /// arithmetic as evidence.
        /// </remarks>
        UniquelyConstrained,
    }
}
