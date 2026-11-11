namespace Chatcheology.Mobile.Library
{
    /// <summary>An archive the start screen opens by itself on the app's first start.</summary>
    /// <param name="Archive">The archive to open; always readable.</param>
    /// <param name="ShowOnThisDay">Whether its conversation opens with the On this day card (a noted-date notification).</param>
    public sealed record AutoOpen(InstalledArchive Archive, bool ShowOnThisDay);

    /// <summary>
    /// Whether the app's first start opens an archive by itself (CH-78). While it does, the start screen shows only a
    /// loading spinner, so neither "Import archive…" nor the archive list flashes before the conversation.
    /// </summary>
    public static class StartAutoOpen
    {
        /// <summary>
        /// The archive the first start opens: the one a tapped noted-date notification named
        /// (<paramref name="pendingInstallId"/>), with its On this day card, when it is installed and readable; otherwise
        /// the only installed archive when it is readable; otherwise <see langword="null"/> (the normal start screen).
        /// Also <see langword="null"/> when the installed archives could not be listed (<paramref name="opening"/> is
        /// <see langword="null"/>).
        /// </summary>
        public static AutoOpen? Choose(Opening? opening, string? pendingInstallId)
        {
            if (opening is null)
            {
                return null;
            }

            if (pendingInstallId is not null
                && opening.Archives.FirstOrDefault(archive => archive.InstallId == pendingInstallId) is { IsReadable: true } notified)
            {
                return new AutoOpen(notified, ShowOnThisDay: true);
            }

            return opening is { Screen: StartScreen.OpenConversation, Archive: { IsReadable: true } only }
                ? new AutoOpen(only, ShowOnThisDay: false)
                : null;
        }
    }
}
