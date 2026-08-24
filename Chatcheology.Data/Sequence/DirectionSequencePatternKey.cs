namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// One message pattern as a value, so two equal patterns are one cache key.
    /// </summary>
    /// <remarks>
    /// Value semantics are the whole point. The patterns arrive as arrays, and array equality is
    /// reference equality, so keying a cache on the array itself would miss every hit and quietly turn a
    /// memo into a leak. Two words hold the archive's longest pattern — its maximum is sixty-six symbols —
    /// and the length is carried so that a shorter pattern of zeros cannot collide with a longer one.
    /// </remarks>
    internal readonly record struct DirectionSequencePatternKey(int Length, ulong Low, ulong High)
    {
        /// <summary>The width one word covers.</summary>
        private const int WordWidth = 64;

        /// <summary>The longest pattern two words can hold.</summary>
        internal const int MaximumLength = 2 * WordWidth;

        /// <summary>
        /// The key for one pattern.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The pattern is longer than two words hold. Longer patterns are not a cache miss to absorb —
        /// they would collide — so a workspace holding one must stop rather than be measured against a
        /// key that cannot describe it.
        /// </exception>
        internal static DirectionSequencePatternKey Of(bool[] pattern)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(pattern.Length, MaximumLength);

            var low = 0UL;
            var high = 0UL;

            for (var index = 0; index < pattern.Length; index++)
            {
                if (!pattern[index])
                {
                    continue;
                }

                if (index < WordWidth)
                {
                    low |= 1UL << index;
                }
                else
                {
                    high |= 1UL << (index - WordWidth);
                }
            }

            return new DirectionSequencePatternKey(pattern.Length, low, high);
        }
    }
}
