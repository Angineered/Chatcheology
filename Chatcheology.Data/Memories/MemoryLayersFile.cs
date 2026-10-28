using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;

namespace Chatcheology.Data.Memories
{
    /// <summary>One entry of the memories file for the desktop's memory layers (CH-42 step 5).</summary>
    /// <param name="Entry">The entry.</param>
    /// <param name="Periods">Its periods, each item naming a conversation participant.</param>
    /// <param name="ImagePath">The image file in the memories media folder, when it is there.</param>
    /// <param name="Links">Its links, a message by its workspace sequence number.</param>
    public sealed record MemoryLayerEntry(
        ArchiveEntry Entry, IReadOnlyList<ArchiveEntryPeriod> Periods, string? ImagePath, IReadOnlyList<ArchiveEntryLink> Links)
    {
        /// <summary>
        /// The extra photos after the cover, in order (CH-46; none before format 3): each file in the
        /// memories media folder when it is there, and its caption.
        /// </summary>
        public IReadOnlyList<(string? Path, string? Caption)> ExtraPhotos { get; init; } = [];
    }

    /// <summary>
    /// The memories file beside a workspace, read for the desktop's memory layers (CH-42 step 5): the
    /// same reading the archive export does, on one read-only connection, with each item's Local or Other
    /// role mapped onto the selected conversation's two participants.
    /// </summary>
    /// <remarks>
    /// <para><strong>Read only.</strong> The file is opened through SQLite's own <c>Mode=ReadOnly</c>; no
    /// file means no layers. Formats 1 to 3 are read (format 1 has no item definitions, formats 1 and 2
    /// no extra photos).</para>
    /// <para><strong>Roles need two named participants.</strong> Without a local participant and exactly
    /// one other, a collection whose entries have items cannot name whose items they are, so it is listed
    /// in <see cref="Unnamed"/> and its entries are left out, never guessed. Personal collections have no
    /// items and are unaffected.</para>
    /// <para>Message links keep the workspace sequence number: the desktop shows the workspace
    /// conversation itself, so nothing is re-mapped.</para>
    /// </remarks>
    public sealed class MemoryLayersFile
    {
        /// <summary>The collections whose entries are given, ordered by identifier.</summary>
        public required IReadOnlyList<ArchiveCollection> Collections { get; init; }

        /// <summary>The collections left out because their items could not be named (see remarks).</summary>
        public required IReadOnlyList<ArchiveCollection> Unnamed { get; init; }

        /// <summary>Every entry of <see cref="Collections"/>, collection by collection, by identifier.</summary>
        public required IReadOnlyList<MemoryLayerEntry> Entries { get; init; }

        /// <summary>The item definitions some item uses (none in a format 1 file).</summary>
        public required IReadOnlyList<ArchiveItemDefinition> Definitions { get; init; }

        /// <summary>A definition's icon file when it is there; otherwise <see langword="null"/>.</summary>
        public required Func<ArchiveItemDefinition, string?> IconPath { get; init; }

        /// <summary>
        /// Reads the memories file beside <paramref name="workspacePath"/>; <see langword="null"/> when
        /// there is none.
        /// </summary>
        /// <param name="workspacePath">The open workspace.</param>
        /// <param name="localParticipantID">The participant the Local role names, or <see langword="null"/>.</param>
        /// <param name="otherParticipantID">The participant the Other role names, or <see langword="null"/>.</param>
        /// <param name="cancellationToken">Stops a long read.</param>
        /// <exception cref="InvalidOperationException">The file is there but could not be read. The message is a fixed sentence.</exception>
        public static MemoryLayersFile? ReadBeside(
            string workspacePath, long? localParticipantID, long? otherParticipantID, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

            MemoriesArchiveSource? source;

            try
            {
                source = MemoriesArchiveSource.ReadBeside(workspacePath, cancellationToken);
            }
            catch (ArchiveExportRefusedException exception)
            {
                throw new InvalidOperationException(
                    "The memories file beside the workspace could not be read, so no memory layers are shown.", exception);
            }

            if (source is null)
            {
                return null;
            }

            var canName = localParticipantID is not null && otherParticipantID is not null
                          && localParticipantID != otherParticipantID;

            HashSet<long> unnamed = canName
                ? []
                : source.Entries
                    .Where(e => e.Periods.Any(p => p.Items.Count > 0))
                    .Select(e => e.Entry.CollectionID)
                    .ToHashSet();

            if (canName && source.Entries.SelectMany(e => e.Periods).SelectMany(p => p.Items)
                    .Any(i => i.Role is not (MemoryRoles.Local or MemoryRoles.Other)))
            {
                throw new InvalidOperationException(
                    "A memory item in the memories file has a role that is neither Local nor Other, so no memory layers are shown.");
            }

            var images = source.Images;
            var links = source.Links.ToLookup(l => l.FromEntryID);
            var order = source.Collections.Select((c, i) => (c.CollectionID, i)).ToDictionary(p => p.CollectionID, p => p.i);

            var entries = source.Entries
                .Where(e => order.ContainsKey(e.Entry.CollectionID) && !unnamed.Contains(e.Entry.CollectionID))
                .OrderBy(e => order[e.Entry.CollectionID])
                .ThenBy(e => e.Entry.EntryID)
                .Select(e => new MemoryLayerEntry(
                    e.Entry,
                    e.Periods.Select(period => new ArchiveEntryPeriod
                    {
                        EntryID = period.EntryID,
                        PeriodIndex = period.PeriodIndex,
                        StartUtc = period.StartUtc,
                        EndUtc = period.EndUtc,
                        StartLocal = period.StartLocal,
                        EndLocal = period.EndLocal,
                        Label = period.Label,
                        LocalItemCount = period.LocalItemCount,
                        OtherItemCount = period.OtherItemCount,
                        Items = period.Items.Select(item => new ArchiveEntryItem
                        {
                            EntryItemID = item.EntryItemID,
                            EntryID = item.EntryID,
                            PeriodIndex = item.PeriodIndex,
                            ParticipantID = item.Role == MemoryRoles.Local ? localParticipantID!.Value : otherParticipantID!.Value,
                            SetId = item.SetId,
                            ItemId = item.ItemId,
                            ItemType = item.ItemType,
                            TimeUtc = item.TimeUtc,
                            TimeLocal = item.TimeLocal,
                        }).ToList(),
                    }).ToList(),
                    e.Entry.ImageSha256 is { } sha && images.TryGetValue(sha, out var image) && File.Exists(image.FullPath)
                        ? image.FullPath
                        : null,
                    links[e.Entry.EntryID].ToList())
                {
                    ExtraPhotos = e.Photos
                        .Select(photo => (
                            images.TryGetValue(photo.Sha256, out var file) && File.Exists(file.FullPath) ? file.FullPath : null,
                            photo.Caption))
                        .ToList(),
                })
                .ToList();


            return new MemoryLayersFile
            {
                Collections = source.Collections.Where(c => !unnamed.Contains(c.CollectionID)).ToList(),
                Unnamed = source.Collections.Where(c => unnamed.Contains(c.CollectionID)).ToList(),
                Entries = entries,
                Definitions = source.Definitions,
                IconPath = definition =>
                    definition.IconSha256 is { } sha && images.TryGetValue(sha, out var icon) && File.Exists(icon.FullPath)
                        ? icon.FullPath
                        : null,
            };
        }
    }
}
