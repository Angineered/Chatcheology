namespace Chatcheology.Mobile.Settings
{
    /// <summary>
    /// A "More" control (CH-78): a heading that opens or closes the items behind it, used for More settings and for the
    /// start screen's Import archive. Closed at first.
    /// </summary>
    /// <param name="Name">What it is called: "More settings", "More".</param>
    /// <param name="IsExpanded">Whether its items are shown.</param>
    public sealed record MoreControl(string Name, bool IsExpanded = false)
    {
        /// <summary>The open or closed mark, the same as the Saved screen's title headings.</summary>
        public string Mark => IsExpanded ? "▼︎" : "▶︎";

        /// <summary>What a screen reader hears: "More settings, collapsed".</summary>
        public string ScreenReaderText => Name + ", " + (IsExpanded ? "expanded" : "collapsed");

        /// <summary>The control after a tap.</summary>
        public MoreControl Toggled() => this with { IsExpanded = !IsExpanded };
    }
}
