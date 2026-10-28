using System.IO;
using Chatcheology.Archive;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Viewer;
using Chatcheology.Mobile.Memories;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Services
{
    /// <summary>The memory layers of one conversation, as read (CH-42 step 5).</summary>
    /// <param name="Index">The shared memory index (the phone's rules); empty without a memories file.</param>
    /// <param name="Unnamed">Collections not shown because their items cannot be named without "Show as".</param>
    /// <param name="Problem">A fixed sentence when the memories file could not be read; otherwise <see langword="null"/>.</param>
    internal sealed record LoadedMemoryLayers(MemoryIndex Index, IReadOnlyList<ArchiveCollection> Unnamed, string? Problem)
    {
        /// <summary>No layers.</summary>
        internal static LoadedMemoryLayers None { get; } = new(MemoryIndex.Empty, [], null);
    }

    /// <summary>
    /// Builds the phone's <see cref="MemoryIndex"/> from the memories file beside the workspace and the
    /// workspace conversation (CH-42 step 5), so the desktop places, filters and words memory rows with the
    /// very same code as the phone.
    /// </summary>
    /// <remarks>
    /// The memories file is read only. Local and Other roles map onto the conversation's two
    /// participants: Local is the "Show as" participant, Other the one other participant; without that,
    /// collections with items are listed as <see cref="LoadedMemoryLayers.Unnamed"/> instead of shown.
    /// Rows are placed among the conversation's messages by their stored local times, and message links
    /// name workspace sequence numbers directly.
    /// </remarks>
    internal static class MemoryLayerSource
    {
        private const string NoLayers = "so no memory layers are shown.";

        /// <summary>Reads the layers. Runs off the dispatcher.</summary>
        internal static LoadedMemoryLayers Load(
            string workspacePath,
            ConversationSummary conversation,
            long? localParticipantID,
            ConversationMessageTimes times,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            ArgumentNullException.ThrowIfNull(times);

            long? otherParticipantID = localParticipantID is { } local
                                       && conversation.Participants.Count == 2
                                       && conversation.Participants.Any(p => p.ParticipantID == local)
                ? conversation.Participants.Single(p => p.ParticipantID != local).ParticipantID
                : null;

            MemoryLayersFile? file;

            try
            {
                file = MemoryLayersFile.ReadBeside(
                    workspacePath, otherParticipantID is null ? null : localParticipantID, otherParticipantID, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or SqliteException)
            {
                // The Data layer's own fixed sentences; anything else may carry a path, so it is not repeated.
                return new LoadedMemoryLayers(
                    MemoryIndex.Empty,
                    [],
                    exception is InvalidOperationException && exception.Message.EndsWith(NoLayers, StringComparison.Ordinal)
                        ? exception.Message
                        : "The memories file beside the workspace could not be read, " + NoLayers);
            }

            if (file is null)
            {
                return LoadedMemoryLayers.None;
            }

            var index = MemoryIndex.Build(new MemorySource
            {
                Collections = file.Collections,
                Entries = file.Entries.Select(e => new MemorySourceEntry(e.Entry, e.Periods, e.ImagePath, e.Links)).ToList(),
                ParticipantNames = conversation.Participants.ToDictionary(p => p.ParticipantID, p => p.DisplayName),
                Definitions = file.Definitions,
                IconPath = file.IconPath,
                MessageTimes = times.TimesLocal,
                MessageSequenceNumbers = times.SequenceNumbers,
            });

            return new LoadedMemoryLayers(index, file.Unnamed, null);
        }
    }
}
