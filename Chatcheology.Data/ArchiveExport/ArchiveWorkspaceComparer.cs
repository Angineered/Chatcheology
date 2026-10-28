using Chatcheology.Archive;
using Chatcheology.Data.Viewer;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.ArchiveExport
{
    /// <summary>
    /// Compares a portable archive with what the workspace says now: every participant, message and
    /// attachment must be what an export made today would write.
    /// </summary>
    /// <remarks>
    /// <para><strong>Read-only on both sides.</strong> The workspace is read through
    /// <see cref="ArchiveExportSource"/>, the exporter's own reading path (the same public readers and
    /// the same single <c>MediaAsset.SHA256</c> query, on one read-only connection), and the archive
    /// through <see cref="ArchiveReader"/>. The expected rows come from the same methods the exporter
    /// writes with, so the comparison cannot disagree with an export about what the workspace
    /// says.</para>
    /// <para><strong>What is compared.</strong> The archive's title, source export hash, time zone and
    /// local participant; each participant's name and local flag; each message's sequence number,
    /// time, kind, sender, text and forwarded status; each attachment's message and ordinal, state,
    /// media hash, forwarded status, low-quality link and basis line; and each payload's recorded
    /// description. An attachment's original file name is not compared: it names whichever copy the
    /// export took the bytes from, which the workspace does not decide. The media hash is.</para>
    /// <para>This compares content, not files: whether the archive's files are whole is
    /// <see cref="ArchiveValidator"/>'s question.</para>
    /// <para><strong>Counts only.</strong> The result never carries message text, a name, a file name
    /// or a path; a mismatch is identified at most by its message's sequence number or its
    /// attachment's identifier.</para>
    /// </remarks>
    public sealed class ArchiveWorkspaceComparer
    {
        /// <summary>How many mismatches the result lists individually; the rest are only counted.</summary>
        public const int MaximumListedMismatches = 10;

        /// <summary>Messages per workspace page read.</summary>
        internal int PageSize { get; init; } = ConversationReader.MaximumPageSize;

        /// <summary>Compares the archive with the workspace.</summary>
        /// <exception cref="ArchiveComparisonRefusedException">
        /// The workspace or the archive could not be read. The message never carries a path.
        /// </exception>
        public ArchiveComparisonResult Compare(ArchiveComparisonRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.WorkspacePath) || string.IsNullOrWhiteSpace(request.ArchivePath))
            {
                throw new ArchiveComparisonRefusedException("The WorkspacePath or the ArchivePath is empty.");
            }

            ArchiveCombinedSource source;

            try
            {
                source = ArchiveCombinedSource.Read(
                    request.WorkspacePath,
                    request.ConversationID,
                    request.LocalParticipantID,
                    PageSize,
                    progress: null,
                    CancellationToken.None);
            }
            catch (ArchiveExportRefusedException exception)
            {
                // The source's refusals are fixed phrases naming the request's options, never a path.
                // They end by saying an export wrote nothing, which a comparison never could; that
                // closing phrase is left off.
                throw new ArchiveComparisonRefusedException(
                    "The workspace could not be compared: " + WithoutExportClosing(exception.Message), exception);
            }
            catch (SqliteException exception)
            {
                // Not a workspace (not an SQLite file, or damaged). Its own message may carry a path.
                throw new ArchiveComparisonRefusedException(
                    "The workspace could not be compared: the file at WorkspacePath could not be read as a " +
                    "workspace database.",
                    exception);
            }

            try
            {
                using var reader = ArchiveReader.Open(request.ArchivePath);

                return Compare(source, request.LocalParticipantID, reader);
            }
            catch (ArchiveFormatException exception)
            {
                throw new ArchiveComparisonRefusedException(
                    "The archive could not be read: " + exception.Message, exception);
            }
            catch (SqliteException exception)
            {
                // Its own message may carry a path; it stays on the inner exception.
                throw new ArchiveComparisonRefusedException(
                    "The archive could not be read: its database could not be read as an SQLite database.",
                    exception);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        private const string ExportClosing = " Nothing was written.";

        private static string WithoutExportClosing(string message) =>
            message.EndsWith(ExportClosing, StringComparison.Ordinal) ? message[..^ExportClosing.Length] : message;

        private static ArchiveComparisonResult Compare(ArchiveCombinedSource combined, long localParticipantID, ArchiveReader reader)
        {
            var record = new Recorder();
            var source = combined.Workspace;

            CompareInfo(combined, localParticipantID, reader.Info, record);
            CompareParticipants(source.ToArchiveParticipants(localParticipantID).ToList(), reader.ReadParticipants(), record);

            // The expected rows, built exactly as the exporter builds them. No workspace original file
            // name: a comparison copies nothing, so it neither knows nor compares one.
            var expectedList = combined.ToArchiveMessages(static _ => null, out _);
            var expectedMessages = expectedList.ToDictionary(m => m.MessageID);
            var expectedAttachments = expectedList.SelectMany(m => m.Attachments).ToDictionary(a => a.AttachmentID);

            var expectedMedia = combined.ToArchiveMedia().ToDictionary(m => m.Sha256, StringComparer.Ordinal);
            var actualMedia = new Dictionary<string, ArchiveMedia>(StringComparer.Ordinal);

            var seenMessages = new HashSet<long>();
            var seenAttachments = new HashSet<long>();
            int workspaceMessages = 0, exportMessages = 0;

            // Refuses (ArchiveFormatException) an archive whose messages cannot all be read in order.
            foreach (var page in reader.ReadAllPages())
            {
                foreach (var actual in page.Messages)
                {
                    seenMessages.Add(actual.MessageID);

                    if (actual.Source == ArchiveMessageSource.Export)
                    {
                        exportMessages++;
                    }
                    else
                    {
                        workspaceMessages++;
                    }

                    if (expectedMessages.TryGetValue(actual.MessageID, out var expected))
                    {
                        CompareMessage(expected, actual, record);
                    }
                    else
                    {
                        record.Add(ArchiveMismatchKind.MessageNotInWorkspace, actual.SequenceNumber);
                    }

                    foreach (var attachment in actual.Attachments)
                    {
                        seenAttachments.Add(attachment.AttachmentID);

                        if (expectedAttachments.TryGetValue(attachment.AttachmentID, out var expectedAttachment))
                        {
                            CompareAttachment(expectedAttachment, attachment, actual.SequenceNumber, record);
                        }
                        else
                        {
                            record.Add(ArchiveMismatchKind.AttachmentNotInWorkspace, actual.SequenceNumber, attachment.AttachmentID);
                        }
                    }
                }

                foreach (var (sha, media) in page.Media)
                {
                    actualMedia[sha] = media;
                }
            }

            foreach (var expected in expectedList.Where(m => !seenMessages.Contains(m.MessageID)))
            {
                record.Add(ArchiveMismatchKind.MessageMissingFromArchive, expected.SequenceNumber);
            }

            foreach (var expected in expectedAttachments.Values.Where(a => !seenAttachments.Contains(a.AttachmentID)))
            {
                record.Add(
                    ArchiveMismatchKind.AttachmentMissingFromArchive,
                    expectedMessages[expected.MessageID].SequenceNumber,
                    expected.AttachmentID);
            }

            // Messages per source, proved against the files: the workspace conversation, and the shown
            // part of the older exports.
            if (workspaceMessages != source.Messages.Count)
            {
                record.Add(ArchiveMismatchKind.SourceCountDiffers, null);
            }

            if (exportMessages != combined.OlderTimeline.Count)
            {
                record.Add(ArchiveMismatchKind.SourceCountDiffers, null);
            }

            var actualEntries = reader.ReadAllEntries();

            var actualDefinitions = reader.ReadAllItemDefinitions();

            foreach (var sha in actualEntries.Select(e => e.ImageSha256)
                         .Concat(reader.ReadAllEntryImages().Select(p => p.Sha256))
                         .Concat(actualDefinitions.Select(d => d.IconSha256))
                         .OfType<string>())
            {
                if (!actualMedia.ContainsKey(sha) && reader.ReadMediaOf(sha) is { } media)
                {
                    actualMedia[sha] = media;
                }
            }

            CompareMedia(expectedMedia, actualMedia, record);

            var expectedCollections = combined.CollectionCounts();
            CompareMemories(combined, reader, actualEntries, record);
            CompareDefinitions(combined, actualDefinitions, record);
            CompareNotedDates(combined, reader.ReadNotedDates(), record);

            var attachments = expectedAttachments.Values.ToList();

            return record.Result(
                participants: source.Participants.Count,
                messages: expectedList.Count,
                attachments: attachments.Count,
                restored: attachments.Count(a => a.State == ArchiveAttachmentState.Restored),
                missing: attachments.Count(a => a.State == ArchiveAttachmentState.Missing),
                unmatched: attachments.Count(a => a.State == ArchiveAttachmentState.Unmatched),
                workspaceMessages: source.Messages.Count,
                exportMessages: combined.OlderTimeline.Count,
                exports: combined.Older?.Exports
                    .Select(e => new ArchiveComparisonExportCounts(e.Position, e.Shown, e.MessageCount, e.ShownMessages.Count, e.HiddenCount))
                    .ToList() ?? [],
                collections: expectedCollections,
                definitions: combined.Memories?.Definitions.Count ?? 0,
                notedDates: combined.ToArchiveNotedDates().Count);
        }

        // -------------------------------------------------------------------------------------
        // Field by field.
        // -------------------------------------------------------------------------------------

        private static void CompareInfo(ArchiveCombinedSource combined, long localParticipantID, ArchiveInfo info, Recorder record)
        {
            var source = combined.Workspace;

            var same = new[]
            {
                info.ConversationTitle == source.ConversationTitle,
                info.SourceExportSha256 == source.SourceExportSha256,
                info.TimeZoneID == source.TimeZoneID,
                info.LocalParticipantID == localParticipantID,
                info.ExportPeriods.SequenceEqual(combined.ExportPeriods),
            };

            record.AddCount(ArchiveMismatchKind.ArchiveInfoDiffers, same.Count(s => !s));
        }

        /// <summary>
        /// Version 2's memory rows: each collection, entry (with its periods and items) and link, field
        /// by field, and each collection's entry, period and item counts against the memories file. An
        /// entry image's extension is not compared: it follows whichever file the payload is stored
        /// under. A version 1 archive holds none, so anything in the memories file is missing from it.
        /// </summary>
        private static void CompareMemories(
            ArchiveCombinedSource combined, ArchiveReader reader, IReadOnlyList<ArchiveEntry> actualEntries, Recorder record)
        {
            var expectedCollections = combined.ToArchiveCollections().ToDictionary(c => c.CollectionID);
            var actualCollections = reader.ReadCollections().ToDictionary(c => c.CollectionID);

            record.AddCount(
                ArchiveMismatchKind.CollectionDiffers,
                expectedCollections.Values.Count(e =>
                    !actualCollections.TryGetValue(e.CollectionID, out var a)
                    || a.Kind != e.Kind
                    || a.Name != e.Name
                    || a.SourceDescription != e.SourceDescription
                    || a.ShownByDefault != e.ShownByDefault
                    || a.PeriodGapMinutes != e.PeriodGapMinutes)
                + actualCollections.Keys.Count(id => !expectedCollections.ContainsKey(id)));

            var expectedEntries = combined.ToArchiveEntries((_, own) => own).ToDictionary(e => e.EntryID);
            var actualByID = actualEntries.ToDictionary(e => e.EntryID);

            foreach (var expected in expectedEntries.Values)
            {
                if (!actualByID.TryGetValue(expected.EntryID, out var actual)
                    || !SameEntry(expected, actual)
                    || !SamePeriods(expected.Periods, reader.ReadPeriods(expected.EntryID))
                    || !SamePhotos(expected.Images, reader.ReadEntryImages(expected.EntryID)))
                {
                    record.Add(ArchiveMismatchKind.EntryDiffers, null);
                }
            }

            record.AddCount(ArchiveMismatchKind.EntryDiffers, actualByID.Keys.Count(id => !expectedEntries.ContainsKey(id)));

            var expectedLinks = combined.ToArchiveLinks().ToDictionary(l => l.EntryLinkID);
            var actualLinks = reader.ReadAllLinks().ToDictionary(l => l.EntryLinkID);

            record.AddCount(
                ArchiveMismatchKind.EntryLinkDiffers,
                expectedLinks.Values.Count(e =>
                    !actualLinks.TryGetValue(e.EntryLinkID, out var a)
                    || a.FromEntryID != e.FromEntryID
                    || a.ToEntryID != e.ToEntryID
                    || a.ToMessageSequenceNumber != e.ToMessageSequenceNumber)
                + actualLinks.Keys.Count(id => !expectedLinks.ContainsKey(id)));

            // Counts per collection, proved against the memories file itself.
            var expectedCounts = combined.CollectionCounts();
            var actualCounts = reader.HasMemories
                ? actualCollections.Keys.Order().Select(id =>
                {
                    var entries = actualEntries.Where(e => e.CollectionID == id).ToList();
                    var periods = entries.SelectMany(e => reader.ReadPeriods(e.EntryID)).ToList();

                    return new ArchiveCollectionCounts(id, entries.Count, periods.Count, periods.Sum(p => p.Items.Count));
                }).ToList()
                : [];

            if (!expectedCounts.SequenceEqual(actualCounts))
            {
                record.Add(ArchiveMismatchKind.CollectionCountsDiffer, null);
            }
        }

        /// <summary>
        /// Version 2's item definitions (CH-43): the ones some item of the memories file uses, field by
        /// field, each on one side only counted. An icon's extension is not compared, as for entry images.
        /// </summary>
        private static void CompareDefinitions(
            ArchiveCombinedSource combined, IReadOnlyList<ArchiveItemDefinition> actual, Recorder record)
        {
            var expected = combined.ToArchiveItemDefinitions((_, own) => own).ToDictionary(d => (d.SetId, d.ItemId));
            var actualByKey = actual.ToDictionary(d => (d.SetId, d.ItemId));

            record.AddCount(
                ArchiveMismatchKind.ItemDefinitionDiffers,
                expected.Values.Count(e =>
                    !actualByKey.TryGetValue((e.SetId, e.ItemId), out var a)
                    || a.ItemType != e.ItemType
                    || a.Name != e.Name
                    || a.Detail != e.Detail
                    || a.Hidden != e.Hidden
                    || a.GroupId != e.GroupId
                    || a.IconSha256 != e.IconSha256)
                + actualByKey.Keys.Count(key => !expected.ContainsKey(key)));
        }

        /// <summary>
        /// Noted dates (CH-47): row by row on the identifier, every field compared; a date on one side only
        /// (missing from the archive, or not in the memories file) is a difference too. An archive without
        /// the table holds none.
        /// </summary>
        private static void CompareNotedDates(
            ArchiveCombinedSource combined, IReadOnlyList<ArchiveNotedDate> actual, Recorder record)
        {
            var expected = combined.ToArchiveNotedDates().ToDictionary(d => d.NotedDateID);
            var actualByID = actual.ToDictionary(d => d.NotedDateID);

            record.AddCount(
                ArchiveMismatchKind.NotedDateDiffers,
                expected.Values.Count(e =>
                    !actualByID.TryGetValue(e.NotedDateID, out var a)
                    || !string.Equals(a.Title, e.Title, StringComparison.Ordinal)
                    || a.Month != e.Month
                    || a.Day != e.Day
                    || a.Year != e.Year
                    || !string.Equals(a.Note, e.Note, StringComparison.Ordinal))
                + actualByID.Keys.Count(id => !expected.ContainsKey(id)));
        }

        private static bool SameEntry(ArchiveEntry e, ArchiveEntry a) =>
            e.CollectionID == a.CollectionID
            && e.SourceKey == a.SourceKey
            && e.Title == a.Title
            && SameLocal(e.StartLocal, a.StartLocal)
            && SameLocal(e.EndLocal, a.EndLocal)
            && SameUtc(e.StartUtc, a.StartUtc)
            && SameUtc(e.EndUtc, a.EndUtc)
            && e.DatePrecision == a.DatePrecision
            && e.Summary == a.Summary
            && e.ImageSha256 == a.ImageSha256
            && e.Source == a.Source
            && e.DayLevel == a.DayLevel;

        /// <summary>Extra photos (CH-46) in order, hash and caption; the extension follows the stored file, as for the cover.</summary>
        private static bool SamePhotos(IReadOnlyList<ArchiveEntryImage> expected, IReadOnlyList<ArchiveEntryImage> actual) =>
            expected.Count == actual.Count
            && expected.Zip(actual).All(pair =>
                pair.First.Ordinal == pair.Second.Ordinal
                && pair.First.Sha256 == pair.Second.Sha256
                && pair.First.Caption == pair.Second.Caption);

        private static bool SamePeriods(IReadOnlyList<ArchiveEntryPeriod> expected, IReadOnlyList<ArchiveEntryPeriod> actual) =>

            expected.Count == actual.Count
            && expected.Zip(actual).All(pair =>
                pair.First.PeriodIndex == pair.Second.PeriodIndex
                && SameUtc(pair.First.StartUtc, pair.Second.StartUtc)
                && SameUtc(pair.First.EndUtc, pair.Second.EndUtc)
                && SameLocal(pair.First.StartLocal, pair.Second.StartLocal)
                && SameLocal(pair.First.EndLocal, pair.Second.EndLocal)
                && pair.First.Label == pair.Second.Label
                && pair.First.LocalItemCount == pair.Second.LocalItemCount
                && pair.First.OtherItemCount == pair.Second.OtherItemCount
                && SameItems(pair.First.Items, pair.Second.Items));

        private static bool SameItems(IReadOnlyList<ArchiveEntryItem> expected, IReadOnlyList<ArchiveEntryItem> actual)
        {
            var byID = actual.ToDictionary(i => i.EntryItemID);

            return expected.Count == actual.Count
                   && expected.All(e =>
                       byID.TryGetValue(e.EntryItemID, out var a)
                       && a.ParticipantID == e.ParticipantID
                       && a.SetId == e.SetId
                       && a.ItemId == e.ItemId
                       && a.ItemType == e.ItemType
                       && SameUtc(e.TimeUtc, a.TimeUtc)
                       && SameLocal(e.TimeLocal, a.TimeLocal));
        }

        private static bool SameLocal(DateTime? e, DateTime? a) =>
            (e is null && a is null)
            || (e is { } x && a is { } y && ArchiveFormat.FormatLocal(x) == ArchiveFormat.FormatLocal(y));

        private static bool SameUtc(DateTime? e, DateTime? a) =>
            (e is null && a is null)
            || (e is { } x && a is { } y && ArchiveFormat.FormatUtc(x) == ArchiveFormat.FormatUtc(y));

        private static void CompareParticipants(
            IReadOnlyList<ArchiveParticipant> expected, IReadOnlyList<ArchiveParticipant> actual, Recorder record)
        {
            var actualByID = actual.ToDictionary(p => p.ParticipantID);
            var expectedIDs = expected.Select(p => p.ParticipantID).ToHashSet();

            var differ = expected.Count(e =>
                !actualByID.TryGetValue(e.ParticipantID, out var a)
                || a.DisplayName != e.DisplayName
                || a.IsLocal != e.IsLocal);

            record.AddCount(ArchiveMismatchKind.ParticipantDiffers, differ + actual.Count(a => !expectedIDs.Contains(a.ParticipantID)));
        }

        private static void CompareMessage(ArchiveMessage expected, ArchiveMessage actual, Recorder record)
        {
            var sequence = actual.SequenceNumber;

            if (expected.SequenceNumber != actual.SequenceNumber)
            {
                record.Add(ArchiveMismatchKind.MessageSequenceDiffers, sequence);
            }

            // In the stored form, which is what the archive can hold: to the second, no zone.
            if (ArchiveFormat.FormatLocal(expected.DateTimeLocal) != ArchiveFormat.FormatLocal(actual.DateTimeLocal))
            {
                record.Add(ArchiveMismatchKind.MessageTimeDiffers, sequence);
            }

            if (expected.Kind != actual.Kind)
            {
                record.Add(ArchiveMismatchKind.MessageKindDiffers, sequence);
            }

            if (expected.SenderParticipantID != actual.SenderParticipantID)
            {
                record.Add(ArchiveMismatchKind.MessageSenderDiffers, sequence);
            }

            if (!string.Equals(expected.Text, actual.Text, StringComparison.Ordinal))
            {
                record.Add(ArchiveMismatchKind.MessageTextDiffers, sequence);
            }

            if (expected.IsForwarded != actual.IsForwarded || expected.ForwardScore != actual.ForwardScore)
            {
                record.Add(ArchiveMismatchKind.MessageForwardedDiffers, sequence);
            }

            if (expected.Source != actual.Source)
            {
                record.Add(ArchiveMismatchKind.MessageSourceDiffers, sequence);
            }
        }

        private static void CompareAttachment(
            ArchiveAttachment expected, ArchiveAttachment actual, int sequence, Recorder record)
        {
            var id = actual.AttachmentID;

            if (expected.MessageID != actual.MessageID || expected.Ordinal != actual.Ordinal)
            {
                record.Add(ArchiveMismatchKind.AttachmentPlacementDiffers, sequence, id);
            }

            if (expected.State != actual.State)
            {
                record.Add(ArchiveMismatchKind.AttachmentStateDiffers, sequence, id);
            }

            // An older export's restored file is named by its line, so its name is known and compared;
            // a workspace file's name follows the copy the export took, and is not.
            if (!string.Equals(expected.MediaSha256, actual.MediaSha256, StringComparison.Ordinal)
                || (expected.OriginalFileName is not null
                    && !string.Equals(expected.OriginalFileName, actual.OriginalFileName, StringComparison.Ordinal)))
            {
                record.Add(ArchiveMismatchKind.AttachmentMediaDiffers, sequence, id);
            }

            if (expected.IsForwarded != actual.IsForwarded || expected.ForwardScore != actual.ForwardScore)
            {
                record.Add(ArchiveMismatchKind.AttachmentForwardedDiffers, sequence, id);
            }

            if (expected.LowQualityCopyOfAttachmentID != actual.LowQualityCopyOfAttachmentID)
            {
                record.Add(ArchiveMismatchKind.AttachmentLowQualityLinkDiffers, sequence, id);
            }

            if (!SameBasis(expected.Basis, actual.Basis))
            {
                record.Add(ArchiveMismatchKind.AttachmentBasisDiffers, sequence, id);
            }
        }

        /// <summary>Equal as stored: the confirmation time to the second, in UTC.</summary>
        private static bool SameBasis(ArchiveAttachmentBasis? expected, ArchiveAttachmentBasis? actual) =>
            (expected, actual) switch
            {
                (null, null) => true,
                ({ } e, { } a) => e.BasisType == a.BasisType
                                  && e.BasisText == a.BasisText
                                  && e.Actor == a.Actor
                                  && ArchiveFormat.FormatUtc(e.ConfirmedUtc) == ArchiveFormat.FormatUtc(a.ConfirmedUtc),
                _ => false,
            };

        /// <summary>
        /// Each payload the workspace's resolved attachments name, against the Media rows the archive's
        /// attachments name. A payload on one side only is a difference too.
        /// </summary>
        private static void CompareMedia(
            Dictionary<string, ArchiveMedia> expected, Dictionary<string, ArchiveMedia> actual, Recorder record)
        {
            var differ = expected.Values.Count(e =>
                !actual.TryGetValue(e.Sha256, out var a)
                || a.MediaType != e.MediaType
                || a.SizeBytes != e.SizeBytes
                || a.DurationMS != e.DurationMS
                || a.Width != e.Width
                || a.Height != e.Height);

            record.AddCount(ArchiveMismatchKind.MediaDetailsDiffer, differ + actual.Keys.Count(sha => !expected.ContainsKey(sha)));
        }

        // -------------------------------------------------------------------------------------
        // Collecting the result.
        // -------------------------------------------------------------------------------------

        private sealed class Recorder
        {
            private readonly Dictionary<ArchiveMismatchKind, int> _counts =
                Enum.GetValues<ArchiveMismatchKind>().ToDictionary(kind => kind, _ => 0);

            private readonly List<ArchiveMismatch> _first = [];

            /// <summary>Mismatches counted without an identifier.</summary>
            internal void AddCount(ArchiveMismatchKind kind, int count)
            {
                _counts[kind] += count;

                for (var index = 0; index < count && _first.Count < MaximumListedMismatches; index++)
                {
                    _first.Add(new ArchiveMismatch(kind, null, null));
                }
            }

            internal void Add(ArchiveMismatchKind kind, int? sequenceNumber, long? attachmentID = null)
            {
                _counts[kind]++;

                if (_first.Count < MaximumListedMismatches)
                {
                    _first.Add(new ArchiveMismatch(kind, sequenceNumber, attachmentID));
                }
            }

            internal ArchiveComparisonResult Result(
                int participants,
                int messages,
                int attachments,
                int restored,
                int missing,
                int unmatched,
                int workspaceMessages,
                int exportMessages,
                IReadOnlyList<ArchiveComparisonExportCounts> exports,
                IReadOnlyList<ArchiveCollectionCounts> collections,
                int definitions,
                int notedDates) => new()
            {
                ParticipantsCompared = participants,
                MessagesCompared = messages,
                AttachmentsCompared = attachments,
                Restored = restored,
                Missing = missing,
                Unmatched = unmatched,
                MismatchCounts = new Dictionary<ArchiveMismatchKind, int>(_counts),
                FirstMismatches = _first.ToList(),
                WorkspaceMessages = workspaceMessages,
                ExportMessages = exportMessages,
                Exports = exports,
                Collections = collections,
                ItemDefinitions = definitions,
                NotedDates = notedDates,
            };
        }
    }

    /// <summary>Which archive to compare with which workspace conversation.</summary>
    public sealed class ArchiveComparisonRequest
    {
        /// <summary>The workspace database. It is opened read-only.</summary>
        public required string WorkspacePath { get; init; }

        /// <summary>The conversation the archive was exported from.</summary>
        public required long ConversationID { get; init; }

        /// <summary>The participant whose side of the conversation is "me", as given to the export.</summary>
        public required long LocalParticipantID { get; init; }

        /// <summary>The archive folder. It is opened read-only.</summary>
        public required string ArchivePath { get; init; }
    }

    /// <summary>A kind of difference between an archive and the workspace.</summary>
    public enum ArchiveMismatchKind
    {
        /// <summary>One of the title, source export hash, time zone or local participant differs.</summary>
        ArchiveInfoDiffers,

        /// <summary>A participant is on one side only, or its name or local flag differs.</summary>
        ParticipantDiffers,

        /// <summary>A workspace message is not in the archive.</summary>
        MessageMissingFromArchive,

        /// <summary>An archive message is not in the workspace conversation.</summary>
        MessageNotInWorkspace,

        /// <summary>A message's sequence number differs.</summary>
        MessageSequenceDiffers,

        /// <summary>A message's local time differs.</summary>
        MessageTimeDiffers,

        /// <summary>A message's kind (user or system) differs.</summary>
        MessageKindDiffers,

        /// <summary>A message's sender differs.</summary>
        MessageSenderDiffers,

        /// <summary>A message's text differs.</summary>
        MessageTextDiffers,

        /// <summary>A message's forwarded status or score differs.</summary>
        MessageForwardedDiffers,

        /// <summary>A workspace attachment is not in the archive.</summary>
        AttachmentMissingFromArchive,

        /// <summary>An archive attachment is not in the workspace conversation.</summary>
        AttachmentNotInWorkspace,

        /// <summary>An attachment's message or ordinal differs.</summary>
        AttachmentPlacementDiffers,

        /// <summary>An attachment's state (Restored, Missing, Unmatched) differs.</summary>
        AttachmentStateDiffers,

        /// <summary>An attachment's media hash differs.</summary>
        AttachmentMediaDiffers,

        /// <summary>An attachment's forwarded status or score differs.</summary>
        AttachmentForwardedDiffers,

        /// <summary>An attachment's low-quality link differs.</summary>
        AttachmentLowQualityLinkDiffers,

        /// <summary>An attachment's basis line differs, or is on one side only.</summary>
        AttachmentBasisDiffers,

        /// <summary>A payload's recorded description differs, or the payload is on one side only.</summary>
        MediaDetailsDiffer,

        /// <summary>Version 2: a message's source (workspace or older export) differs.</summary>
        MessageSourceDiffers,

        /// <summary>
        /// Version 2: the archive's messages of one source are not as many as that source file shows
        /// (the workspace conversation, or the shown part of the older exports).
        /// </summary>
        SourceCountDiffers,

        /// <summary>Version 2: a collection is on one side only, or differs.</summary>
        CollectionDiffers,

        /// <summary>Version 2: an entry is on one side only, or it, its periods or its items differ.</summary>
        EntryDiffers,

        /// <summary>Version 2: a link is on one side only, or differs (a message link as re-mapped).</summary>
        EntryLinkDiffers,

        /// <summary>Version 2: the entries, periods or items per collection differ from the memories file's.</summary>
        CollectionCountsDiffer,

        /// <summary>Version 2: a used item definition is on one side only, or differs.</summary>
        ItemDefinitionDiffers,

        /// <summary>Version 2 (CH-47): a noted date is on one side only (missing from the archive, or extra in it), or a field differs.</summary>
        NotedDateDiffers,
    }

    /// <summary>One older export's messages as the older-exports file holds them. Counts only.</summary>
    /// <param name="Position">Its place in the timeline of older exports.</param>
    /// <param name="Shown">Whether it is shown.</param>
    /// <param name="Messages">Its messages.</param>
    /// <param name="ShownMessages">Messages in the archive: from its cut on, none when not shown.</param>
    /// <param name="HiddenMessages">Messages kept as evidence only.</param>
    public sealed record ArchiveComparisonExportCounts(
        int Position, bool Shown, int Messages, int ShownMessages, int HiddenMessages);

    /// <summary>
    /// One listed mismatch: its kind, and at most the sequence number of its message and the
    /// identifier of its attachment. Never text, a name or a path.
    /// </summary>
    /// <param name="Kind">What differs.</param>
    /// <param name="SequenceNumber">The message involved, when there is one.</param>
    /// <param name="AttachmentID">The attachment involved, when there is one.</param>
    public sealed record ArchiveMismatch(ArchiveMismatchKind Kind, int? SequenceNumber, long? AttachmentID);

    /// <summary>The outcome of <see cref="ArchiveWorkspaceComparer.Compare"/>. Counts and identifiers only.</summary>
    public sealed class ArchiveComparisonResult
    {
        /// <summary>The workspace conversation's participants.</summary>
        public required int ParticipantsCompared { get; init; }

        /// <summary>The workspace conversation's messages.</summary>
        public required int MessagesCompared { get; init; }

        /// <summary>The workspace conversation's attachments.</summary>
        public required int AttachmentsCompared { get; init; }

        /// <summary>Attachments the workspace now has resolved.</summary>
        public required int Restored { get; init; }

        /// <summary>
        /// Unresolved attachments the workspace now has reviewed as missing (a current "reviewed,
        /// insufficient evidence" review).
        /// </summary>
        public required int Missing { get; init; }

        /// <summary>Every other unresolved attachment.</summary>
        public required int Unmatched { get; init; }

        /// <summary>How many mismatches of each kind; every kind is present, most with zero.</summary>
        public required IReadOnlyDictionary<ArchiveMismatchKind, int> MismatchCounts { get; init; }

        /// <summary>The first <see cref="ArchiveWorkspaceComparer.MaximumListedMismatches"/> mismatches found.</summary>
        public required IReadOnlyList<ArchiveMismatch> FirstMismatches { get; init; }

        /// <summary>Version 2: the workspace conversation's messages.</summary>
        public int WorkspaceMessages { get; init; }

        /// <summary>Version 2: the older exports' shown messages, all exports together.</summary>
        public int ExportMessages { get; init; }

        /// <summary>Version 2: each older export's shown and hidden messages, in position order. Empty with no older-exports file.</summary>
        public IReadOnlyList<ArchiveComparisonExportCounts> Exports { get; init; } = [];

        /// <summary>Version 2: entries, periods and items per collection in the memories file. Empty with no memories file.</summary>
        public IReadOnlyList<ArchiveCollectionCounts> Collections { get; init; } = [];

        /// <summary>Version 2: item definitions some item of the memories file uses. 0 with no memories file or a format 1 one.</summary>
        public int ItemDefinitions { get; init; }

        /// <summary>Version 2 (CH-47): noted dates in the memories file. 0 with no memories file or one before format 4.</summary>
        public int NotedDates { get; init; }

        /// <summary>The archive says exactly what the workspace says.</summary>
        public bool Passed => MismatchCounts.Values.All(count => count == 0);
    }

    /// <summary>
    /// The comparison could not be made: the workspace or the archive could not be read. The message
    /// never carries a path.
    /// </summary>
    public sealed class ArchiveComparisonRefusedException : Exception
    {
        /// <summary>A refusal with a reason to show.</summary>
        public ArchiveComparisonRefusedException(string message)
            : base(message)
        {
        }

        /// <summary>A refusal with a reason to show, and the fault that caused it.</summary>
        public ArchiveComparisonRefusedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
