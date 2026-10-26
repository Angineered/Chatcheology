namespace Chatcheology.Core.Models
{
    /// <summary>
    /// The timestamp layout a WhatsApp Android export writes at the start of every message header.
    /// </summary>
    /// <remarks>
    /// One export uses one layout throughout. The three layouts are told apart by their first
    /// characters alone — time first, day first, or a four-digit year first — so detecting one
    /// never depends on a guess about what a number means.
    /// <para>
    /// The letters in the design notes (A, B and C) are recorded on each member so the two
    /// vocabularies can be read against each other.
    /// </para>
    /// </remarks>
    public enum WhatsAppHeaderShape
    {
        /// <summary>
        /// Shape A, <c>HH:mm, d MMM yyyy - </c>: the time first, then a one- or two-digit day, an
        /// English three-letter month abbreviation and a four-digit year. Older exports.
        /// </summary>
        TimeFirst,

        /// <summary>
        /// Shape B, <c>dd/MM/yyyy, HH:mm - </c>: a two-digit day, two-digit month and four-digit
        /// year, then the time. Older exports.
        /// </summary>
        DayFirst,

        /// <summary>
        /// Shape C, <c>yyyy/MM/dd, HH:mm - </c>: the pinned layout of the workspace export.
        /// </summary>
        YearFirst,
    }
}
