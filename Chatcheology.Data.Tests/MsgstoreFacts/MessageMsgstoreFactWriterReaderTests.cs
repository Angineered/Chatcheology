using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Tests.Viewer;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.MsgstoreFacts
{
    /// <summary>
    /// The text-message msgstore fact writer and reader (CH-22): batches go in and come out whole,
    /// and every refusal writes nothing.
    /// </summary>
    /// <remarks>
    /// Synthetic workspaces only: invented text messages, one invented media message with an
    /// attachment, and one invented system message. No real identifier, path or figure appears here.
    /// </remarks>
    public sealed class MessageMsgstoreFactWriterReaderTests : IDisposable
    {
        private static readonly DateTime Recorded =
            new(2026, 10, 19, 8, 30, 0, DateTimeKind.Utc);

        private readonly ViewerTestWorkspace _workspace = new();

        private readonly long[] _texts;

        private readonly long _mediaMessage;

        private readonly long _systemMessage;

        public MessageMsgstoreFactWriterReaderTests()
        {
            var date = new DateOnly(2026, 3, 2);

            _texts = Enumerable.Range(0, 4)
                .Select(index => _workspace.AddMessage(
                    $"Text {index}", date, ViewerTestWorkspace.Minute(index)))
                .ToArray();

            _mediaMessage = _workspace.AddMessage("A caption", date, ViewerTestWorkspace.Minute(10));
            _workspace.AddAttachments(_mediaMessage);

            _systemMessage = _workspace.AddSystemMessage(
                "Messages are end-to-end encrypted.", date, ViewerTestWorkspace.Minute(11));

            _workspace.Workspace.CloseBuildingConnection();
        }

        private string Path => _workspace.DatabasePath;

        private long M(int index) => _texts[index];

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _workspace.Dispose();
        }

        // ---------------------------------------------------------------------------------------
        // Writing and reading back.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void InsertBatch_RecordsEveryRowAndTheReaderReturnsThem()
        {
            var written = MessageMsgstoreFactWriter.InsertBatch(
                Path,
                "text-1",
                [
                    new MessageMsgstoreFactRow(M(0), true, 1),
                    new MessageMsgstoreFactRow(M(1), true, 120),
                    new MessageMsgstoreFactRow(M(2), false, null),
                ],
                Recorded);

            Assert.Equal(3, written);

            var facts = Read([M(0), M(1), M(2), M(3)]);

            Assert.Equal(3, facts.Count);

            var first = facts[M(0)];
            Assert.True(first.IsForwarded);
            Assert.Equal(1, first.ForwardScore);
            Assert.Equal("text-1", first.SourceBatch);
            Assert.Equal(Recorded, first.RecordedDateTimeUtc);
            Assert.Equal(DateTimeKind.Utc, first.RecordedDateTimeUtc.Kind);

            Assert.Equal(120, facts[M(1)].ForwardScore);

            // A known "not forwarded" is a row; an unknown message has none.
            Assert.False(facts[M(2)].IsForwarded);
            Assert.Null(facts[M(2)].ForwardScore);
            Assert.False(facts.ContainsKey(M(3)));
        }

        [Fact]
        public void ReadForPage_ReturnsThePageFacts()
        {
            MessageMsgstoreFactWriter.InsertBatch(
                Path,
                "text-1",
                [new MessageMsgstoreFactRow(M(0), true, 2), new MessageMsgstoreFactRow(M(3), false, 0)],
                Recorded);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(Path);

            var page = new ConversationReader().ReadFirstPage(
                connection, ViewerTestWorkspace.ConversationID, ConversationReader.DefaultPageSize);

            var facts = new MessageMsgstoreFactReader().ReadForPage(connection, page);

            Assert.Equal(new[] { M(0), M(3) }, facts.Keys.Order());
            Assert.Equal(0, facts[M(3)].ForwardScore);
        }

        [Fact]
        public void AWorkspaceWithNoFacts_ReadsEmpty()
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(Path);

            var reader = new MessageMsgstoreFactReader();

            Assert.Empty(reader.Read(connection, _texts));
            Assert.Empty(reader.Read(connection, []));
        }

        [Fact]
        public void TheReaderIssuesOnlySelectStatements()
        {
            foreach (var statement in MessageMsgstoreFactReader.Statements)
            {
                var trimmed = statement.TrimStart();

                Assert.StartsWith("SELECT", trimmed, StringComparison.Ordinal);

                foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE", "REPLACE", "CREATE", "DROP", "ALTER" })
                {
                    Assert.DoesNotContain(verb, statement, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        [Fact]
        public void TheWriterLeavesTheAttachmentFactTableAlone()
        {
            MessageMsgstoreFactWriter.InsertBatch(
                Path, "text-1", [new MessageMsgstoreFactRow(M(0), true, 1)], Recorded);

            Assert.Equal(0, Scalar("SELECT COUNT(*) FROM AttachmentMsgstoreFact;"));
        }

        // ---------------------------------------------------------------------------------------
        // Deleting by batch.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void DeleteBatch_RemovesThatBatchOnly()
        {
            MessageMsgstoreFactWriter.InsertBatch(
                Path,
                "text-1",
                [new MessageMsgstoreFactRow(M(0), true, 1), new MessageMsgstoreFactRow(M(1), false, null)],
                Recorded);

            MessageMsgstoreFactWriter.InsertBatch(
                Path, "text-2", [new MessageMsgstoreFactRow(M(2), true, 9)], Recorded);

            Assert.Equal(2, MessageMsgstoreFactWriter.DeleteBatch(Path, "text-1"));

            Assert.Equal(1, FactCount());
            Assert.Equal(1, Scalar("SELECT COUNT(*) FROM MessageMsgstoreFact WHERE SourceBatch = 'text-2';"));

            // And the messages themselves are untouched.
            Assert.Equal(_texts.Length + 2, Scalar("SELECT COUNT(*) FROM Message;"));
        }

        [Fact]
        public void DeleteBatch_UnknownBatch_RemovesNothing()
        {
            MessageMsgstoreFactWriter.InsertBatch(
                Path, "text-1", [new MessageMsgstoreFactRow(M(0), true, 1)], Recorded);

            Assert.Equal(0, MessageMsgstoreFactWriter.DeleteBatch(Path, "text-9"));
            Assert.Equal(1, FactCount());
        }

        [Fact]
        public void DeleteBatch_LeavesAnAttachmentBatchOfTheSameNameAlone()
        {
            var attachmentID = Scalar(
                $"SELECT AttachmentID FROM Attachment WHERE MessageID = {_mediaMessage};");

            MsgstoreFactWriter.InsertBatch(
                Path, "shared", [new MsgstoreFactRow(attachmentID, true, 1, null)], Recorded);

            MessageMsgstoreFactWriter.InsertBatch(
                Path, "shared", [new MessageMsgstoreFactRow(M(0), true, 1)], Recorded);

            Assert.Equal(1, MessageMsgstoreFactWriter.DeleteBatch(Path, "shared"));

            Assert.Equal(0, FactCount());
            Assert.Equal(1, Scalar("SELECT COUNT(*) FROM AttachmentMsgstoreFact;"));
        }

        [Fact]
        public void DeleteThenInsert_ReplacesABatchCleanly()
        {
            MessageMsgstoreFactWriter.InsertBatch(
                Path, "text-1", [new MessageMsgstoreFactRow(M(0), true, 1)], Recorded);

            MessageMsgstoreFactWriter.DeleteBatch(Path, "text-1");

            MessageMsgstoreFactWriter.InsertBatch(
                Path, "text-1", [new MessageMsgstoreFactRow(M(0), true, 7)], Recorded);

            Assert.Equal(7, Read([M(0)])[M(0)].ForwardScore);
        }

        // ---------------------------------------------------------------------------------------
        // Refusals: each writes nothing.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AMessageWithAFactFromAnotherBatch_IsRefused()
        {
            MessageMsgstoreFactWriter.InsertBatch(
                Path, "text-1", [new MessageMsgstoreFactRow(M(0), true, 1)], Recorded);

            var exception = Assert.Throws<InvalidOperationException>(
                () => MessageMsgstoreFactWriter.InsertBatch(
                    Path,
                    "text-2",
                    [new MessageMsgstoreFactRow(M(1), true, 1), new MessageMsgstoreFactRow(M(0), true, 2)],
                    Recorded));

            Assert.Contains("another batch", exception.Message, StringComparison.Ordinal);
            AssertOnlyBatch("text-1", 1);
        }

        [Fact]
        public void TheSameBatchTwice_IsRefused()
        {
            MessageMsgstoreFactWriter.InsertBatch(
                Path, "text-1", [new MessageMsgstoreFactRow(M(0), true, 1)], Recorded);

            var exception = Assert.Throws<InvalidOperationException>(
                () => MessageMsgstoreFactWriter.InsertBatch(
                    Path, "text-1", [new MessageMsgstoreFactRow(M(0), true, 1)], Recorded));

            Assert.Contains("this batch", exception.Message, StringComparison.Ordinal);
            AssertOnlyBatch("text-1", 1);
        }

        [Fact]
        public void AnUnknownMessage_IsRefused()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => MessageMsgstoreFactWriter.InsertBatch(
                    Path,
                    "text-1",
                    [new MessageMsgstoreFactRow(M(0), true, 1), new MessageMsgstoreFactRow(9999, true, 1)],
                    Recorded));

            Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void ASystemMessage_IsRefused()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => MessageMsgstoreFactWriter.InsertBatch(
                    Path,
                    "text-1",
                    [new MessageMsgstoreFactRow(M(0), true, 1), new MessageMsgstoreFactRow(_systemMessage, false, null)],
                    Recorded));

            Assert.Contains("system message", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, FactCount());
        }

        /// <remarks>
        /// Captions are excluded by design: the attachment's own fact already says whether the
        /// message was forwarded.
        /// </remarks>
        [Fact]
        public void AMessageWithAnAttachment_IsRefused()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => MessageMsgstoreFactWriter.InsertBatch(
                    Path,
                    "text-1",
                    [new MessageMsgstoreFactRow(M(0), true, 1), new MessageMsgstoreFactRow(_mediaMessage, true, 1)],
                    Recorded));

            Assert.Contains("has an attachment", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void InvalidInput_IsRefusedBeforeTheWorkspaceIsTouched()
        {
            var before = WorkspaceCheckpoint.ComputeFileSha256(Path);

            Assert.Throws<ArgumentException>(() => MessageMsgstoreFactWriter.InsertBatch(
                Path, " ", [new MessageMsgstoreFactRow(M(0), true, 1)], Recorded));

            Assert.Throws<ArgumentException>(() => MessageMsgstoreFactWriter.InsertBatch(
                Path, "text-1", [], Recorded));

            Assert.Throws<ArgumentException>(() => MessageMsgstoreFactWriter.InsertBatch(
                Path,
                "text-1",
                [new MessageMsgstoreFactRow(M(0), true, 1), new MessageMsgstoreFactRow(M(0), true, 2)],
                Recorded));

            Assert.Throws<ArgumentException>(() => MessageMsgstoreFactWriter.InsertBatch(
                Path, "text-1", [new MessageMsgstoreFactRow(M(0), true, -1)], Recorded));

            Assert.Throws<ArgumentException>(() => MessageMsgstoreFactWriter.InsertBatch(
                Path,
                "text-1",
                [new MessageMsgstoreFactRow(M(0), true, 1)],
                new DateTime(2026, 10, 19, 8, 30, 0, DateTimeKind.Local)));

            Assert.Throws<ArgumentException>(() => MessageMsgstoreFactWriter.InsertBatch(
                Path,
                "text-1",
                [new MessageMsgstoreFactRow(M(0), true, 1)],
                new DateTime(2026, 10, 19, 8, 30, 0, DateTimeKind.Unspecified)));

            Assert.Throws<ArgumentException>(() => MessageMsgstoreFactWriter.DeleteBatch(Path, ""));

            SqliteConnection.ClearAllPools();
            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(Path));
            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void AWorkspaceClaimedByAnotherOperation_IsRefused()
        {
            using (WorkspaceExclusiveLock.Acquire(Path))
            {
                Assert.Throws<InvalidOperationException>(() => MessageMsgstoreFactWriter.InsertBatch(
                    Path, "text-1", [new MessageMsgstoreFactRow(M(0), true, 1)], Recorded));

                Assert.Throws<InvalidOperationException>(
                    () => MessageMsgstoreFactWriter.DeleteBatch(Path, "text-1"));
            }

            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void AMissingWorkspace_IsRefusedAndNotCreated()
        {
            var missing = System.IO.Path.Combine(_workspace.DirectoryPath, "absent.db");

            Assert.Throws<InvalidOperationException>(() => MessageMsgstoreFactWriter.InsertBatch(
                missing, "text-1", [new MessageMsgstoreFactRow(1, true, 1)], Recorded));

            Assert.False(File.Exists(missing));
        }

        /// <remarks>
        /// The schema's own CHECK and foreign-key constraints hold even for a write that bypasses
        /// the writer.
        /// </remarks>
        [Theory]
        [InlineData("{0}, 2, NULL, 'b', 't'")]
        [InlineData("{0}, 1, -1, 'b', 't'")]
        [InlineData("{0}, 1, NULL, '', 't'")]
        [InlineData("{0}, NULL, NULL, 'b', 't'")]
        [InlineData("{0}, 1, NULL, NULL, 't'")]
        [InlineData("{0}, 1, NULL, 'b', NULL")]
        [InlineData("9999, 1, NULL, 'b', 't'")]
        public void TheTableConstraintsRefuseInvalidRows(string values)
        {
            using var connection = WorkspaceDatabase.OpenConnection(Path);

            Assert.Throws<SqliteException>(() => Execute(
                connection,
                "INSERT INTO MessageMsgstoreFact (MessageID, IsForwarded, ForwardScore, " +
                "SourceBatch, RecordedDateTimeUtc) VALUES (" +
                string.Format(System.Globalization.CultureInfo.InvariantCulture, values, M(0)) + ");"));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private IReadOnlyDictionary<long, MessageMsgstoreFact> Read(IReadOnlyCollection<long> messageIDs)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(Path);

            return new MessageMsgstoreFactReader().Read(connection, messageIDs);
        }

        private long Scalar(string sql)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(Path);

            return ScalarLong(connection, sql);
        }

        private long FactCount() => Scalar("SELECT COUNT(*) FROM MessageMsgstoreFact;");

        private void AssertOnlyBatch(string batch, long count)
        {
            Assert.Equal(count, FactCount());
            Assert.Equal(
                count,
                Scalar($"SELECT COUNT(*) FROM MessageMsgstoreFact WHERE SourceBatch = '{batch}';"));
        }
    }
}
