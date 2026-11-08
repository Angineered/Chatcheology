namespace Chatcheology.Mobile.Library
{
    /// <summary>One installed archive, as the start screen lists it.</summary>
    /// <param name="InstallId">The folder name under <c>archives/</c> (not the format's archive id).</param>
    /// <param name="Folder">The archive's folder in the app's private storage.</param>
    /// <param name="ContentsSha256">
    /// The archive's identity (SHA-256 of its <c>CONTENTS.sha256</c>), or <see langword="null"/> when that
    /// file cannot be read.
    /// </param>
    /// <param name="ConversationTitle">The conversation's title, or <see langword="null"/> when unreadable.</param>
    /// <param name="CreatedUtc">When the archive was made, or <see langword="null"/> when unreadable.</param>
    /// <param name="MessageCount">How many messages it holds.</param>
    /// <param name="LastOpenedUtc">When it was last opened in the app, or <see langword="null"/> when never.</param>
    /// <param name="IsReadable">Whether its database opens and its identity is known.</param>
    public sealed record InstalledArchive(
        string InstallId,
        string Folder,
        string? ContentsSha256,
        string? ConversationTitle,
        DateTime? CreatedUtc,
        int MessageCount,
        DateTime? LastOpenedUtc,
        bool IsReadable)
    {
        /// <summary>Whether the archive holds no messages: it opens to an empty state, without date picker or search.</summary>
        public bool IsEmpty => MessageCount == 0;
    }

    /// <summary>What the app shows when it starts.</summary>
    public enum StartScreen
    {
        /// <summary>No archive: the start screen with "Import archive…".</summary>
        NoArchive,

        /// <summary>Exactly one: its conversation, directly.</summary>
        OpenConversation,

        /// <summary>Several: the list, last opened first.</summary>
        ChooseArchive,
    }

    /// <summary>The opening decision.</summary>
    /// <param name="Screen">Which screen to show.</param>
    /// <param name="Archive">The archive to open, for <see cref="StartScreen.OpenConversation"/>.</param>
    /// <param name="Archives">Every installed archive in list order.</param>
    public sealed record Opening(StartScreen Screen, InstalledArchive? Archive, IReadOnlyList<InstalledArchive> Archives);

    /// <summary>
    /// The opening rule (design "Opening"): none, a start screen; exactly one, straight into its
    /// conversation; several, a list with the last opened first.
    /// </summary>
    public static class OpeningRule
    {
        /// <summary>Decides the start screen for the installed archives.</summary>
        public static Opening Decide(IReadOnlyList<InstalledArchive> installed)
        {
            ArgumentNullException.ThrowIfNull(installed);

            var ordered = Order(installed);

            return ordered.Count switch
            {
                0 => new Opening(StartScreen.NoArchive, null, ordered),
                1 => new Opening(StartScreen.OpenConversation, ordered[0], ordered),
                _ => new Opening(StartScreen.ChooseArchive, null, ordered),
            };
        }

        /// <summary>
        /// List order: last opened first; archives never opened after them, newest created first; then
        /// by folder name, so the order is stable.
        /// </summary>
        public static IReadOnlyList<InstalledArchive> Order(IEnumerable<InstalledArchive> installed) =>
            installed
                .OrderBy(archive => archive.LastOpenedUtc is null)
                .ThenByDescending(archive => archive.LastOpenedUtc)
                .ThenByDescending(archive => archive.CreatedUtc)
                .ThenBy(archive => archive.InstallId, StringComparer.Ordinal)
                .ToList();
    }
}
