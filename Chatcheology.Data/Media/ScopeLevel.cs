namespace Chatcheology.Data.Media
{
    /// <summary>
    /// One scope the WA sequence census measures behaviour at.
    /// </summary>
    /// <remarks>
    /// The first four values are the <em>key</em> levels of the scope ladder, each refining the one
    /// before it. The last three are the <em>group</em> scopes, which drop the token and describe the
    /// population a range or a continuity figure belongs to.
    /// <para>
    /// One enum rather than two, because both families are the same idea — how much of the recovered
    /// name is being conditioned on — and because a single declaration order gives every report a
    /// deterministic ordering without a second convention to keep in step.
    /// </para>
    /// <para>
    /// No value here is "the namespace". Which added component actually reduces ambiguity is the
    /// measurement, not an assumption built into this type.
    /// </para>
    /// </remarks>
    public enum ScopeLevel
    {
        /// <summary>The token alone.</summary>
        Token = 0,

        /// <summary>Calendar date and token.</summary>
        DateToken = 1,

        /// <summary>Device group, calendar date and token.</summary>
        DeviceGroupDateToken = 2,

        /// <summary>Acquisition source, calendar date and token.</summary>
        SourceDateToken = 3,

        /// <summary>Calendar date alone, pooled across every source.</summary>
        Date = 4,

        /// <summary>Device group and calendar date.</summary>
        DeviceGroupDate = 5,

        /// <summary>Acquisition source and calendar date.</summary>
        SourceDate = 6,
    }
}
