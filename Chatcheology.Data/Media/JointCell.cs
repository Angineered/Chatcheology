namespace Chatcheology.Data.Media
{
    /// <summary>One cell of a joint distribution.</summary>
    /// <remarks>
    /// <see cref="Row"/> and <see cref="Column"/> are fixed labels this census generates — band names
    /// and flag names — never text taken from the archive.
    /// <para>
    /// Only non-empty cells are emitted. A joint distribution over two banded quantities is mostly
    /// zeroes, and printing them all would bury the structure the cross-tab exists to show.
    /// </para>
    /// </remarks>
    public sealed class JointCell
    {
        /// <summary>The row band or flag.</summary>
        public required string Row { get; init; }

        /// <summary>The column band or flag.</summary>
        public required string Column { get; init; }

        /// <summary>Observations in this cell.</summary>
        public required int Count { get; init; }
    }
}
