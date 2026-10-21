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

            ArchiveExportSource source;

            try
            {
                source = ArchiveExportSource.Read(
                    request.WorkspacePath, request.ConversationID, request.LocalParticipantID, PageSize, progress: null);
            }
            catch (ArchiveExportRefusedException exception)
            {
                // The source's refusals are fixed phrases naming the request's options, never a path.
                throw new ArchiveComparisonRefusedException(
                    "The workspace could not be read: " + exception.Message, exception);
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

        private static ArchiveComparisonResult Compare(ArchiveExportSource source, long localParticipantID, ArchiveReader reader)
        {
            var record = new Recorder();

            CompareInfo(source, localParticipantID, reader.Info, record);
            CompareParticipants(source.ToArchiveParticipants(localParticipantID).ToList(), reader.ReadParticipants(), record);

            // The expected rows, built exactly as the exporter builds them. No original file name: a
            // comparison copies nothing, so it neither knows nor compares one.
            var linksOutside = 0;
            var expectedMessages = new Dictionary<long, ArchiveMessage>(source.Messages.Count);
            var expectedAttachments = new Dictionary<long, ArchiveAttachment>();

            foreach (var message in source.Messages)
            {
                var expected = source.ToArchiveMessage(message, static _ => null, ref linksOutside);
                expectedMessages.Add(expected.MessageID, expected);

                foreach (var attachment in expected.Attachments)
                {
                    expectedAttachments.Add(attachment.AttachmentID, attachment);
                }
            }

            var expectedMedia = source.ToArchiveMedia().ToDictionary(m => m.Sha256, StringComparer.Ordinal);
            var actualMedia = new Dictionary<string, ArchiveMedia>(StringComparer.Ordinal);

            var seenMessages = new HashSet<long>();
            var seenAttachments = new HashSet<long>();

            var page = reader.ReadFirstPage(ArchiveReader.MaximumPageSize);

            while (true)
            {
                foreach (var actual in page.Messages)
                {
                    seenMessages.Add(actual.MessageID);

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

                if (!page.HasLater || page.LastSequenceNumber is not { } last)
                {
                    break;
                }

                page = reader.ReadPageFrom(last + 1, ArchiveReader.MaximumPageSize);
            }

            foreach (var expected in source.Messages.Where(m => !seenMessages.Contains(m.MessageID)))
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

            CompareMedia(expectedMedia, actualMedia, record);

            return record.Result(
                participants: source.Participants.Count,
                messages: source.Messages.Count,
                attachments: expectedAttachments.Count,
                restored: source.Resolved,
                missing: source.MissingReviewed,
                unmatched: source.Unmatched);
        }

        // -------------------------------------------------------------------------------------
        // Field by field.
        // -------------------------------------------------------------------------------------

        private static void CompareInfo(ArchiveExportSource source, long localParticipantID, ArchiveInfo info, Recorder record)
        {
            var same = new[]
            {
                info.ConversationTitle == source.ConversationTitle,
                info.SourceExportSha256 == source.SourceExportSha256,
                info.TimeZoneID == source.TimeZoneID,
                info.LocalParticipantID == localParticipantID,
            };

            record.AddCount(ArchiveMismatchKind.ArchiveInfoDiffers, same.Count(s => !s));
        }

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

            if (!string.Equals(expected.MediaSha256, actual.MediaSha256, StringComparison.Ordinal))
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
                int participants, int messages, int attachments, int restored, int missing, int unmatched) => new()
            {
                ParticipantsCompared = participants,
                MessagesCompared = messages,
                AttachmentsCompared = attachments,
                Restored = restored,
                Missing = missing,
                Unmatched = unmatched,
                MismatchCounts = new Dictionary<ArchiveMismatchKind, int>(_counts),
                FirstMismatches = _first.ToList(),
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
    }

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
