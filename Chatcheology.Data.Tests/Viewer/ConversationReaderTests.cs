using Chatcheology.Core.Models;
using Chatcheology.Data.Viewer;

namespace Chatcheology.Data.Tests.Viewer
{
    /// <summary>
    /// What the conversation reader returns: order, scoping, page bounds and attachment placement.
    /// </summary>
    public class ConversationReaderTests
    {
        private static readonly DateOnly Day = new(2026, 4, 6);

        // -----------------------------------------------------------------------------------
        // Order and scoping.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void ReadFirstPage_ReturnsMessagesInSequenceNumberOrder()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 5, Day);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader()
                .ReadFirstPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            Assert.Equal(
                [1, 2, 3, 4, 5],
                page.Messages.Select(message => message.SequenceNumber));
        }

        /// <remarks>
        /// The stored timestamps deliberately fall the other way round. Order is the sequence
        /// number's to state, and a reader that sorted by the timestamp would pass every test where
        /// the two happen to agree and quietly reorder the archive where they do not.
        /// </remarks>
        [Fact]
        public void ReadFirstPage_WithTimestampsThatDisagreeWithSequence_StillOrdersBySequence()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessageWithStoredTimestamp("2026-04-06T18:00:00", "First");
            workspace.AddMessageWithStoredTimestamp("2026-04-06T09:00:00", "Second");
            workspace.AddMessageWithStoredTimestamp("2026-04-06T12:00:00", "Third");

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader()
                .ReadFirstPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            Assert.Equal(
                ["First", "Second", "Third"],
                page.Messages.Select(message => message.MessageContent));
        }

        [Fact]
        public void ReadFirstPage_ReturnsOnlyTheRequestedConversation()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 3, Day);
            workspace.AddMessages(count: 4, Day, ViewerTestWorkspace.OtherConversationID);

            using var connection = workspace.OpenReadOnly();
            var reader = new ConversationReader();

            var first = reader.ReadFirstPage(
                connection, ViewerTestWorkspace.ConversationID, pageSize: 50);

            var second = reader.ReadFirstPage(
                connection, ViewerTestWorkspace.OtherConversationID, pageSize: 50);

            Assert.All(
                first.Messages,
                message => Assert.Equal(ViewerTestWorkspace.ConversationID, message.ConversationID));

            Assert.Equal(3, first.Messages.Count);
            Assert.Equal(4, second.Messages.Count);
        }

        [Fact]
        public void ReadFirstPage_ForAConversationThatDoesNotExist_IsRefused()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 1, Day);

            using var connection = workspace.OpenReadOnly();

            Assert.Throws<InvalidOperationException>(
                () => new ConversationReader().ReadFirstPage(
                    connection, conversationID: 9_999, pageSize: 10));
        }

        // -----------------------------------------------------------------------------------
        // Paging.
        // -----------------------------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(ConversationReader.MaximumPageSize + 1)]
        public void ReadFirstPage_WithAPageSizeOutsideTheBounds_IsRefused(int pageSize)
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 1, Day);

            using var connection = workspace.OpenReadOnly();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ConversationReader().ReadFirstPage(
                    connection, ViewerTestWorkspace.ConversationID, pageSize));
        }

        [Fact]
        public void ForwardPaging_AcrossThreePages_HasNoDuplicateAndNoGap()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 25, Day);

            using var connection = workspace.OpenReadOnly();
            var reader = new ConversationReader();

            var seen = new List<int>();

            var page = reader.ReadFirstPage(
                connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            while (true)
            {
                seen.AddRange(page.Messages.Select(message => message.SequenceNumber));

                if (!page.HasLater)
                {
                    break;
                }

                page = reader.ReadPageFrom(
                    connection,
                    ViewerTestWorkspace.ConversationID,
                    page.LastSequenceNumber!.Value + 1,
                    pageSize: 10);
            }

            Assert.Equal(Enumerable.Range(1, 25), seen);
        }

        [Fact]
        public void ReadPageBefore_ReturnsThePrecedingWindowAscending()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 25, Day);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader().ReadPageBefore(
                connection, ViewerTestWorkspace.ConversationID, beforeSequenceNumber: 21, pageSize: 10);

            Assert.Equal(
                Enumerable.Range(11, 10),
                page.Messages.Select(message => message.SequenceNumber));

            Assert.Equal(11, page.FirstSequenceNumber);
            Assert.Equal(20, page.LastSequenceNumber);
        }

        [Fact]
        public void ReadPageBefore_AtTheStart_ReturnsWhatThereIs()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 25, Day);

            using var connection = workspace.OpenReadOnly();
            var reader = new ConversationReader();

            var page = reader.ReadPageBefore(
                connection, ViewerTestWorkspace.ConversationID, beforeSequenceNumber: 4, pageSize: 10);

            Assert.Equal([1, 2, 3], page.Messages.Select(message => message.SequenceNumber));
            Assert.False(page.HasEarlier);
            Assert.True(page.HasLater);

            var none = reader.ReadPageBefore(
                connection, ViewerTestWorkspace.ConversationID, beforeSequenceNumber: 1, pageSize: 10);

            Assert.Empty(none.Messages);
            Assert.Null(none.FirstSequenceNumber);
            Assert.False(none.HasEarlier);
            Assert.False(none.HasLater);
        }

        [Fact]
        public void ReadLastPage_ReturnsTheFinalWindowAscending()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 25, Day);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader()
                .ReadLastPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            Assert.Equal(
                Enumerable.Range(16, 10),
                page.Messages.Select(message => message.SequenceNumber));

            Assert.True(page.HasEarlier);
            Assert.False(page.HasLater);
        }

        [Fact]
        public void FirstAndLastPage_ReportTheirNeighboursCorrectly()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 12, Day);

            using var connection = workspace.OpenReadOnly();
            var reader = new ConversationReader();

            var first = reader.ReadFirstPage(
                connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            Assert.False(first.HasEarlier);
            Assert.True(first.HasLater);

            var whole = reader.ReadFirstPage(
                connection, ViewerTestWorkspace.ConversationID, pageSize: 50);

            Assert.False(whole.HasEarlier);
            Assert.False(whole.HasLater);
        }

        [Fact]
        public void ReadFirstPage_OfAConversationWithNoMessages_IsEmptyRatherThanRefused()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 2, Day);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader().ReadFirstPage(
                connection, ViewerTestWorkspace.OtherConversationID, pageSize: 10);

            Assert.Empty(page.Messages);
            Assert.Null(page.FirstSequenceNumber);
            Assert.Null(page.LastSequenceNumber);
        }

        [Fact]
        public void ReadPageFrom_AtASequenceNumberNoMessageUses_StartsAtTheNextMessage()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 6, Day);

            using var connection = workspace.OpenReadOnly();

            // Sequence 4 exists here; asking from it is the ordinary resume case, and asking from
            // a number between two messages must not be an error either.
            var page = new ConversationReader().ReadPageFrom(
                connection, ViewerTestWorkspace.ConversationID, startSequenceNumber: 4, pageSize: 10);

            Assert.Equal([4, 5, 6], page.Messages.Select(message => message.SequenceNumber));
        }

        // -----------------------------------------------------------------------------------
        // Senders.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void ReadFirstPage_JoinsTheSenderDisplayName()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessage("Hello", Day, senderParticipantID: ViewerTestWorkspace.FirstParticipantID);
            workspace.AddMessage("Hi", Day, "09:01:00", ViewerTestWorkspace.SecondParticipantID);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader()
                .ReadFirstPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            var names = page.Messages.Select(message => message.SenderDisplayName).ToList();

            Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
            Assert.Equal(2, names.Distinct().Count());

            Assert.Equal(
                [ViewerTestWorkspace.FirstParticipantID, ViewerTestWorkspace.SecondParticipantID],
                page.Messages.Select(message => message.SenderParticipantID));
        }

        [Fact]
        public void ReadFirstPage_KeepsASenderlessSystemMessage()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessage("Hello", Day);
            workspace.AddSystemMessage("Messages are encrypted", Day, "09:01:00");

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader()
                .ReadFirstPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            var system = page.Messages.Single(
                message => message.MessageType == MessageType.System);

            Assert.Null(system.SenderParticipantID);
            Assert.Null(system.SenderDisplayName);
            Assert.Equal("Messages are encrypted", system.MessageContent);

            Assert.Equal(
                MessageType.User,
                page.Messages.Single(m => m.SequenceNumber == 1).MessageType);
        }

        // -----------------------------------------------------------------------------------
        // Attachments.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void ReadFirstPage_PlacesAttachmentsOnTheirOwnMessage()
        {
            using var workspace = new ViewerTestWorkspace();
            var plain = workspace.AddMessage("Just text", Day);
            var carrying = workspace.AddMessage("<Media omitted>", Day, "09:01:00");
            workspace.AddMessage("More text", Day, "09:02:00");

            var attachments = workspace.AddAttachments(carrying, count: 1);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader()
                .ReadFirstPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            Assert.Empty(page.Messages.Single(m => m.MessageID == plain).Attachments);

            var carried = page.Messages.Single(m => m.MessageID == carrying).Attachments;

            Assert.Equal(attachments[0], Assert.Single(carried).AttachmentID);
            Assert.Equal(1, carried[0].Ordinal);
        }

        /// <remarks>
        /// The schema permits several attachments on one message, so the reader must carry the
        /// ordinal rather than assume the importer's current one-per-placeholder behaviour.
        /// </remarks>
        [Fact]
        public void ReadFirstPage_KeepsEveryAttachmentOfAMessageInOrdinalOrder()
        {
            using var workspace = new ViewerTestWorkspace();
            var carrying = workspace.AddMessage("<Media omitted>", Day);
            var attachments = workspace.AddAttachments(carrying, count: 3);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader()
                .ReadFirstPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            var carried = page.Messages.Single(m => m.MessageID == carrying).Attachments;

            Assert.Equal([1, 2, 3], carried.Select(attachment => attachment.Ordinal));
            Assert.Equal(attachments, carried.Select(attachment => attachment.AttachmentID));
        }

        [Fact]
        public void ReadFirstPage_DoesNotCarryAttachmentsFromOutsideThePage()
        {
            using var workspace = new ViewerTestWorkspace();
            var early = workspace.AddMessage("<Media omitted>", Day);
            workspace.AddAttachments(early, count: 1);

            workspace.AddMessages(count: 8, Day);

            var late = workspace.AddMessage("<Media omitted>", Day, "10:00:00");
            workspace.AddAttachments(late, count: 1);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader().ReadPageFrom(
                connection, ViewerTestWorkspace.ConversationID, startSequenceNumber: 2, pageSize: 5);

            Assert.All(page.Messages, message => Assert.Empty(message.Attachments));
        }

        // -----------------------------------------------------------------------------------
        // Summaries.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void ReadConversations_ReportsCountsBoundsAndParticipants()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessage("First", Day);
            workspace.AddMessages(count: 4, Day.AddDays(1));
            workspace.AddMessage("Last", Day.AddDays(2), "23:59:00");

            using var connection = workspace.OpenReadOnly();

            var summaries = new ConversationReader().ReadConversations(connection);

            var conversation = summaries.Single(
                summary => summary.ConversationID == ViewerTestWorkspace.ConversationID);

            Assert.Equal(6, conversation.MessageCount);
            Assert.Equal(1, conversation.FirstSequenceNumber);
            Assert.Equal(6, conversation.LastSequenceNumber);
            Assert.Equal(Day, DateOnly.FromDateTime(conversation.FirstMessageDateTimeLocal!.Value));

            Assert.Equal(
                Day.AddDays(2),
                DateOnly.FromDateTime(conversation.LastMessageDateTimeLocal!.Value));

            Assert.Equal(
                [ViewerTestWorkspace.FirstParticipantID, ViewerTestWorkspace.SecondParticipantID],
                conversation.Participants.Select(participant => participant.ParticipantID));

            Assert.False(string.IsNullOrWhiteSpace(conversation.Participants[0].DisplayName));
        }

        /// <remarks>
        /// The boundary dates are the messages at the smallest and largest sequence number, not the
        /// smallest and largest stored timestamp. Here the two disagree, and the summary follows
        /// conversation order because that is what the viewer displays.
        /// </remarks>
        [Fact]
        public void ReadConversations_TakesBoundaryDatesFromTheSequenceExtremes()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessageWithStoredTimestamp("2026-04-06T12:00:00");
            workspace.AddMessageWithStoredTimestamp("2026-01-01T00:00:00");
            workspace.AddMessageWithStoredTimestamp("2026-04-07T12:00:00");
            workspace.AddMessageWithStoredTimestamp("2026-12-31T23:59:59");
            workspace.AddMessageWithStoredTimestamp("2026-04-08T12:00:00");

            using var connection = workspace.OpenReadOnly();

            var conversation = new ConversationReader()
                .ReadConversations(connection)
                .Single(summary => summary.ConversationID == ViewerTestWorkspace.ConversationID);

            Assert.Equal(
                new DateTime(2026, 4, 6, 12, 0, 0),
                conversation.FirstMessageDateTimeLocal);

            Assert.Equal(
                new DateTime(2026, 4, 8, 12, 0, 0),
                conversation.LastMessageDateTimeLocal);
        }

        [Fact]
        public void ReadConversations_ListsAConversationWithNoMessages()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 2, Day);

            using var connection = workspace.OpenReadOnly();

            var empty = new ConversationReader()
                .ReadConversations(connection)
                .Single(summary => summary.ConversationID == ViewerTestWorkspace.OtherConversationID);

            Assert.Equal(0, empty.MessageCount);
            Assert.Null(empty.FirstSequenceNumber);
            Assert.Null(empty.LastSequenceNumber);
            Assert.Null(empty.FirstMessageDateTimeLocal);
            Assert.Null(empty.LastMessageDateTimeLocal);
            Assert.False(string.IsNullOrWhiteSpace(empty.Title));
        }

        /// <remarks>
        /// The reader is handed a conversation; it never assumes there is exactly one, or which one
        /// it is.
        /// </remarks>
        [Fact]
        public void ReadConversations_ReturnsEveryConversationInTheWorkspace()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 2, Day);
            workspace.AddMessages(count: 2, Day, ViewerTestWorkspace.OtherConversationID);

            using var connection = workspace.OpenReadOnly();

            var summaries = new ConversationReader().ReadConversations(connection);

            Assert.Equal(2, summaries.Count);

            Assert.Equal(
                summaries.Select(summary => summary.ConversationID).Order(),
                summaries.Select(summary => summary.ConversationID));
        }

        // -----------------------------------------------------------------------------------
        // Stored timestamps.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void ReadFirstPage_ReturnsTheStoredWallClockReadingUnshifted()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessageWithStoredTimestamp("2026-04-06T20:39:07");

            using var connection = workspace.OpenReadOnly();

            var message = new ConversationReader()
                .ReadFirstPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10)
                .Messages
                .Single();

            Assert.Equal(new DateTime(2026, 4, 6, 20, 39, 7), message.MessageDateTimeLocal);
            Assert.Equal(DateTimeKind.Unspecified, message.MessageDateTimeLocal.Kind);
        }

        /// <remarks>
        /// A timestamp in a format this workspace does not write is refused rather than guessed at,
        /// for the reason the format's owner gives: a date read the wrong way is a plausible wrong
        /// day rather than an obvious failure.
        /// </remarks>
        [Fact]
        public void ReadFirstPage_WithATimestampInAnotherFormat_IsRefused()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessageWithStoredTimestamp("06/04/2026 20:39");

            using var connection = workspace.OpenReadOnly();

            Assert.Throws<InvalidOperationException>(
                () => new ConversationReader().ReadFirstPage(
                    connection, ViewerTestWorkspace.ConversationID, pageSize: 10));
        }
    }
}
