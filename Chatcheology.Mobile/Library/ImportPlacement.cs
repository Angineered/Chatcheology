namespace Chatcheology.Mobile.Library
{
    /// <summary>Where the start screen puts "Import archive…" (CH-78).</summary>
    public enum ImportPlacement
    {
        /// <summary>
        /// The main button: no readable archive is installed (none at all on first install, or only ones that cannot be
        /// opened), or the list could not be read (recovery).
        /// </summary>
        Main,

        /// <summary>A smaller entry under a "More" control: at least one readable archive is installed.</summary>
        UnderMore,
    }

    /// <summary>The start screen's import placement rule (CH-78).</summary>
    public static class StartImport
    {
        /// <summary>
        /// Under More when a readable archive is installed; the main button when none is (no archive, or only unreadable
        /// ones), or when the installed archives could not be listed (<paramref name="opening"/> is <see langword="null"/>).
        /// </summary>
        public static ImportPlacement For(Opening? opening) =>
            opening is not null && opening.Archives.Any(archive => archive.IsReadable)
                ? ImportPlacement.UnderMore
                : ImportPlacement.Main;
    }
}
