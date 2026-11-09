using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Core.Models;
using Chatcheology.Data.Memories;
using Chatcheology.Data.NewerMessages;
using Chatcheology.Data.OlderExports;

namespace Chatcheology.Data.ArchiveExport
{
    /// <summary>
    /// The three source files of an archive (format version 2), read before anything is written: the
    /// main workspace, and, found beside it by fixed name, the older-exports file and the memories file.
    /// </summary>
    /// <remarks>
    /// <para><strong>One continuous conversation</strong> (CH-33 D1, 3.4): the shown older-export
    /// messages first, exports in <c>Position</c> order, each from its cut on, then the workspace
    /// conversation. The archive sequence number is the position in that combined timeline, so the
    /// workspace's messages move up by the number of shown older messages. Older messages get archive
    /// message (and attachment) identifiers above the workspace conversation's own, in timeline order.
    /// </para>
    /// <para><strong>Participants.</strong> The archive's participants are the workspace
    /// conversation's. An older export's Local sender is the archive's local participant and its Other
    /// sender the conversation's other participant (by <c>ExportRole</c>, never by name); memory items
    /// map the same way by role. Either needs a two-participant conversation.</para>
    /// <para><strong>Memories.</strong> Collections, entries, periods and items keep the memories file's
    /// identifiers. A link to a message names the workspace sequence number; it is re-mapped through
    /// the combined timeline, and a link to no workspace message refuses the export.</para>
    /// <para><strong>One owner for the rows.</strong> The exporter writes what this builds and
    /// <see cref="ArchiveWorkspaceComparer"/> compares an archive against it.</para>
    /// <para>A missing older-exports or memories file means none: counted as zero, never an error.</para>
    /// <para><strong>Newer messages</strong> (format version 4, CH-71 step 3b): the newer-messages file beside the
    /// workspace, after the workspace conversation in <c>NewerMessageID</c> order, <c>Source = Msgstore</c>.
    /// Identifiers and sequence numbers follow <see cref="NewerMessagesArchiveIds"/>, so every earlier message and
    /// attachment keeps its identifier and sequence number when newer messages are added. A system row is a System
    /// message with its fixed wording; a placeholder (something the archive cannot show yet) is a User message of
    /// its sender carrying only its fixed wording, which the phone recognises and shows muted. The file's
    /// participants must be the conversation's local and other participant, as the request names them. A missing
    /// file means none.</para>
    /// </remarks>
    internal sealed class ArchiveCombinedSource
    {
        private ArchiveCombinedSource(
            ArchiveExportSource workspace,
            OlderExportsArchiveSource? older,
            MemoriesArchiveSource? memories,
            NewerMessagesArchiveSource? newer,
            long conversationID,
            long localParticipantID,
            long? otherParticipantID)
        {
            Workspace = workspace;
            Older = older;
            Memories = memories;
            Newer = newer;
            ConversationID = conversationID;
            LocalParticipantID = localParticipantID;
            OtherParticipantID = otherParticipantID;

            OlderTimeline = older?.ShownTimeline.ToList() ?? [];
            OlderMessageIDBase = workspace.Messages.Count == 0 ? 0 : workspace.Messages.Max(m => m.MessageID);
            OlderAttachmentIDBase = workspace.States.Count == 0 ? 0 : workspace.States.Keys.Max();

            var archiveSequence = new Dictionary<int, int>(workspace.Messages.Count);

            // The workspace's own numbering (1..n) moved up past the older messages, so a workspace
            // message keeps its number relative to the workspace and the timeline stays gapless.
            foreach (var message in workspace.Messages)
            {
                archiveSequence[message.SequenceNumber] = OlderTimeline.Count + message.SequenceNumber;
            }

            WorkspaceArchiveSequence = archiveSequence;

            // CH-71 step 3b: newer ids right above everything the older-export part uses (NewerMessagesArchiveIds).
            NewerMessageIDBase = OlderMessageIDBase + OlderTimeline.Count;
            NewerAttachmentIDBase = OlderAttachmentIDBase + OlderTimeline.Sum(pair => pair.Message.Attachments.Count);
        }

        internal ArchiveExportSource Workspace { get; }

        /// <summary>The older-exports file, or <see langword="null"/> when there is none beside the workspace.</summary>
        internal OlderExportsArchiveSource? Older { get; }

        /// <summary>The memories file, or <see langword="null"/> when there is none beside the workspace.</summary>
        internal MemoriesArchiveSource? Memories { get; }

        /// <summary>The newer-messages file, or <see langword="null"/> when there is none beside the workspace.</summary>
        internal NewerMessagesArchiveSource? Newer { get; }

        internal long ConversationID { get; }

        /// <summary>The highest archive message id the workspace and older-export parts use: newer ids start above it.</summary>
        internal long NewerMessageIDBase { get; }

        /// <summary>The highest archive attachment id the workspace and older-export parts use: newer ids start above it.</summary>
        internal long NewerAttachmentIDBase { get; }

        /// <summary>The newer messages; none when there is no file.</summary>
        internal int NewerMessageCount => Newer?.Messages.Count ?? 0;

        internal long LocalParticipantID { get; }

        /// <summary>The conversation's other participant, when it has exactly two.</summary>
        internal long? OtherParticipantID { get; }

        /// <summary>The shown older messages in timeline order.</summary>
        internal IReadOnlyList<(OlderExport Export, OlderMessage Message)> OlderTimeline { get; }

        /// <summary>Older message identifiers start above this.</summary>
        internal long OlderMessageIDBase { get; }

        /// <summary>Older attachment identifiers start above this.</summary>
        internal long OlderAttachmentIDBase { get; }

        /// <summary>Each workspace sequence number's archive sequence number.</summary>
        internal IReadOnlyDictionary<int, int> WorkspaceArchiveSequence { get; }

        internal int MessageCount => OlderTimeline.Count + Workspace.Messages.Count + NewerMessageCount;

        /// <summary>The messages before the newer ones: the shown older messages and the workspace conversation's.</summary>
        internal int EarlierMessageCount => OlderTimeline.Count + Workspace.Messages.Count;

        /// <summary>Every recorded media root of the source files, and the memories media folder.</summary>
        internal IEnumerable<string> MediaRoots =>
            Workspace.MediaRoots
                .Concat(Older?.MediaRoots ?? [])
                .Concat(Newer?.MediaRoots ?? [])
                .Concat(Memories is { } memories ? [memories.MediaFolder] : []);

        /// <summary>Reads the three files.</summary>
        /// <exception cref="ArchiveExportRefusedException">A file or its contents is unsuitable. Nothing was written.</exception>
        internal static ArchiveCombinedSource Read(
            string workspacePath,
            long conversationID,
            long localParticipantID,
            int pageSize,
            IProgress<ArchiveExportProgress>? progress,
            CancellationToken cancellationToken)
        {
            var workspace = ArchiveExportSource.Read(
                workspacePath, conversationID, localParticipantID, pageSize, progress, cancellationToken);

            var older = OlderExportsArchiveSource.ReadBeside(workspacePath, cancellationToken);
            var memories = MemoriesArchiveSource.ReadBeside(workspacePath, cancellationToken);
            var newer = NewerMessagesArchiveSource.ReadBeside(workspacePath, cancellationToken);

            var others = workspace.Participants.Where(p => p.ParticipantID != localParticipantID).ToList();
            long? other = others.Count == 1 && workspace.Participants.Count == 2 ? others[0].ParticipantID : null;

            var source = new ArchiveCombinedSource(workspace, older, memories, newer, conversationID, localParticipantID, other);

            source.RefuseWhatCannotBeMapped();

            return source;
        }

        // -------------------------------------------------------------------------------------
        // Checks made before anything is written.
        // -------------------------------------------------------------------------------------

        private void RefuseWhatCannotBeMapped()
        {
            RefuseNewerThatCannotBeMapped();

            var needsTwo = OlderTimeline.Count > 0
                           || (Memories?.Entries.Any(e => e.Periods.Any(p => p.Items.Count > 0)) ?? false);

            if (needsTwo && OtherParticipantID is null)
            {
                throw new ArchiveExportRefusedException(
                    "Older-export messages and memory items are mapped onto the conversation's two participants, " +
                    "but the conversation does not have exactly two. Nothing was written.");
            }

            foreach (var (export, message) in OlderTimeline)
            {
                if (message.Type == MessageType.User
                    && (message.SenderParticipantID is not { } sender || !export.Roles.ContainsKey(sender)))
                {
                    throw new ArchiveExportRefusedException(
                        "An older-export message's sender has no Local or Other role. Nothing was written.");
                }

                foreach (var attachment in message.Attachments)
                {
                    if (attachment.ResolvedAssetID is not null && !IsStorableName(attachment.FileName!))
                    {
                        throw new ArchiveExportRefusedException(
                            "An older export names an attached file whose name cannot be stored as a bare file " +
                            "name. Nothing was written.");
                    }
                }

                if (message.Attachments.Any(a => a.ResolvedAssetID is not null) && export.ImportedUtc is null)
                {
                    throw new ArchiveExportRefusedException(
                        "An older export has no recorded import time for its restored files. Nothing was written.");
                }
            }

            if (Memories is null)
            {
                return;
            }

            foreach (var item in Memories.Entries.SelectMany(e => e.Periods).SelectMany(p => p.Items))
            {
                if (item.Role is not (MemoryRoles.Local or MemoryRoles.Other))
                {
                    throw new ArchiveExportRefusedException(
                        "A memory item has a role that is neither Local nor Other. Nothing was written.");
                }
            }

            var unmapped = Memories.Links.Count(link =>
                link.ToMessageSequenceNumber is { } sequence && !WorkspaceArchiveSequence.ContainsKey(sequence));

            if (unmapped > 0)
            {
                throw new ArchiveExportRefusedException(
                    $"{unmapped.ToString(CultureInfo.InvariantCulture)} memory link(s) name a message the " +
                    "workspace conversation does not hold, so they cannot be mapped onto the archive's " +
                    "timeline. Nothing was written.");
            }
        }

        /// <summary>
        /// The newer-messages file must continue this conversation, with this local participant and the
        /// conversation's other one, from this workspace's end; every sender must follow its direction.
        /// </summary>
        private void RefuseNewerThatCannotBeMapped()
        {
            if (Newer is not { } newer)
            {
                return;
            }

            if (newer.Info.ConversationID != ConversationID)
            {
                throw new ArchiveExportRefusedException(
                    "The newer-messages file beside the workspace continues another conversation. Nothing was written.");
            }

            if (OtherParticipantID is null
                || newer.Info.LocalParticipantID != LocalParticipantID
                || newer.Info.OtherParticipantID != OtherParticipantID)
            {
                throw new ArchiveExportRefusedException(
                    "The newer-messages file's participants are not the conversation's two, with the LocalParticipantID " +
                    "as its own side. Nothing was written.");
            }

            if (newer.Messages.Count == 0)
            {
                return;
            }

            // D1/D2: the workspace is the checked past and does not change; newer messages continue from its last
            // day. A workspace that now ends on another day would put them in the wrong place.
            DateOnly? lastDay = Workspace.Messages.Count == 0
                ? null
                : DateOnly.FromDateTime(Workspace.Messages.Max(m => m.MessageDateTimeLocal));

            if (lastDay != newer.Info.WorkspaceLastDay)
            {
                throw new ArchiveExportRefusedException(
                    "The workspace conversation no longer ends on the day the newer messages continue from. Nothing " +
                    "was written.");
            }

            // NewerMessagesArchiveIds: newer sequence numbers follow the earlier ones directly, which needs the
            // earlier timeline to be gapless.
            if (WorkspaceArchiveSequence.Values.Max() != EarlierMessageCount)
            {
                throw new ArchiveExportRefusedException(
                    "The workspace conversation's sequence numbers are not 1 to its message count, so newer messages " +
                    "cannot be numbered after them. Nothing was written.");
            }

            var wrongSender = newer.Messages.Count(m =>
                m.Kind != NewerMessagesDatabase.KindSystem
                && m.SenderParticipantID != (m.FromMe ? LocalParticipantID : OtherParticipantID));

            if (wrongSender > 0)
            {
                throw new ArchiveExportRefusedException(
                    $"{wrongSender.ToString(CultureInfo.InvariantCulture)} newer message(s) name a sender that does not " +
                    "follow their direction. Nothing was written.");
            }
        }

        private static bool IsStorableName(string name) =>
            name.Length > 0 && name.IndexOfAny(['/', '\\', ':']) < 0 && !name.Any(char.IsControl);

        // -------------------------------------------------------------------------------------
        // The archive's rows.
        // -------------------------------------------------------------------------------------

        /// <summary>The archive's info: the workspace's, with one period per shown older export.</summary>
        internal IReadOnlyList<ArchiveExportPeriod> ExportPeriods =>
            Older?.Exports
                .Where(e => e.Shown && e.ShownMessages.Count > 0)
                .Select(e => new ArchiveExportPeriod(
                    e.ShownMessages.Min(m => m.DateTimeLocal), e.ShownMessages.Max(m => m.DateTimeLocal)))
                .ToList()
            ?? [];

        /// <summary>The archive participant of a role.</summary>
        private long ParticipantOf(OlderExportRole role) =>
            role == OlderExportRole.Local ? LocalParticipantID : OtherParticipantID!.Value;

        /// <summary>The archive participant of a memory item's role.</summary>
        internal long ParticipantOf(string role) =>
            role == MemoryRoles.Local ? LocalParticipantID : OtherParticipantID!.Value;

        /// <summary>
        /// Every message of the combined conversation in archive order, as the archive holds it.
        /// </summary>
        /// <param name="workspaceOriginalFileName">As for <see cref="ArchiveExportSource.ToArchiveMessage"/>.</param>
        /// <param name="linksOutside">Low-quality links left out (workspace only).</param>
        internal List<ArchiveMessage> ToArchiveMessages(Func<long, string?> workspaceOriginalFileName, out int linksOutside)
        {
            var messages = new List<ArchiveMessage>(MessageCount);
            var nextAttachmentID = OlderAttachmentIDBase;

            for (var index = 0; index < OlderTimeline.Count; index++)
            {
                var (export, message) = OlderTimeline[index];
                var messageID = OlderMessageIDBase + index + 1;

                var attachments = message.Attachments
                    .Select(attachment => ToArchiveAttachment(export, attachment, messageID, ++nextAttachmentID))
                    .ToList();

                messages.Add(new ArchiveMessage
                {
                    MessageID = messageID,
                    SequenceNumber = index + 1,
                    DateTimeLocal = message.DateTimeLocal,
                    Kind = message.Type == MessageType.System ? ArchiveMessageKind.System : ArchiveMessageKind.User,
                    SenderParticipantID = message.Type == MessageType.System
                        ? null
                        : ParticipantOf(export.Roles[message.SenderParticipantID!.Value]),

                    // Stored raw, as for the workspace. Hiding a "(file attached)" first line is the viewer's.
                    Text = message.Content,

                    // An older export records no forwarded fact: not known.
                    IsForwarded = null,
                    ForwardScore = null,
                    Attachments = attachments,
                    Source = ArchiveMessageSource.Export,
                });
            }

            linksOutside = 0;

            foreach (var message in Workspace.Messages)
            {
                var archived = Workspace.ToArchiveMessage(message, workspaceOriginalFileName, ref linksOutside);

                messages.Add(new ArchiveMessage
                {
                    MessageID = archived.MessageID,
                    SequenceNumber = WorkspaceArchiveSequence[message.SequenceNumber],
                    DateTimeLocal = archived.DateTimeLocal,
                    Kind = archived.Kind,
                    SenderParticipantID = archived.SenderParticipantID,
                    Text = archived.Text,
                    IsForwarded = archived.IsForwarded,
                    ForwardScore = archived.ForwardScore,
                    Attachments = archived.Attachments,
                    Source = ArchiveMessageSource.Workspace,
                });
            }

            if (Newer is { } newer)
            {
                for (var index = 0; index < newer.Messages.Count; index++)
                {
                    messages.Add(ToArchiveMessage(newer, newer.Messages[index], index + 1));
                }
            }

            return messages;
        }

        /// <summary>
        /// One newer message as the archive holds it (CH-71 step 3b). User: its text exactly as stored (empty for a
        /// media message without text), its one attachment with the caption. System: no sender, the fixed wording.
        /// Placeholder: its sender, the fixed wording only. Forwarded facts as stored (null = not known): on the
        /// attachment for a media message, as the workspace keeps them per attachment, otherwise on the message.
        /// </summary>
        private ArchiveMessage ToArchiveMessage(NewerMessagesArchiveSource newer, NewerMessageRow row, int position)
        {
            var messageID = NewerMessagesArchiveIds.MessageID(NewerMessageIDBase, row.NewerMessageID);
            var isSystem = row.Kind == NewerMessagesDatabase.KindSystem;
            var attachments = new List<ArchiveAttachment>(1);

            if (row.Attachment is { } attachment)
            {
                attachments.Add(ToArchiveAttachment(newer, row, attachment, messageID));
            }

            NewerMessagesArchiveSource.TryParseLocal(row.DateTimeLocal, out var local);

            return new ArchiveMessage
            {
                MessageID = messageID,
                SequenceNumber = NewerMessagesArchiveIds.SequenceNumber(EarlierMessageCount, position),
                DateTimeLocal = local,
                Kind = isSystem ? ArchiveMessageKind.System : ArchiveMessageKind.User,
                SenderParticipantID = isSystem ? null : row.SenderParticipantID,
                Text = row.Text ?? string.Empty,
                IsForwarded = row.Attachment is null ? row.IsForwarded : null,
                ForwardScore = row.Attachment is null ? row.ForwardScore : null,
                Attachments = attachments,
                Source = ArchiveMessageSource.Msgstore,
            };
        }

        private ArchiveAttachment ToArchiveAttachment(
            NewerMessagesArchiveSource newer, NewerMessageRow row, NewerAttachmentRow attachment, long messageID)
        {
            var restored = attachment.State == NewerMessagesDatabase.StateRestored;
            var file = restored ? newer.Files[attachment.NewerAttachmentID] : null;

            return new ArchiveAttachment
            {
                AttachmentID = NewerMessagesArchiveIds.AttachmentID(NewerAttachmentIDBase, attachment.NewerAttachmentID),
                MessageID = messageID,
                Ordinal = 1,
                State = restored ? ArchiveAttachmentState.Restored : ArchiveAttachmentState.Unmatched,
                MediaSha256 = restored ? attachment.Sha256 : null,
                OriginalFileName = file?.FileName,
                IsForwarded = row.IsForwarded,
                ForwardScore = row.ForwardScore,
                LowQualityCopyOfAttachmentID = attachment.LowQualityCopyOfAttachmentID is { } full
                    ? NewerMessagesArchiveIds.AttachmentID(NewerAttachmentIDBase, full)
                    : null,
                Basis = restored
                    ? new ArchiveAttachmentBasis
                    {
                        BasisType = NewerMessagesArchiveSource.BasisType,
                        BasisText = NewerMessagesArchiveSource.BasisText,
                        Actor = null,
                        ConfirmedUtc = newer.RunTimes[row.RunID],
                    }
                    : null,
                Caption = row.Caption,
            };
        }

        /// <summary>
        /// The newer media files the archive needs: one per distinct stored path (hash and the file's extension), with
        /// the file under its media root.
        /// </summary>
        internal IReadOnlyList<(string Sha256, string FileName, string FullPath)> NewerMediaNeeds() =>
            Newer is not { } newer
                ? []
                : newer.Messages
                    .Select(m => m.Attachment)
                    .OfType<NewerAttachmentRow>()
                    .Where(a => a.State == NewerMessagesDatabase.StateRestored)
                    .Select(a => (Sha256: a.Sha256!, File: newer.Files[a.NewerAttachmentID]))
                    .DistinctBy(a => ArchiveFormat.MediaFileName(a.Sha256, a.File.FileName))
                    .OrderBy(a => ArchiveFormat.MediaFileName(a.Sha256, a.File.FileName), StringComparer.Ordinal)
                    .Select(a => (a.Sha256, a.File.FileName, a.File.FullPath))
                    .ToList();

        private ArchiveAttachment ToArchiveAttachment(
            OlderExport export, OlderAttachment attachment, long messageID, long attachmentID)
        {
            if (attachment.ResolvedAssetID is not { } assetID)
            {
                // Everything not restored by exact name; never reviewed, so never Missing.
                return new ArchiveAttachment
                {
                    AttachmentID = attachmentID,
                    MessageID = messageID,
                    Ordinal = attachment.Ordinal,
                    State = ArchiveAttachmentState.Unmatched,
                    MediaSha256 = null,
                    OriginalFileName = null,
                    IsForwarded = null,
                    ForwardScore = null,
                    LowQualityCopyOfAttachmentID = null,
                    Basis = null,
                };
            }

            return new ArchiveAttachment
            {
                AttachmentID = attachmentID,
                MessageID = messageID,
                Ordinal = attachment.Ordinal,
                State = ArchiveAttachmentState.Restored,
                MediaSha256 = Older!.Assets[assetID].Sha256,
                OriginalFileName = attachment.FileName,
                IsForwarded = null,
                ForwardScore = null,
                LowQualityCopyOfAttachmentID = null,
                Basis = new ArchiveAttachmentBasis
                {
                    BasisType = OlderExportsArchiveSource.BasisType,
                    BasisText = OlderExportsArchiveSource.BasisText,
                    Actor = null,
                    ConfirmedUtc = export.ImportedUtc!.Value,
                },
            };
        }

        /// <summary>
        /// The older media files the archive needs: one per distinct stored path (hash and the
        /// referenced name's extension), with the payload's recorded copies.
        /// </summary>
        internal IReadOnlyList<(string Sha256, string FileName, IReadOnlyList<MediaAssetLocationCopy> Copies)> OlderMediaNeeds()
        {
            if (Older is null)
            {
                return [];
            }

            return OlderTimeline
                .SelectMany(pair => pair.Message.Attachments)
                .Where(a => a.ResolvedAssetID is not null)
                .Select(a => (AssetID: a.ResolvedAssetID!.Value, a.FileName))
                .Select(a => (Older.Assets[a.AssetID].Sha256, FileName: a.FileName!, a.AssetID))
                .DistinctBy(a => ArchiveFormat.MediaFileName(a.Sha256, a.FileName))
                .OrderBy(a => ArchiveFormat.MediaFileName(a.Sha256, a.FileName), StringComparer.Ordinal)
                .Select(a => (
                    a.Sha256,
                    a.FileName,
                    (IReadOnlyList<MediaAssetLocationCopy>)(Older.Locations.GetValueOrDefault(a.AssetID) ?? [])
                        .Select(l => new MediaAssetLocationCopy(l.FullPath))
                        .ToList()))
                .ToList();
        }

        /// <summary>
        /// Every payload once, by hash: the workspace's resolved payloads as the workspace describes them,
        /// then the shown older restored payloads, then the entry images and item icons (recorded size only).
        /// </summary>
        internal IReadOnlyList<ArchiveMedia> ToArchiveMedia()
        {
            var media = Workspace.ToArchiveMedia().ToList();
            var seen = media.Select(m => m.Sha256).ToHashSet(StringComparer.Ordinal);

            foreach (var asset in OlderTimeline
                         .SelectMany(pair => pair.Message.Attachments)
                         .Select(a => a.ResolvedAssetID)
                         .OfType<long>()
                         .Distinct()
                         .Order()
                         .Select(id => Older!.Assets[id]))
            {
                if (seen.Add(asset.Sha256))
                {
                    media.Add(new ArchiveMedia
                    {
                        Sha256 = asset.Sha256,
                        MediaType = asset.MediaType,
                        SizeBytes = asset.SizeBytes,
                        DurationMS = asset.DurationMS,
                        Width = asset.Width,
                        Height = asset.Height,
                    });
                }
            }

            // CH-71 step 3b: newer restored payloads, in timeline order, as the file and the media folder describe them.
            foreach (var (sha, file) in (Newer?.Messages ?? [])
                         .Select(m => m.Attachment)
                         .OfType<NewerAttachmentRow>()
                         .Where(a => a.State == NewerMessagesDatabase.StateRestored)
                         .Select(a => (a.Sha256!, Newer!.Files[a.NewerAttachmentID])))
            {
                if (seen.Add(sha))
                {
                    media.Add(new ArchiveMedia
                    {
                        Sha256 = sha,
                        MediaType = file.MediaType,
                        SizeBytes = file.SizeBytes,
                        DurationMS = null,
                        Width = null,
                        Height = null,
                    });
                }
            }

            foreach (var image in (Memories?.Images.Values ?? []).OrderBy(i => i.Sha256, StringComparer.Ordinal))
            {
                if (seen.Add(image.Sha256))
                {
                    media.Add(new ArchiveMedia
                    {
                        Sha256 = image.Sha256,
                        MediaType = "Image",
                        SizeBytes = image.SizeBytes,
                        DurationMS = null,
                        Width = null,
                        Height = null,
                    });
                }
            }

            return media;
        }

        internal IReadOnlyList<ArchiveCollection> ToArchiveCollections() => Memories?.Collections ?? [];

        /// <summary>Every entry with its periods and items, the items naming archive participants.</summary>
        /// <param name="imageExtension">
        /// The extension the entry image is stored under: the one an attachment's file of the same
        /// payload already has, so one payload is one file, or else the memories file's own. A
        /// comparison passes the memories file's own.
        /// </param>
        internal IReadOnlyList<ArchiveEntry> ToArchiveEntries(Func<string, string, string> imageExtension) =>
            Memories?.Entries.Select(row => new ArchiveEntry
            {
                EntryID = row.Entry.EntryID,
                CollectionID = row.Entry.CollectionID,
                SourceKey = row.Entry.SourceKey,
                Title = row.Entry.Title,
                StartLocal = row.Entry.StartLocal,
                EndLocal = row.Entry.EndLocal,
                StartUtc = row.Entry.StartUtc,
                EndUtc = row.Entry.EndUtc,
                DatePrecision = row.Entry.DatePrecision,
                Summary = row.Entry.Summary,
                ImageSha256 = row.Entry.ImageSha256,
                ImageExtension = row.Entry.ImageSha256 is { } sha ? imageExtension(sha, row.Entry.ImageExtension!) : null,
                Source = row.Entry.Source,
                DayLevel = row.Entry.DayLevel,
                EveryYear = row.Entry.EveryYear,
                Images = row.Photos.Select(photo => new ArchiveEntryImage
                {
                    EntryID = photo.EntryID,
                    Ordinal = photo.Ordinal,
                    Sha256 = photo.Sha256,
                    Extension = imageExtension(photo.Sha256, Memories!.Images[photo.Sha256].Extension),
                    Caption = photo.Caption,
                }).ToList(),
                Periods = row.Periods.Select(period => new ArchiveEntryPeriod
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
                        ParticipantID = ParticipantOf(item.Role),
                        SetId = item.SetId,
                        ItemId = item.ItemId,
                        ItemType = item.ItemType,
                        TimeUtc = item.TimeUtc,
                        TimeLocal = item.TimeLocal,
                    }).ToList(),
                }).ToList(),
            }).ToList()
            ?? [];

        /// <summary>The item definitions some item uses, each icon under the extension <paramref name="iconExtension"/> gives (as for entry images).</summary>
        internal IReadOnlyList<ArchiveItemDefinition> ToArchiveItemDefinitions(Func<string, string, string> iconExtension) =>
            Memories?.Definitions.Select(d => new ArchiveItemDefinition
            {
                SetId = d.SetId,
                ItemId = d.ItemId,
                ItemType = d.ItemType,
                Name = d.Name,
                Detail = d.Detail,
                Hidden = d.Hidden,
                GroupId = d.GroupId,
                IconSha256 = d.IconSha256,
                IconExtension = d.IconSha256 is { } sha ? iconExtension(sha, d.IconExtension!) : null,
            }).ToList()
            ?? [];

        /// <summary>The noted dates (CH-47) exactly as the memories file holds them, with their identifiers.</summary>
        internal IReadOnlyList<ArchiveNotedDate> ToArchiveNotedDates() => Memories?.NotedDates ?? [];

        /// <summary>The external links (CH-48) exactly as the memories file holds them, with their identifiers.</summary>
        internal IReadOnlyList<ArchiveExternalLink> ToArchiveExternalLinks() => Memories?.ExternalLinks ?? [];

        /// <summary>Every link, a message link re-mapped to the archive sequence number.</summary>
        internal IReadOnlyList<ArchiveEntryLink> ToArchiveLinks() =>
            Memories?.Links.Select(link => new ArchiveEntryLink
            {
                EntryLinkID = link.EntryLinkID,
                FromEntryID = link.FromEntryID,
                ToEntryID = link.ToEntryID,
                ToMessageSequenceNumber = link.ToMessageSequenceNumber is { } sequence
                    ? WorkspaceArchiveSequence[sequence]
                    : null,
            }).ToList()
            ?? [];

        /// <summary>Entries, periods and items per collection, as the memories file holds them.</summary>
        internal IReadOnlyList<ArchiveCollectionCounts> CollectionCounts() =>
            Memories?.Collections
                .Select(c =>
                {
                    var entries = Memories.Entries.Where(e => e.Entry.CollectionID == c.CollectionID).ToList();

                    return new ArchiveCollectionCounts(
                        c.CollectionID,
                        entries.Count,
                        entries.Sum(e => e.Periods.Count),
                        entries.Sum(e => e.Periods.Sum(p => p.Items.Count)));
                })
                .ToList()
            ?? [];
    }

    /// <summary>One recorded copy of a payload, by its full path.</summary>
    internal sealed record MediaAssetLocationCopy(string FullPath);
}
