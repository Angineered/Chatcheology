using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Tests.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;

namespace Chatcheology.Data.Tests.Viewer
{
    /// <summary>
    /// How a page of messages asks Phase 7 about its attachments.
    /// </summary>
    /// <remarks>
    /// Phase 7's own rules are not retested here; its suite remains the authority for what a status
    /// or a placeholder reason means. What is tested is the joining: that the viewer asks about the
    /// right attachments, asks in bounded batches, does not ask when there is nothing to ask about,
    /// and repeats Phase 7's answer rather than forming one.
    /// </remarks>
    public class ViewerReconstructionIntegrationTests
    {
        private static readonly DateOnly Day = new(2026, 4, 6);

        /// <remarks>
        /// The pin for the empty case. The batch read throws on an empty list, so a helper that
        /// returned an empty result without throwing demonstrably did not call it.
        /// </remarks>
        [Fact]
        public void APageWithNoAttachments_AsksPhase7Nothing()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 5, Day);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader()
                .ReadFirstPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            var attachmentIDs = AttachmentIDsOf(page);

            Assert.Empty(attachmentIDs);

            Assert.Empty(new ConversationReconstructionReader().Read(connection, attachmentIDs));
        }

        /// <remarks>
        /// And the same call with one attachment does reach the reader, so the test above is not
        /// passing because nothing is wired up.
        /// </remarks>
        [Fact]
        public void APageWithAnAttachment_DoesAskPhase7()
        {
            using var workspace = new ViewerTestWorkspace();
            var carrying = workspace.AddMessage("<Media omitted>", Day);
            workspace.AddAttachments(carrying, count: 1);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader()
                .ReadFirstPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            var states = new ConversationReconstructionReader()
                .Read(connection, AttachmentIDsOf(page));

            var state = Assert.Single(states).Value;

            Assert.Equal(Phase7ResolutionStatus.Unresolved, state.Status);
            Assert.Equal(Phase7UnresolvedPlaceholderReason.NeverEvaluated, state.PlaceholderReason);
        }

        [Fact]
        public void EveryAttachmentOnThePage_IsAnsweredForItself()
        {
            using var workspace = new ViewerTestWorkspace();

            var first = workspace.AddMessage("<Media omitted>", Day);
            workspace.AddMessage("Just text", Day, "09:01:00");
            var second = workspace.AddMessage("<Media omitted>", Day, "09:02:00");

            var firstAttachments = workspace.AddAttachments(first, count: 2);
            var secondAttachments = workspace.AddAttachments(second, count: 1);

            using var connection = workspace.OpenReadOnly();

            var page = new ConversationReader()
                .ReadFirstPage(connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            var attachmentIDs = AttachmentIDsOf(page);

            Assert.Equal([.. firstAttachments, .. secondAttachments], attachmentIDs);

            var states = new ConversationReconstructionReader().Read(connection, attachmentIDs);

            Assert.Equal(3, states.Count);

            Assert.All(
                attachmentIDs,
                attachmentID => Assert.Equal(attachmentID, states[attachmentID].AttachmentID));
        }

        /// <remarks>
        /// More attachments than one batch binds, so the read crosses the internal boundary. Every
        /// attachment must still be answered, and answered as itself.
        /// </remarks>
        [Fact]
        public void MoreAttachmentsThanOneBatch_AreAllAnsweredCorrectly()
        {
            using var workspace = new ViewerTestWorkspace();
            var count = ConversationReconstructionReader.BatchSize + 20;
            var expected = new List<long>(count);

            for (var index = 0; index < count; index++)
            {
                var messageID = workspace.AddMessage(
                    "<Media omitted>", Day, ViewerTestWorkspace.Minute(index));

                expected.AddRange(workspace.AddAttachments(messageID, count: 1));
            }

            using var connection = workspace.OpenReadOnly();

            var states = new ConversationReconstructionReader().Read(connection, expected);

            Assert.Equal(count, states.Count);

            Assert.All(
                expected,
                attachmentID =>
                {
                    Assert.True(states.ContainsKey(attachmentID));
                    Assert.Equal(attachmentID, states[attachmentID].AttachmentID);
                });
        }

        [Fact]
        public void ARepeatedAttachment_IsRefused()
        {
            using var workspace = new ViewerTestWorkspace();
            var carrying = workspace.AddMessage("<Media omitted>", Day);
            var attachments = workspace.AddAttachments(carrying, count: 1);

            using var connection = workspace.OpenReadOnly();

            Assert.Throws<ArgumentException>(
                () => new ConversationReconstructionReader().Read(
                    connection, [attachments[0], attachments[0]]));
        }

        // -----------------------------------------------------------------------------------
        // The two states, as Phase 7 reports them.
        // -----------------------------------------------------------------------------------

        /// <remarks>
        /// Built through the real Phase 7 path — an evidence run and a confirmed resolution — so
        /// what the viewer displays is what those services produced, not a row a test wrote to look
        /// like one.
        /// </remarks>
        [Fact]
        public void AResolvedAttachment_ReportsPhase7sOwnAssetDescriptor()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();
            fixture.Confirm(attachment, asset);

            using var connection =
                WorkspaceDatabase.OpenReadOnlyConnection(fixture.Evidence.DatabasePath);

            var state = new ConversationReconstructionReader()
                .Read(connection, [attachment])[attachment];

            Assert.Equal(Phase7ResolutionStatus.Resolved, state.Status);
            Assert.Null(state.PlaceholderReason);
            Assert.Equal(asset, state.ResolvedAsset!.MediaAssetID);
        }

        [Fact]
        public void AnUnresolvedAttachment_ReportsPhase7sOwnPlaceholderReason()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            fixture.Run();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);

            var state = new ConversationReconstructionReader()
                .Read(connection, [attachment])[attachment];

            Assert.Equal(Phase7ResolutionStatus.Unresolved, state.Status);
            Assert.Null(state.ResolvedAsset);
            Assert.Equal(Phase7UnresolvedPlaceholderReason.NoCandidates, state.PlaceholderReason);
            Assert.NotNull(state.EvidenceRunID);
        }

        /// <remarks>
        /// The page reader says where an attachment sits; it says nothing about what it is. Those
        /// are separate questions with separate owners, and this is where the viewer joins them.
        /// </remarks>
        [Fact]
        public void ThePageReader_CarriesPositionOnlyAndTheStatesCarryTheRest()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();
            fixture.Confirm(attachment, asset);

            using var connection =
                WorkspaceDatabase.OpenReadOnlyConnection(fixture.Evidence.DatabasePath);

            var page = new ConversationReader().ReadFirstPage(
                connection, MatchingTestWorkspace.ConversationID, pageSize: 50);

            var carried = page.Messages
                .SelectMany(message => message.Attachments)
                .Single(entry => entry.AttachmentID == attachment);

            Assert.Equal(1, carried.Ordinal);

            var states = new ConversationReconstructionReader()
                .Read(connection, AttachmentIDsOf(page));

            Assert.Equal(Phase7ResolutionStatus.Resolved, states[attachment].Status);
        }

        private static List<long> AttachmentIDsOf(ConversationPage page) =>
            [.. page.Messages
                .SelectMany(message => message.Attachments)
                .Select(attachment => attachment.AttachmentID)];
    }
}
