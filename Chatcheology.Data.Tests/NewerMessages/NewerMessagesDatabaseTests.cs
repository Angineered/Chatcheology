using Chatcheology.Data.NewerMessages;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.NewerMessages
{
    /// <summary>newer-messages.db (CH-71 step 3a): schema, marker, append-only writer guards, reader. Synthetic only.</summary>
    public sealed class NewerMessagesDatabaseTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
        private static readonly NewerMessagesInfo Info = new(1, 2, 3, 7, Now);

        private readonly string _folder = Path.Combine(Path.GetTempPath(), "Chatcheology.NewerMessages.Tests", Guid.NewGuid().ToString("N"));

        public NewerMessagesDatabaseTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // A temporary folder outside the repository; leaving it is harmless.
            }
        }

        private string NewerPath => Path.Combine(_folder, NewerMessagesDatabase.FileName);

        private static NewerRefreshRun Run(string sha, string handOff, string handOffKey, string? root = null) => new()
        {
            RunDateTimeUtc = Now,
            ManifestSha256 = sha,
            MsgstoreSizeBytes = 4096,
            MsgstoreUserVersion = 1,
            MsgstoreSchemaSha256 = new string('c', 64),
            HandOff = handOff,
            HandOffKeyID = handOffKey,
            MediaRootPath = root,
        };

        private static NewerMessageRow Message(string key, long utcMs, bool fromMe, NewerAttachmentRow? attachment = null, string kind = "User") => new()
        {
            KeyID = key,
            MsgstoreRowID = utcMs % 100000,
            SortID = utcMs % 100000,
            FromMe = fromMe,
            SenderParticipantID = kind == NewerMessagesDatabase.KindSystem ? null : fromMe ? 2 : 3,
            TimestampUtcMs = utcMs,
            DateTimeLocal = NewerMessagesTimeZone.Format(NewerMessagesTimeZone.ToLocal(utcMs)!.Value),
            MessageType = attachment is null ? 0 : 1,
            Kind = kind,
            PlaceholderKind = kind == NewerMessagesDatabase.KindPlaceholder ? "Poll" : null,
            Text = attachment is null ? "synthetic text" : null,
            Attachment = attachment,
        };

        private static readonly long T0 = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        [Fact]
        public void The_first_run_creates_the_file_and_reads_back_in_timeline_order()
        {
            var root = Path.Combine(_folder, "root");
            var rows = new[]
            {
                Message("K1", T0, true),
                Message("K2", T0 + 1000, false, new NewerAttachmentRow
                {
                    FileName = "a.jpg", FileSizeBytes = 5, RelativePath = "Media/a.jpg", State = NewerMessagesDatabase.StateRestored,
                    Sha256 = new string('a', 64), FileHash = "x",
                }),
                Message("K3", T0 + 2000, false, new NewerAttachmentRow
                {
                    FileName = "b.jpg", FileSizeBytes = 3, State = NewerMessagesDatabase.StateUnmatched, LowQualityCopyOfKeyID = "K2",
                }),
            };

            var result = NewerMessagesWriter.Append(NewerPath, Info, Run(new string('1', 64), NewerMessagesDatabase.HandOffWorkspace, "K0", root), rows);

            Assert.True(result.Created);
            Assert.Equal((3, 2, 1), (result.Messages, result.Attachments, result.Links));

            var read = NewerMessagesReader.Read(NewerPath);
            Assert.Equal(Info with { CreatedDateTimeUtc = Now }, read.Info);
            Assert.Equal(["K1", "K2", "K3"], read.Messages.Select(m => m.KeyID));
            Assert.Equal("K3", read.LastKeyID);
            Assert.Equal(Path.GetFullPath(root), read.Messages[1].Attachment!.MediaRootPath);
            Assert.Null(read.Messages[2].Attachment!.MediaRootPath);
            Assert.Equal(read.Messages[1].Attachment!.NewerAttachmentID, read.Messages[2].Attachment!.LowQualityCopyOfAttachmentID);
            Assert.Equal((1, 1), (read.Runs[0].Restored, read.Runs[0].Unmatched));
            Assert.Equal("2026-09-01T12:00:00", read.Messages[0].DateTimeLocal);
        }

        [Fact]
        public void The_same_manifest_twice_writes_nothing()
        {
            var run = Run(new string('1', 64), NewerMessagesDatabase.HandOffWorkspace, "K0");
            NewerMessagesWriter.Append(NewerPath, Info, run, [Message("K1", T0, true)]);
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(NewerPath);

            var again = NewerMessagesWriter.Append(NewerPath, Info, run, [Message("K1", T0, true)]);

            Assert.True(again.AlreadyApplied);
            Assert.Equal(before, File.ReadAllBytes(NewerPath));
        }

        [Fact]
        public void A_later_run_must_continue_from_the_last_imported_key()
        {
            NewerMessagesWriter.Append(NewerPath, Info, Run(new string('1', 64), NewerMessagesDatabase.HandOffWorkspace, "K0"), [Message("K1", T0, true)]);
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(NewerPath);

            Assert.Throws<InvalidOperationException>(() => NewerMessagesWriter.Append(
                NewerPath, Info, Run(new string('2', 64), NewerMessagesDatabase.HandOffWorkspace, "K0"), [Message("K2", T0 + 1, true)]));
            Assert.Throws<InvalidOperationException>(() => NewerMessagesWriter.Append(
                NewerPath, Info, Run(new string('2', 64), NewerMessagesDatabase.HandOffPreviousRun, "K9"), [Message("K2", T0 + 1, true)]));
            Assert.Throws<InvalidOperationException>(() => NewerMessagesWriter.Append(
                NewerPath, Info, Run(new string('2', 64), NewerMessagesDatabase.HandOffPreviousRun, "K1"), [Message("K1", T0, true)]));
            Assert.Throws<InvalidOperationException>(() => NewerMessagesWriter.Append(
                NewerPath, Info with { ChatRowID = 8 }, Run(new string('2', 64), NewerMessagesDatabase.HandOffPreviousRun, "K1"), [Message("K2", T0 + 1, true)]));

            SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(NewerPath));

            var ok = NewerMessagesWriter.Append(NewerPath, Info, Run(new string('2', 64), NewerMessagesDatabase.HandOffPreviousRun, "K1"), [Message("K2", T0 + 1, true)]);
            Assert.False(ok.Created);
            Assert.Equal([1L, 2L], NewerMessagesReader.Read(NewerPath).Messages.Select(m => m.NewerMessageID));
        }

        [Fact]
        public void A_first_run_that_fails_leaves_no_file()
        {
            var bad = Message("K1", T0, true);
            var wrongSender = new NewerMessageRow
            {
                KeyID = "K2", MsgstoreRowID = 2, SortID = 2, FromMe = true, SenderParticipantID = 3, TimestampUtcMs = T0,
                DateTimeLocal = bad.DateTimeLocal, Kind = NewerMessagesDatabase.KindUser, Text = "x",
            };

            Assert.Throws<ArgumentException>(() => NewerMessagesWriter.Append(
                NewerPath, Info, Run(new string('1', 64), NewerMessagesDatabase.HandOffWorkspace, "K0"), [bad, wrongSender]));
            Assert.Throws<InvalidOperationException>(() => NewerMessagesWriter.Append(
                NewerPath, Info, Run(new string('1', 64), NewerMessagesDatabase.HandOffPreviousRun, "K0"), [bad]));

            // A Restored attachment without a media root is refused by the writer, and a broken row by the schema.
            Assert.Throws<ArgumentException>(() => NewerMessagesWriter.Append(
                NewerPath, Info, Run(new string('1', 64), NewerMessagesDatabase.HandOffWorkspace, "K0"),
                [Message("K1", T0, true, new NewerAttachmentRow { State = NewerMessagesDatabase.StateRestored, Sha256 = new string('a', 64), RelativePath = "a" })]));
            Assert.ThrowsAny<SqliteException>(() => NewerMessagesWriter.Append(
                NewerPath, Info, Run(new string('1', 64), NewerMessagesDatabase.HandOffWorkspace, "K0", _folder),
                [Message("K1", T0, true, new NewerAttachmentRow { State = "Missing" })]));

            SqliteConnection.ClearAllPools();
            Assert.False(File.Exists(NewerPath));
        }

        [Fact]
        public void A_workspace_is_never_opened_as_a_newer_messages_file()
        {
            WorkspaceDatabase.Initialise(NewerPath);
            SqliteConnection.ClearAllPools();

            Assert.Throws<InvalidOperationException>(() => NewerMessagesReader.Read(NewerPath));
            Assert.Throws<InvalidOperationException>(() => NewerMessagesWriter.Append(
                NewerPath, Info, Run(new string('1', 64), NewerMessagesDatabase.HandOffPreviousRun, "K0"), [Message("K1", T0, true)]));
        }

        [Fact]
        public void Any_other_file_name_is_refused()
        {
            var path = Path.Combine(_folder, "workspace.db");

            Assert.Throws<ArgumentException>(() => NewerMessagesWriter.Append(
                path, Info, Run(new string('1', 64), NewerMessagesDatabase.HandOffWorkspace, "K0"), [Message("K1", T0, true)]));
            Assert.False(File.Exists(path));
            Assert.Null(NewerMessagesReader.ReadBeside(path));
        }

        [Fact]
        public void Archive_ids_follow_the_earlier_parts_and_never_shift()
        {
            Assert.Equal(1_005, NewerMessagesArchiveIds.MessageID(1_000, 5));
            Assert.Equal(205, NewerMessagesArchiveIds.AttachmentID(200, 5));
        }
    }
}
