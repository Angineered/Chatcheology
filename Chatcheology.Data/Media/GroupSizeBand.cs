namespace Chatcheology.Data.Media
{
    /// <summary>
    /// How many distinct sequence tokens a group holds, banded.
    /// </summary>
    /// <remarks>
    /// Continuity and starting-value results are reported per band and never only pooled. A group
    /// holding one or two tokens is contiguous by arithmetic rather than by anything the numbering did,
    /// and those groups are numerous enough to carry an unbanded headline on their own.
    /// <para>
    /// The bands are contiguous and cover every count from one upwards, so the members always sum to
    /// the groups described. A group with no tokens does not exist, so there is no zero band.
    /// </para>
    /// </remarks>
    public enum GroupSizeBand
    {
        /// <summary>Exactly one distinct token.</summary>
        One = 0,

        /// <summary>Exactly two.</summary>
        Two = 1,

        /// <summary>Three to five.</summary>
        ThreeToFive = 2,

        /// <summary>Six to ten.</summary>
        SixToTen = 3,

        /// <summary>Eleven to twenty-five.</summary>
        ElevenToTwentyFive = 4,

        /// <summary>More than twenty-five.</summary>
        MoreThanTwentyFive = 5,
    }
}
