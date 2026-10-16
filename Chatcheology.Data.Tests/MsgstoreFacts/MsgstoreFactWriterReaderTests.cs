using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Tests.Viewer;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.MsgstoreFacts
{
    /// <summary>
    /// The msgstore fact writer and reader (CH-8, CH-10): batches go in and come out whole, and
    /// every refusal writes nothing.
    /// </summary>
    /// <remarks>
    /// Synthetic workspaces only: invented messages with one attachment each. No real identifier,
    /// path or figure appears here.
    /// </remarks>
    public sealed class MsgstoreFactWriterReaderTests : IDisposable
    {
        private static readonly DateTime Recorded =
            new(2026, 10, 16, 8, 30, 0, DateTimeKind.Utc);

        private readonly ViewerTestWorkspace _workspace = new();

        private readonly long[] _attachments;

        public MsgstoreFactWriterReaderTests()
        {
            var date = new DateOnly(2026, 3, 2);

            _attachments = Enumerable.Range(0, 6)
                .Select(index => _workspace.AddAttachments(
                    _workspace.AddMessage(
                        $"Photo {index}", date, ViewerTestWorkspace.Minute(index)))[0])
                .ToArray();

            _workspace.Workspace.CloseBuildingConnection();
        }

        private string Path => _workspace.DatabasePath;

        private long A(int index) => _attachments[index];

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
            var written = MsgstoreFactWriter.InsertBatch(
                Path,
                "batch-1",
                [
                    new MsgstoreFactRow(A(0), true, 1, null),
                    new MsgstoreFactRow(A(1), true, 120, null),
                    new MsgstoreFactRow(A(2), false, null, A(3)),
                ],
                Recorded);

            Assert.Equal(3, written);

            var set = Read([A(0), A(1), A(2), A(3), A(4)]);

            Assert.Equal(3, set.Facts.Count);

            var first = set.Facts[A(0)];
            Assert.True(first.IsForwarded);
            Assert.Equal(1, first.ForwardScore);
            Assert.Null(first.LowQualityCopyOfAttachmentID);
            Assert.Equal("batch-1", first.SourceBatch);
            Assert.Equal(Recorded, first.RecordedDateTimeUtc);
            Assert.Equal(DateTimeKind.Utc, first.RecordedDateTimeUtc.Kind);

            Assert.Equal(120, set.Facts[A(1)].ForwardScore);

            var copy = set.Facts[A(2)];
            Assert.False(copy.IsForwarded);
            Assert.Null(copy.ForwardScore);
            Assert.Equal(A(3), copy.LowQualityCopyOfAttachmentID);

            Assert.Equal(new[] { A(2) }, set.LowQualityCopiesByFullCopy[A(3)]);
            Assert.Single(set.LowQualityCopiesByFullCopy);
            Assert.False(set.Facts.ContainsKey(A(3)));
        }

        [Fact]
        public void Read_ListsACopyAgainstItsFullCopyEvenWhenTheCopyWasNotAskedAbout()
        {
            MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [new MsgstoreFactRow(A(4), false, null, A(5))], Recorded);

            var set = Read([A(5)]);

            Assert.Empty(set.Facts);
            Assert.Equal(new[] { A(4) }, set.LowQualityCopiesByFullCopy[A(5)]);
        }

        [Fact]
        public void ReadForPage_ReturnsThePageFacts()
        {
            MsgstoreFactWriter.InsertBatch(
                Path,
                "batch-1",
                [new MsgstoreFactRow(A(0), true, 2, null), new MsgstoreFactRow(A(1), false, null, A(0))],
                Recorded);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(Path);

            var page = new ConversationReader().ReadFirstPage(
                connection, ViewerTestWorkspace.ConversationID, ConversationReader.DefaultPageSize);

            var set = new MsgstoreFactReader().ReadForPage(connection, page);

            Assert.Equal(2, set.Facts.Count);
            Assert.Equal(new[] { A(1) }, set.LowQualityCopiesByFullCopy[A(0)]);
        }

        [Fact]
        public void AWorkspaceWithNoFacts_ReadsEmptyAndHasNoLowQualityCopy()
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(Path);

            var reader = new MsgstoreFactReader();
            var set = reader.Read(connection, _attachments);

            Assert.Empty(set.Facts);
            Assert.Empty(set.LowQualityCopiesByFullCopy);
            Assert.False(reader.HasAnyLowQualityCopy(connection));
            Assert.Empty(reader.Read(connection, []).Facts);
        }

        [Fact]
        public void HasAnyLowQualityCopy_IsTrueOnlyOnceACopyIsRecorded()
        {
            MsgstoreFactWriter.InsertBatch(
                Path, "forwards", [new MsgstoreFactRow(A(0), true, 1, null)], Recorded);

            Assert.False(HasAnyCopy());

            MsgstoreFactWriter.InsertBatch(
                Path, "copies", [new MsgstoreFactRow(A(1), false, null, A(2))], Recorded);

            Assert.True(HasAnyCopy());
        }

        [Fact]
        public void TheReaderIssuesOnlySelectStatements()
        {
            foreach (var statement in MsgstoreFactReader.Statements)
            {
                var trimmed = statement.TrimStart();

                Assert.StartsWith("SELECT", trimmed, StringComparison.Ordinal);

                foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE", "REPLACE", "CREATE", "DROP", "ALTER" })
                {
                    Assert.DoesNotContain(verb, statement, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Deleting by batch.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void DeleteBatch_RemovesThatBatchOnly()
        {
            MsgstoreFactWriter.InsertBatch(
                Path,
                "batch-1",
                [new MsgstoreFactRow(A(0), true, 1, null), new MsgstoreFactRow(A(1), false, null, A(2))],
                Recorded);

            MsgstoreFactWriter.InsertBatch(
                Path, "batch-2", [new MsgstoreFactRow(A(3), true, 9, null)], Recorded);

            Assert.Equal(2, MsgstoreFactWriter.DeleteBatch(Path, "batch-1"));

            Assert.Equal(1, FactCount());
            Assert.Equal(1, Scalar("SELECT COUNT(*) FROM AttachmentMsgstoreFact WHERE SourceBatch = 'batch-2';"));

            // And the attachments themselves are untouched.
            Assert.Equal(_attachments.Length, Scalar("SELECT COUNT(*) FROM Attachment;"));
        }

        [Fact]
        public void DeleteBatch_UnknownBatch_RemovesNothing()
        {
            MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [new MsgstoreFactRow(A(0), true, 1, null)], Recorded);

            Assert.Equal(0, MsgstoreFactWriter.DeleteBatch(Path, "batch-9"));
            Assert.Equal(1, FactCount());
        }

        [Fact]
        public void DeleteThenInsert_ReplacesABatchCleanly()
        {
            MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [new MsgstoreFactRow(A(0), true, 1, null)], Recorded);

            MsgstoreFactWriter.DeleteBatch(Path, "batch-1");

            MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [new MsgstoreFactRow(A(0), true, 7, null)], Recorded);

            Assert.Equal(7, Read([A(0)]).Facts[A(0)].ForwardScore);
        }

        // ---------------------------------------------------------------------------------------
        // Refusals: each writes nothing.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AnAttachmentWithAFactFromAnotherBatch_IsRefused()
        {
            MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [new MsgstoreFactRow(A(0), true, 1, null)], Recorded);

            var exception = Assert.Throws<InvalidOperationException>(
                () => MsgstoreFactWriter.InsertBatch(
                    Path,
                    "batch-2",
                    [new MsgstoreFactRow(A(1), true, 1, null), new MsgstoreFactRow(A(0), true, 2, null)],
                    Recorded));

            Assert.Contains("another batch", exception.Message, StringComparison.Ordinal);
            AssertOnlyBatch("batch-1", 1);
        }

        [Fact]
        public void TheSameBatchTwice_IsRefused()
        {
            MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [new MsgstoreFactRow(A(0), true, 1, null)], Recorded);

            var exception = Assert.Throws<InvalidOperationException>(
                () => MsgstoreFactWriter.InsertBatch(
                    Path, "batch-1", [new MsgstoreFactRow(A(0), true, 1, null)], Recorded));

            Assert.Contains("this batch", exception.Message, StringComparison.Ordinal);
            AssertOnlyBatch("batch-1", 1);
        }

        [Fact]
        public void AnUnknownAttachment_IsRefused()
        {
            Assert.Throws<InvalidOperationException>(
                () => MsgstoreFactWriter.InsertBatch(
                    Path,
                    "batch-1",
                    [new MsgstoreFactRow(A(0), true, 1, null), new MsgstoreFactRow(9999, true, 1, null)],
                    Recorded));

            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void AnUnknownLowQualityCopyTarget_IsRefused()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => MsgstoreFactWriter.InsertBatch(
                    Path, "batch-1", [new MsgstoreFactRow(A(0), false, null, 9999)], Recorded));

            Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void AChainWithinTheBatch_IsRefused()
        {
            // A(0) is a copy of A(1), and A(1) is itself a copy of A(2).
            var exception = Assert.Throws<ArgumentException>(
                () => MsgstoreFactWriter.InsertBatch(
                    Path,
                    "batch-1",
                    [new MsgstoreFactRow(A(0), false, null, A(1)), new MsgstoreFactRow(A(1), false, null, A(2))],
                    Recorded));

            Assert.Contains("Chains", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void ATargetThatIsAStoredCopy_IsRefused()
        {
            MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [new MsgstoreFactRow(A(1), false, null, A(2))], Recorded);

            var exception = Assert.Throws<InvalidOperationException>(
                () => MsgstoreFactWriter.InsertBatch(
                    Path, "batch-2", [new MsgstoreFactRow(A(0), false, null, A(1))], Recorded));

            Assert.Contains("Chains", exception.Message, StringComparison.Ordinal);
            AssertOnlyBatch("batch-1", 1);
        }

        [Fact]
        public void ACopyOfAnAttachmentThatIsAlreadyAStoredFullCopy_IsRefused()
        {
            // Stored: A(0) is a copy of A(1). New: A(1) would become a copy of A(2).
            MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [new MsgstoreFactRow(A(0), false, null, A(1))], Recorded);

            var exception = Assert.Throws<InvalidOperationException>(
                () => MsgstoreFactWriter.InsertBatch(
                    Path, "batch-2", [new MsgstoreFactRow(A(1), false, null, A(2))], Recorded));

            Assert.Contains("Chains", exception.Message, StringComparison.Ordinal);
            AssertOnlyBatch("batch-1", 1);
        }

        [Fact]
        public void TwoCopiesOfOneFullCopy_AreAccepted()
        {
            MsgstoreFactWriter.InsertBatch(
                Path,
                "batch-1",
                [new MsgstoreFactRow(A(0), false, null, A(2)), new MsgstoreFactRow(A(1), false, null, A(2))],
                Recorded);

            Assert.Equal(new[] { A(0), A(1) }, Read([A(2)]).LowQualityCopiesByFullCopy[A(2)]);
        }

        [Fact]
        public void InvalidInput_IsRefusedBeforeTheWorkspaceIsTouched()
        {
            Assert.Throws<ArgumentException>(() => MsgstoreFactWriter.InsertBatch(
                Path, " ", [new MsgstoreFactRow(A(0), true, 1, null)], Recorded));

            Assert.Throws<ArgumentException>(() => MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [], Recorded));

            Assert.Throws<ArgumentException>(() => MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [new MsgstoreFactRow(A(0), false, null, A(0))], Recorded));

            Assert.Throws<ArgumentException>(() => MsgstoreFactWriter.InsertBatch(
                Path,
                "batch-1",
                [new MsgstoreFactRow(A(0), true, 1, null), new MsgstoreFactRow(A(0), true, 2, null)],
                Recorded));

            Assert.Throws<ArgumentException>(() => MsgstoreFactWriter.InsertBatch(
                Path, "batch-1", [new MsgstoreFactRow(A(0), true, -1, null)], Recorded));

            Assert.Throws<ArgumentException>(() => MsgstoreFactWriter.InsertBatch(
                Path,
                "batch-1",
                [new MsgstoreFactRow(A(0), true, 1, null)],
                new DateTime(2026, 10, 16, 8, 30, 0, DateTimeKind.Local)));

            Assert.Throws<ArgumentException>(() => MsgstoreFactWriter.DeleteBatch(Path, ""));

            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void AWorkspaceClaimedByAnotherOperation_IsRefused()
        {
            using (WorkspaceExclusiveLock.Acquire(Path))
            {
                Assert.Throws<InvalidOperationException>(() => MsgstoreFactWriter.InsertBatch(
                    Path, "batch-1", [new MsgstoreFactRow(A(0), true, 1, null)], Recorded));

                Assert.Throws<InvalidOperationException>(
                    () => MsgstoreFactWriter.DeleteBatch(Path, "batch-1"));
            }

            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void AMissingWorkspace_IsRefusedAndNotCreated()
        {
            var missing = System.IO.Path.Combine(_workspace.DirectoryPath, "absent.db");

            Assert.Throws<InvalidOperationException>(() => MsgstoreFactWriter.InsertBatch(
                missing, "batch-1", [new MsgstoreFactRow(1, true, 1, null)], Recorded));

            Assert.False(File.Exists(missing));
        }

        /// <remarks>
        /// The schema's own CHECK constraints hold even for a write that bypasses the writer.
        /// </remarks>
        [Theory]
        [InlineData("{0}, 1, NULL, {0}, 'b', 't'")]
        [InlineData("{0}, 2, NULL, NULL, 'b', 't'")]
        [InlineData("{0}, 1, NULL, NULL, '', 't'")]
        [InlineData("9999, 1, NULL, NULL, 'b', 't'")]
        public void TheTableConstraintsRefuseInvalidRows(string values)
        {
            using var connection = WorkspaceDatabase.OpenConnection(Path);

            Assert.Throws<SqliteException>(() => Execute(
                connection,
                "INSERT INTO AttachmentMsgstoreFact (AttachmentID, IsForwarded, ForwardScore, " +
                "LowQualityCopyOfAttachmentID, SourceBatch, RecordedDateTimeUtc) VALUES (" +
                string.Format(System.Globalization.CultureInfo.InvariantCulture, values, A(0)) + ");"));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private MsgstoreFactSet Read(IReadOnlyCollection<long> attachmentIDs)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(Path);

            return new MsgstoreFactReader().Read(connection, attachmentIDs);
        }

        private bool HasAnyCopy()
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(Path);

            return new MsgstoreFactReader().HasAnyLowQualityCopy(connection);
        }

        private long Scalar(string sql)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(Path);

            return ScalarLong(connection, sql);
        }

        private long FactCount() => Scalar("SELECT COUNT(*) FROM AttachmentMsgstoreFact;");

        private void AssertOnlyBatch(string batch, long count)
        {
            Assert.Equal(count, FactCount());
            Assert.Equal(
                count,
                Scalar($"SELECT COUNT(*) FROM AttachmentMsgstoreFact WHERE SourceBatch = '{batch}';"));
        }
    }
}
