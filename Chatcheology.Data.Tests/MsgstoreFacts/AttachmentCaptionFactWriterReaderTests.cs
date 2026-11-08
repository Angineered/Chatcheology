using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Tests.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.MsgstoreFacts
{
    /// <summary>
    /// The attachment caption writer and reader (CH-63, CH-71): batches go in and come out whole,
    /// the text is stored exactly as given, and every refusal writes nothing.
    /// </summary>
    /// <remarks>Synthetic workspaces only: invented messages and attachments, invented captions.</remarks>
    public sealed class AttachmentCaptionFactWriterReaderTests : IDisposable
    {
        private static readonly DateTime Recorded = new(2026, 11, 8, 8, 30, 0, DateTimeKind.Utc);

        private readonly ViewerTestWorkspace _workspace = new();

        private readonly long[] _attachments;

        public AttachmentCaptionFactWriterReaderTests()
        {
            var date = new DateOnly(2026, 3, 2);

            _attachments = Enumerable.Range(0, 4)
                .Select(index =>
                {
                    var message = _workspace.AddMessage("<Media omitted>", date, ViewerTestWorkspace.Minute(index));

                    return _workspace.AddAttachments(message)[0];
                })
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

        private IReadOnlyDictionary<long, AttachmentCaptionFact> Read(IReadOnlyCollection<long> ids)
        {
            using var connection = _workspace.OpenReadOnly();

            return new AttachmentCaptionFactReader().Read(connection, ids);
        }

        private long FactCount() =>
            _workspace.ScalarLongReadOnly("SELECT COUNT(*) FROM AttachmentCaptionFact;");

        [Fact]
        public void InsertBatch_StoresTheExactTextAndTheReaderReturnsIt()
        {
            const string Untrimmed = "  Synthetic caption\r\nwith a second line ‎ ";

            var written = AttachmentCaptionFactWriter.InsertBatch(
                Path,
                "caption-1",
                [
                    new AttachmentCaptionFactRow(A(0), Untrimmed, false),
                    new AttachmentCaptionFactRow(A(1), "Synthetic partner caption", true),
                ],
                Recorded);

            Assert.Equal(2, written);

            var facts = Read([A(0), A(1), A(2)]);

            Assert.Equal(2, facts.Count);
            Assert.Equal(Untrimmed, facts[A(0)].CaptionText);
            Assert.False(facts[A(0)].FromLowQualityCopy);
            Assert.True(facts[A(1)].FromLowQualityCopy);
            Assert.Equal("caption-1", facts[A(0)].SourceBatch);
            Assert.Equal(Recorded, facts[A(0)].RecordedDateTimeUtc);
            Assert.Equal(DateTimeKind.Utc, facts[A(0)].RecordedDateTimeUtc.Kind);
        }

        [Fact]
        public void DeleteBatch_RemovesOnlyThatBatch()
        {
            AttachmentCaptionFactWriter.InsertBatch(Path, "caption-1", [new AttachmentCaptionFactRow(A(0), "One", false)], Recorded);
            AttachmentCaptionFactWriter.InsertBatch(Path, "caption-2", [new AttachmentCaptionFactRow(A(1), "Two", false)], Recorded);

            Assert.Equal(1, AttachmentCaptionFactWriter.DeleteBatch(Path, "caption-1"));
            Assert.Equal(0, AttachmentCaptionFactWriter.DeleteBatch(Path, "caption-1"));

            var left = Read([A(0), A(1)]);

            Assert.Single(left);
            Assert.Equal("caption-2", left[A(1)].SourceBatch);
        }

        [Fact]
        public void TheReaderIssuesOnlySelectStatements()
        {
            foreach (var statement in AttachmentCaptionFactReader.Statements)
            {
                Assert.StartsWith("SELECT", statement.TrimStart(), StringComparison.Ordinal);

                foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE", "REPLACE", "CREATE", "DROP", "ALTER" })
                {
                    Assert.DoesNotContain(verb, statement, StringComparison.OrdinalIgnoreCase);
                }
            }

            Assert.Empty(Read([]));
        }

        [Fact]
        public void AnEmptyCaption_IsRefusedAndNothingIsWritten()
        {
            Assert.Throws<ArgumentException>(() => AttachmentCaptionFactWriter.InsertBatch(
                Path,
                "caption-1",
                [new AttachmentCaptionFactRow(A(0), "Fine", false), new AttachmentCaptionFactRow(A(1), "", false)],
                Recorded));

            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void BadBatches_AreRefusedAndNothingIsWritten()
        {
            SqliteConnection.ClearAllPools();
            var before = WorkspaceCheckpoint.ComputeFileSha256(Path);

            Assert.Throws<ArgumentException>(() => AttachmentCaptionFactWriter.InsertBatch(Path, " ", [new AttachmentCaptionFactRow(A(0), "x", false)], Recorded));
            Assert.Throws<ArgumentException>(() => AttachmentCaptionFactWriter.InsertBatch(Path, "caption-1", [], Recorded));
            Assert.Throws<ArgumentException>(() => AttachmentCaptionFactWriter.InsertBatch(
                Path, "caption-1", [new AttachmentCaptionFactRow(A(0), "x", false)], new DateTime(2026, 11, 8, 8, 30, 0, DateTimeKind.Local)));
            Assert.Throws<ArgumentException>(() => AttachmentCaptionFactWriter.InsertBatch(
                Path, "caption-1", [new AttachmentCaptionFactRow(A(0), "x", false), new AttachmentCaptionFactRow(A(0), "y", false)], Recorded));

            // An unknown attachment refuses the whole batch, including the good row before it.
            Assert.Throws<InvalidOperationException>(() => AttachmentCaptionFactWriter.InsertBatch(
                Path, "caption-1", [new AttachmentCaptionFactRow(A(0), "x", false), new AttachmentCaptionFactRow(999_999, "y", false)], Recorded));

            SqliteConnection.ClearAllPools();
            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(Path));
            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void ASecondCaptionForAnAttachment_IsRefusedFromAnyBatch()
        {
            AttachmentCaptionFactWriter.InsertBatch(Path, "caption-1", [new AttachmentCaptionFactRow(A(0), "One", false)], Recorded);

            var same = Assert.Throws<InvalidOperationException>(() => AttachmentCaptionFactWriter.InsertBatch(
                Path, "caption-1", [new AttachmentCaptionFactRow(A(0), "One", false)], Recorded));
            var other = Assert.Throws<InvalidOperationException>(() => AttachmentCaptionFactWriter.InsertBatch(
                Path, "caption-2", [new AttachmentCaptionFactRow(A(1), "Two", false), new AttachmentCaptionFactRow(A(0), "Again", false)], Recorded));

            Assert.Contains("this batch", same.Message, StringComparison.Ordinal);
            Assert.Contains("another batch", other.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Again", other.Message, StringComparison.Ordinal);
            Assert.Equal(1, FactCount());
        }

        [Fact]
        public void AWorkspaceClaimedByAnotherOperation_IsRefused()
        {
            using (WorkspaceExclusiveLock.Acquire(Path))
            {
                Assert.Throws<InvalidOperationException>(() => AttachmentCaptionFactWriter.InsertBatch(
                    Path, "caption-1", [new AttachmentCaptionFactRow(A(0), "x", false)], Recorded));

                Assert.Throws<InvalidOperationException>(() => AttachmentCaptionFactWriter.DeleteBatch(Path, "caption-1"));
            }

            Assert.Equal(0, FactCount());
        }

        [Fact]
        public void AMissingWorkspace_IsRefusedAndNotCreated()
        {
            var missing = System.IO.Path.Combine(_workspace.DirectoryPath, "absent.db");

            Assert.Throws<InvalidOperationException>(() => AttachmentCaptionFactWriter.InsertBatch(
                missing, "caption-1", [new AttachmentCaptionFactRow(1, "x", false)], Recorded));
            Assert.Throws<InvalidOperationException>(() => AttachmentCaptionFactWriter.DeleteBatch(missing, "caption-1"));

            Assert.False(File.Exists(missing));
        }

        [Fact]
        public void TheSchemaRefusesAnEmptyCaptionEvenWithoutTheWriter()
        {
            using var connection = WorkspaceDatabase.OpenConnection(Path);

            void Insert(long attachment, string caption, int fromCopy)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO AttachmentCaptionFact (AttachmentID, CaptionText, FromLowQualityCopy, SourceBatch, RecordedDateTimeUtc)
                    VALUES ($a, $c, $f, 'b', '2026-11-08T00:00:00.0000000Z');
                    """;
                command.Parameters.AddWithValue("$a", attachment);
                command.Parameters.AddWithValue("$c", caption);
                command.Parameters.AddWithValue("$f", fromCopy);
                command.ExecuteNonQuery();
            }

            Insert(A(0), "ok", 0);

            Assert.Throws<SqliteException>(() => Insert(A(1), "", 0));
            Assert.Throws<SqliteException>(() => Insert(A(2), "x", 2));
            Assert.Throws<SqliteException>(() => Insert(999_999, "x", 0));
            Assert.Throws<SqliteException>(() => Insert(A(0), "again", 0));
        }
    }
}
