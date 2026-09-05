using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Viewer
{
    /// <summary>
    /// That an entire browse works on a connection SQLite itself will not let anything write to.
    /// </summary>
    /// <remarks>
    /// The guarantee is <c>Mode=ReadOnly</c>'s, not a promise made by the readers, which is why the
    /// last test here attempts a write on the same connection and expects SQLite to refuse it. No
    /// hash or checkpoint protocol is involved: this is an ordinary integration test.
    /// </remarks>
    public class ViewerReadOnlySafetyTests
    {
        private static readonly DateOnly Day = new(2026, 4, 6);

        [Fact]
        public void EveryBrowseApi_WorksOnAReadOnlyConnection()
        {
            using var workspace = new ViewerTestWorkspace();

            var source = workspace.Workspace.AddMediaSource();
            var hash = MatchingTestData.Hash(1);
            var asset = workspace.Workspace.AddAssetWithCopy(source, hash, Day);

            workspace.AddMessages(count: 12, Day);

            var carrying = workspace.AddMessage("<Media omitted>", Day, "10:00:00");
            var attachments = workspace.AddAttachments(carrying, count: 1, resolvedMediaAssetID: asset);

            workspace.AddMessages(count: 6, Day.AddDays(1));

            using var connection = workspace.OpenReadOnly();

            var reader = new ConversationReader();

            var conversation = reader
                .ReadConversations(connection)
                .Single(summary => summary.ConversationID == ViewerTestWorkspace.ConversationID);

            Assert.Equal(19, conversation.MessageCount);

            var first = reader.ReadFirstPage(connection, conversation.ConversationID, pageSize: 10);
            Assert.Equal(10, first.Messages.Count);
            Assert.True(first.HasLater);

            var next = reader.ReadPageFrom(
                connection,
                conversation.ConversationID,
                first.LastSequenceNumber!.Value + 1,
                pageSize: 10);

            Assert.Equal(9, next.Messages.Count);

            var back = reader.ReadPageBefore(
                connection,
                conversation.ConversationID,
                next.FirstSequenceNumber!.Value,
                pageSize: 10);

            Assert.Equal(first.LastSequenceNumber, back.LastSequenceNumber);

            var last = reader.ReadLastPage(connection, conversation.ConversationID, pageSize: 10);
            Assert.False(last.HasLater);

            var jumped = reader.FindFirstSequenceNumberOnOrAfter(
                connection, conversation.ConversationID, Day.AddDays(1));

            Assert.NotNull(jumped);

            var page = reader.ReadPageFrom(
                connection, conversation.ConversationID, jumped.Value, pageSize: 10);

            Assert.Equal(Day.AddDays(1), DateOnly.FromDateTime(page.Messages[0].MessageDateTimeLocal));

            var whole = reader.ReadFirstPage(
                connection, conversation.ConversationID, ConversationReader.MaximumPageSize);

            var attachmentIDs = whole.Messages
                .SelectMany(message => message.Attachments)
                .Select(attachment => attachment.AttachmentID)
                .ToList();

            Assert.Equal(attachments, attachmentIDs);

            var states = new ConversationReconstructionReader().Read(connection, attachmentIDs);

            Assert.Equal(
                Phase7ResolutionStatus.Resolved,
                states[attachmentIDs[0]].Status);

            var location = Assert.Single(
                new MediaAssetLocationReader().Read(connection, [asset]));

            Assert.Equal(asset, location.MediaAssetID);
            Assert.False(string.IsNullOrWhiteSpace(location.FullPath));
        }

        [Fact]
        public void AWriteOnThatSameConnection_IsRefusedBySqlite()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 2, Day);

            using var connection = workspace.OpenReadOnly();

            new ConversationReader().ReadFirstPage(
                connection, ViewerTestWorkspace.ConversationID, pageSize: 10);

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Message SET MessageContent = 'changed';";

            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }

        /// <remarks>
        /// Row counts before and after a whole browse. Nothing the viewer reads is allowed to be a
        /// write in disguise, and the read-only mode above is only one of the two ways to see that.
        /// </remarks>
        [Fact]
        public void AWholeBrowse_LeavesTheWorkspaceUnchanged()
        {
            using var workspace = new ViewerTestWorkspace();

            var source = workspace.Workspace.AddMediaSource();
            var asset = workspace.Workspace.AddAssetWithCopy(source, MatchingTestData.Hash(1), Day);

            var carrying = workspace.AddMessage("<Media omitted>", Day);
            workspace.AddAttachments(carrying, count: 1, resolvedMediaAssetID: asset);
            workspace.AddMessages(count: 5, Day);

            const string CountsStatement =
                """
                SELECT (SELECT COUNT(*) FROM Message)
                     + (SELECT COUNT(*) FROM Attachment)
                     + (SELECT COUNT(*) FROM MediaFile)
                     + (SELECT COUNT(*) FROM MediaAsset)
                     + (SELECT COUNT(*) FROM MediaAssetFile)
                     + (SELECT COUNT(*) FROM AttachmentResolutionEvent)
                     + (SELECT COUNT(*) FROM AttachmentReviewEvent)
                     + (SELECT COUNT(*) FROM Phase7Run);
                """;

            var before = workspace.ScalarLongReadOnly(CountsStatement);

            using (var connection = workspace.OpenReadOnly())
            {
                var reader = new ConversationReader();
                reader.ReadConversations(connection);

                var page = reader.ReadFirstPage(
                    connection, ViewerTestWorkspace.ConversationID, pageSize: 50);

                reader.FindFirstSequenceNumberOnOrAfter(
                    connection, ViewerTestWorkspace.ConversationID, Day);

                new ConversationReconstructionReader().Read(
                    connection,
                    [.. page.Messages
                        .SelectMany(message => message.Attachments)
                        .Select(attachment => attachment.AttachmentID)]);

                new MediaAssetLocationReader().Read(connection, [asset]);
            }

            Assert.Equal(before, workspace.ScalarLongReadOnly(CountsStatement));
        }

        /// <remarks>
        /// The viewer refuses a workspace that is not the current schema version rather than
        /// migrating it, so this is the check it makes: the public constant and the value the file
        /// reports, compared, with nothing created either way.
        /// </remarks>
        [Fact]
        public void ASyntheticWorkspace_ReportsTheCurrentSchemaVersionWithoutBeingInitialised()
        {
            using var workspace = new ViewerTestWorkspace();
            workspace.AddMessages(count: 1, Day);

            using var connection = workspace.OpenReadOnly();

            Assert.Equal(
                WorkspaceDatabase.SchemaVersion,
                WorkspaceDatabase.ReadSchemaVersion(connection));
        }
    }
}
