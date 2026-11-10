namespace Chatcheology.Mobile.Library
{
    /// <summary>Where the start screen puts "Import archive…" (CH-78).</summary>
    public enum ImportPlacement
    {
        /// <summary>The main button: no archive is installed (first install), or the list could not be read (recovery).</summary>
        Main,

        /// <summary>A smaller entry under a "More" control: at least one archive is installed.</summary>
        UnderMore,
    }

    /// <summary>The start screen's import placement rule (CH-78).</summary>
    public static class StartImport
    {
        /// <summary>
        /// Under More when any archive is installed (readable or not); the main button when none is, or when the
        /// installed archives could not be listed (<paramref name="opening"/> is <see langword="null"/>).
        /// </summary>
        public static ImportPlacement For(Opening? opening) =>
            opening is { Archives.Count: > 0 } ? ImportPlacement.UnderMore : ImportPlacement.Main;
    }
}
