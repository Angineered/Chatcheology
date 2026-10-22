namespace Chatcheology.Mobile.Conversation
{
    /// <summary>Which way more of the conversation is wanted, from the rows on screen.</summary>
    /// <param name="Earlier">Load the page before the loaded rows.</param>
    /// <param name="Later">Load the page after the loaded rows.</param>
    public readonly record struct PagingWant(bool Earlier, bool Later)
    {
        /// <summary>Whether anything is wanted.</summary>
        public bool Any => Earlier || Later;
    }

    /// <summary>
    /// When the list asks for another page: when the rows on screen come within <see cref="Margin"/>
    /// rows of either end of what is loaded, and there is more that way.
    /// </summary>
    /// <remarks>
    /// Both ways can be wanted at once: a list shorter than the screen is near both ends. The caller
    /// loads one page at a time and asks again after each, with the rows moved by what it added.
    /// </remarks>
    public static class PagingRule
    {
        /// <summary>How close to an end, in rows, the screen must come for the next page to load.</summary>
        public const int Margin = 15;

        /// <summary>What to load, given the first and last rows on screen (-1 when unknown).</summary>
        public static PagingWant Wanted(int firstVisible, int lastVisible, int rowCount, bool hasEarlier, bool hasLater)
        {
            if (firstVisible < 0 || lastVisible < 0 || rowCount == 0)
            {
                return default;
            }

            return new PagingWant(
                Earlier: hasEarlier && firstVisible <= Margin,
                Later: hasLater && lastVisible >= rowCount - 1 - Margin);
        }
    }
}
