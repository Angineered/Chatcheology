using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>What a Saved tab lists.</summary>
    public enum SavedTabKind
    {
        /// <summary>The starred messages and photos (CH-38).</summary>
        Starred,

        /// <summary>The personal collections' memories together (CH-56).</summary>
        Memories,

        /// <summary>One imported collection's memories (CH-56).</summary>
        Collection,
    }

    /// <summary>One tab of the Saved screen (CH-59).</summary>
    /// <param name="Id">
    /// <see cref="SavedTabs.StarredId"/>, or the Memories list group it shows (<see cref="MemoryList.PersonalGroup"/>,
    /// <see cref="MemoryList.CollectionGroup"/>).
    /// </param>
    /// <param name="Kind">What it lists.</param>
    /// <param name="Name">What the tab shows: "Starred", "Memories" or the collection's name.</param>
    public sealed record SavedTab(string Id, SavedTabKind Kind, string Name)
    {
        /// <summary>What the Release log says: <c>starred</c>, <c>memories</c> or <c>collection</c>, never a name.</summary>
        public string LogName => Kind switch
        {
            SavedTabKind.Starred => "starred",
            SavedTabKind.Memories => "memories",
            _ => "collection",
        };

        /// <summary>Whether the tab has the title search (the memory tabs).</summary>
        public bool HasSearch => Kind != SavedTabKind.Starred;

        /// <summary>The screen-reader name: "Starred tab", or "Starred tab, selected".</summary>
        public string ScreenReaderName(bool selected) => selected ? Name + " tab, selected" : Name + " tab";
    }

    /// <summary>
    /// What one Saved tab keeps while the screen is open: the first visible row (its key and place), the search
    /// text and, on a collection tab, Date or Title and which memory headings are open, so switching tabs or coming
    /// back from the details or the photo viewer finds it as it was. Never written to Settings.
    /// </summary>
    public sealed class SavedTabState
    {
        private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

        /// <summary>The search text (memory tabs only); empty when none.</summary>
        public string Search { get; set; } = string.Empty;

        /// <summary>How the tab lists its memories (collection tabs only; Date otherwise).</summary>
        public MemoryListGrouping GroupBy { get; private set; } = MemoryListGrouping.Date;

        /// <summary>
        /// The exact titles whose headings are open when grouped by title (ordinal); none at first. Kept in memory
        /// only while the screen is open, never logged or written to Settings.
        /// </summary>
        public IReadOnlySet<string> Expanded => _expanded;

        /// <summary>
        /// Lists the tab by <paramref name="grouping"/>; false when it already is. A change starts the list from the top
        /// (the rows are different); the open headings are kept for when Title is chosen again.
        /// </summary>
        public bool SetGroupBy(MemoryListGrouping grouping)
        {
            if (GroupBy == grouping)
            {
                return false;
            }

            GroupBy = grouping;
            Remember(null, 0);

            return true;
        }

        /// <summary>Opens a closed heading or closes an open one; true when it is now open.</summary>
        public bool Toggle(string title)
        {
            ArgumentNullException.ThrowIfNull(title);

            if (_expanded.Remove(title))
            {
                return false;
            }

            _expanded.Add(title);

            return true;
        }

        /// <summary>The key of the first visible row (<see cref="SavedTabs.KeyOf(StarredEntry)"/>), or <see langword="null"/> at the top.</summary>
        public string? AnchorKey { get; private set; }

        /// <summary>Where the first visible row was, for when its row is gone (an unstarred entry).</summary>
        public int AnchorIndex { get; private set; }

        /// <summary>Keeps <paramref name="key"/> at <paramref name="index"/> as the first visible row.</summary>
        public void Remember(string? key, int index)
        {
            AnchorKey = key;
            AnchorIndex = Math.Max(0, index);
        }

        /// <summary>
        /// Where to scroll in <paramref name="keys"/> (the rows now, by key): the remembered row when it is still there;
        /// otherwise the same place, kept inside the list (best effort, after an unstar); -1 for an empty list.
        /// </summary>
        public int RestoreIndex(IReadOnlyList<string> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);

            if (keys.Count == 0)
            {
                return -1;
            }

            if (AnchorKey is { } key)
            {
                for (var index = 0; index < keys.Count; index++)
                {
                    if (keys[index] == key)
                    {
                        return index;
                    }
                }
            }

            return Math.Min(AnchorIndex, keys.Count - 1);
        }
    }

    /// <summary>
    /// The Saved screen's tabs (CH-59): Starred | Memories | one per imported collection, which one is chosen, and
    /// each tab's state while the screen is open. Built from the session, which keeps the last chosen tab for as
    /// long as the archive is open.
    /// </summary>
    /// <remarks>
    /// <para>Starred is there whenever the archive has messages (also with no stars); Memories always; each imported
    /// collection always, by identifier, empty or not. Personal collections stay together under Memories.</para>
    /// <para>The screen opens on the tab last chosen for this open archive, or the first tab (Starred, or Memories
    /// when there are no messages).</para>
    /// </remarks>
    public sealed class SavedTabs
    {
        /// <summary>The Starred tab's identifier.</summary>
        public const string StarredId = "starred";

        /// <summary>The Starred tab's empty line.</summary>
        public const string NoStarsText = "Star a message or photo to keep it here.";

        /// <summary>The Starred tab's line when the stars could not be read.</summary>
        public const string StarsNotReadText = "The starred items could not be read.";

        private readonly Dictionary<string, SavedTabState> _states = new(StringComparer.Ordinal);
        private readonly Action<string>? _chosen;

        /// <summary>
        /// The tabs <paramref name="tabs"/>, opening on <paramref name="lastUsed"/> when it is one of them (otherwise the
        /// first); <paramref name="chosen"/> hears every tab chosen, the first included, to remember it.
        /// </summary>
        public SavedTabs(IReadOnlyList<SavedTab> tabs, string? lastUsed = null, Action<string>? chosen = null)
        {
            ArgumentNullException.ThrowIfNull(tabs);

            if (tabs.Count == 0)
            {
                throw new ArgumentException("The Saved screen has at least the Memories tab.", nameof(tabs));
            }

            Tabs = tabs;
            _chosen = chosen;
            Selected = tabs.FirstOrDefault(tab => tab.Id == lastUsed) ?? tabs[0];
            _chosen?.Invoke(Selected.Id);
        }

        /// <summary>The tabs, in order.</summary>
        public IReadOnlyList<SavedTab> Tabs { get; }

        /// <summary>The chosen tab.</summary>
        public SavedTab Selected { get; private set; }

        /// <summary>Whether the bookmark icon is shown: the archive has messages (something to star) or memories.</summary>
        public static bool IsShown(bool hasMessages, bool hasMemories) => hasMessages || hasMemories;

        /// <summary>The tabs for an archive with or without messages and with <paramref name="collections"/>.</summary>
        public static IReadOnlyList<SavedTab> TabsFor(bool hasMessages, IEnumerable<ArchiveCollection> collections)
        {
            ArgumentNullException.ThrowIfNull(collections);

            var tabs = new List<SavedTab>();

            if (hasMessages)
            {
                tabs.Add(new SavedTab(StarredId, SavedTabKind.Starred, "Starred"));
            }

            tabs.Add(new SavedTab(MemoryList.PersonalGroup, SavedTabKind.Memories, "Memories"));

            tabs.AddRange(collections
                .Where(c => c.Kind == ArchiveMemoryVocabulary.KindImported)
                .OrderBy(c => c.CollectionID)
                .Select(c => new SavedTab(MemoryList.CollectionGroup(c.CollectionID), SavedTabKind.Collection, c.Name)));

            return tabs;
        }

        /// <summary>Chooses the tab <paramref name="id"/>; false when it is already chosen or is not a tab.</summary>
        public bool Select(string id)
        {
            if (Selected.Id == id || Tabs.FirstOrDefault(tab => tab.Id == id) is not { } tab)
            {
                return false;
            }

            Selected = tab;
            _chosen?.Invoke(tab.Id);

            return true;
        }

        /// <summary>What <paramref name="tab"/> keeps while the screen is open.</summary>
        public SavedTabState StateOf(SavedTab tab)
        {
            ArgumentNullException.ThrowIfNull(tab);

            if (!_states.TryGetValue(tab.Id, out var state))
            {
                state = new SavedTabState();
                _states[tab.Id] = state;
            }

            return state;
        }

        /// <summary>A starred entry's key: <c>m:&lt;sequence&gt;</c> for a message, <c>p:&lt;sequence&gt;:&lt;attachment&gt;</c> for a photo.</summary>
        public static string KeyOf(StarredEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            var sequence = entry.SequenceNumber.ToString(CultureInfo.InvariantCulture);

            return entry.IsPhoto && entry.Thumbnail is { } photo
                ? "p:" + sequence + ":" + photo.AttachmentID.ToString(CultureInfo.InvariantCulture)
                : "m:" + sequence;
        }

        /// <summary>
        /// A Memories list row's key: <c>y:&lt;year&gt;</c> for a year heading, <c>t:&lt;title&gt;</c> for a title's
        /// heading (grouped by title; in memory only, never logged), <c>e:&lt;entry&gt;[:&lt;period&gt;]</c> for a memory.
        /// </summary>
        public static string KeyOf(MemoryListRow row) => row switch
        {
            MemoryListYearRow year => "y:" + year.Text,
            MemoryListTitleRow title => "t:" + title.Title,
            MemoryListItem item => "e:" + item.Key.EntryID.ToString(CultureInfo.InvariantCulture)
                + (item.Key.PeriodIndex is { } period ? ":" + period.ToString(CultureInfo.InvariantCulture) : string.Empty),
            _ => throw new ArgumentException("Unknown row.", nameof(row)),
        };
    }
}
