using Chatcheology.Data.Viewer;

namespace Chatcheology.Data.Tests.Viewer
{
    /// <summary>
    /// Where jump-to-date lands, at every boundary and where the stored timestamps do not rise with
    /// the conversation order.
    /// </summary>
    /// <remarks>
    /// This is navigation. Nothing here establishes anything about a message, and no result is a
    /// matching input.
    /// </remarks>
    public class ConversationJumpToDateTests
    {
        private static readonly DateOnly First = new(2026, 4, 6);

        [Fact]
        public void OnADateAMessageFallsOn_ReturnsThatMessagesSequenceNumber()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessage("One", First);
            workspace.AddMessage("Two", First.AddDays(1));
            workspace.AddMessage("Three", First.AddDays(2));

            using var connection = workspace.OpenReadOnly();

            Assert.Equal(
                2,
                new ConversationReader().FindFirstSequenceNumberOnOrAfter(
                    connection, ViewerTestWorkspace.ConversationID, First.AddDays(1)));
        }

        [Fact]
        public void OnADateBetweenTwoMessages_ReturnsTheFollowingMessage()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessage("One", First);
            workspace.AddMessage("Two", First.AddDays(10));

            using var connection = workspace.OpenReadOnly();

            Assert.Equal(
                2,
                new ConversationReader().FindFirstSequenceNumberOnOrAfter(
                    connection, ViewerTestWorkspace.ConversationID, First.AddDays(5)));
        }

        [Fact]
        public void BeforeTheFirstMessage_ReturnsTheFirstMessage()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 5, First);

            using var connection = workspace.OpenReadOnly();

            Assert.Equal(
                1,
                new ConversationReader().FindFirstSequenceNumberOnOrAfter(
                    connection, ViewerTestWorkspace.ConversationID, First.AddDays(-30)));
        }

        [Fact]
        public void AfterTheLastMessage_ReturnsNothing()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 5, First);

            using var connection = workspace.OpenReadOnly();

            Assert.Null(
                new ConversationReader().FindFirstSequenceNumberOnOrAfter(
                    connection, ViewerTestWorkspace.ConversationID, First.AddDays(1)));
        }

        /// <remarks>
        /// The threshold is that date at local midnight, so a message at 00:00:00 on the requested
        /// day qualifies and the last message of the previous day does not. The stored form is
        /// fixed width, which is what makes the comparison exact rather than approximately right.
        /// </remarks>
        [Fact]
        public void AtMidnight_IncludesTheFirstInstantOfTheRequestedDay()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessageWithStoredTimestamp("2026-04-06T23:59:59", "Previous day");
            workspace.AddMessageWithStoredTimestamp("2026-04-07T00:00:00", "First instant");
            workspace.AddMessageWithStoredTimestamp("2026-04-07T00:00:01", "One second later");

            using var connection = workspace.OpenReadOnly();

            Assert.Equal(
                2,
                new ConversationReader().FindFirstSequenceNumberOnOrAfter(
                    connection, ViewerTestWorkspace.ConversationID, new DateOnly(2026, 4, 7)));
        }

        /// <remarks>
        /// <strong>The case a binary search would get wrong.</strong> The stored timestamps here do
        /// not rise with the sequence number, which the schema never promised they would. The
        /// answer is the smallest sequence number among the qualifying messages — sequence 2 — and
        /// a search that assumed sortedness would land somewhere later and look plausible doing it.
        /// </remarks>
        [Fact]
        public void WithTimestampsThatDoNotRiseWithSequence_ReturnsTheSmallestQualifyingSequence()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessageWithStoredTimestamp("2026-01-01T09:00:00", "Sequence one");
            workspace.AddMessageWithStoredTimestamp("2026-06-01T09:00:00", "Sequence two");
            workspace.AddMessageWithStoredTimestamp("2026-02-01T09:00:00", "Sequence three");
            workspace.AddMessageWithStoredTimestamp("2026-05-01T09:00:00", "Sequence four");
            workspace.AddMessageWithStoredTimestamp("2026-03-01T09:00:00", "Sequence five");

            using var connection = workspace.OpenReadOnly();

            Assert.Equal(
                2,
                new ConversationReader().FindFirstSequenceNumberOnOrAfter(
                    connection, ViewerTestWorkspace.ConversationID, new DateOnly(2026, 4, 1)));
        }

        [Fact]
        public void IsScopedToItsOwnConversation()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessage("Only conversation one", First);
            workspace.AddMessage(
                "Only conversation two",
                First.AddDays(30),
                senderParticipantID: ViewerTestWorkspace.OtherConversationParticipantID,
                conversationID: ViewerTestWorkspace.OtherConversationID);

            using var connection = workspace.OpenReadOnly();
            var reader = new ConversationReader();

            Assert.Null(
                reader.FindFirstSequenceNumberOnOrAfter(
                    connection, ViewerTestWorkspace.ConversationID, First.AddDays(20)));

            Assert.Equal(
                2,
                reader.FindFirstSequenceNumberOnOrAfter(
                    connection, ViewerTestWorkspace.OtherConversationID, First.AddDays(20)));
        }

        [Fact]
        public void ForAConversationThatDoesNotExist_IsRefused()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 1, First);

            using var connection = workspace.OpenReadOnly();

            Assert.Throws<InvalidOperationException>(
                () => new ConversationReader().FindFirstSequenceNumberOnOrAfter(
                    connection, conversationID: 9_999, First));
        }

        /// <remarks>
        /// The whole point of the call: the sequence number it returns is a page start.
        /// </remarks>
        [Fact]
        public void TheReturnedSequenceNumber_OpensThePageBeginningAtThatMessage()
        {
            using var workspace = new ViewerTestWorkspace();

            for (var day = 0; day < 12; day++)
            {
                workspace.AddMessage($"Day {day}", First.AddDays(day));
            }

            using var connection = workspace.OpenReadOnly();
            var reader = new ConversationReader();

            var start = reader.FindFirstSequenceNumberOnOrAfter(
                connection, ViewerTestWorkspace.ConversationID, First.AddDays(7));

            var page = reader.ReadPageFrom(
                connection, ViewerTestWorkspace.ConversationID, start!.Value, pageSize: 3);

            Assert.Equal("Day 7", page.Messages[0].MessageContent);
            Assert.Equal(First.AddDays(7), DateOnly.FromDateTime(page.Messages[0].MessageDateTimeLocal));
            Assert.True(page.HasEarlier);
            Assert.True(page.HasLater);
        }
    }
}
