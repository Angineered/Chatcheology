namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// The format's shape and its version rule: exactly the format's tables, and a reader that refuses
    /// what it does not know.
    /// </summary>
    public sealed class ArchiveFormatTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        private string Database => Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName);

        // ---------------------------------------------------------------------------------------
        // Schema.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Database_HoldsExactlyTheFormatsTables()
        {
            ArchiveSample.WriteDatabase(_folder.Path);

            var tables = ArchiveSample.Rows(
                Database, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");

            Assert.Equal(ArchiveFormat.TableNames.Order(StringComparer.Ordinal), tables);
        }

        [Fact]
        public void Database_HasTheDayIndex_AndTheSequenceAndMessageLookupsAreIndexed()
        {
            ArchiveSample.WriteDatabase(_folder.Path);

            var indexes = ArchiveSample.Rows(
                Database,
                "SELECT tbl_name || '.' || name FROM sqlite_master WHERE type = 'index' ORDER BY 1;");

            Assert.Contains("Message.IX_Message_DateTimeLocal", indexes);

            // The UNIQUE constraints' own indexes: one on SequenceNumber, one led by MessageID.
            Assert.Equal(
                ["SequenceNumber"],
                ArchiveSample.Rows(
                    Database,
                    "SELECT ii.name FROM pragma_index_list('Message') AS il, pragma_index_info(il.name) AS ii " +
                    "WHERE il.[unique] = 1 AND il.origin = 'u';"));

            Assert.Equal(
                ["MessageID,0"],
                ArchiveSample.Rows(
                    Database,
                    "SELECT ii.name, ii.seqno FROM pragma_index_list('Attachment') AS il, " +
                    "pragma_index_info(il.name) AS ii WHERE il.origin = 'u' AND ii.seqno = 0;"));
        }

        [Fact]
        public void Database_IsVersionOne_InRollbackJournalMode_WithNoCompanionFiles()
        {
            ArchiveSample.WriteDatabase(_folder.Path);

            Assert.Equal(["1"], ArchiveSample.Rows(Database, "PRAGMA user_version;"));
            Assert.Equal(["delete"], ArchiveSample.Rows(Database, "PRAGMA journal_mode;"));
            Assert.Equal([Database], Directory.GetFiles(_folder.Path));
        }

        [Fact]
        public void MediaFileName_IsTheHashAndALowerCasedPlainExtension()
        {
            var sha = new string('a', 64);

            Assert.Equal(sha + ".jpg", ArchiveFormat.MediaFileName(sha, "IMG-1.JPG"));
            Assert.Equal(sha + ".mp4", ArchiveFormat.MediaFileName(sha, "a.b.mp4"));
            Assert.Equal(sha, ArchiveFormat.MediaFileName(sha, "no-extension"));
            Assert.Equal(sha, ArchiveFormat.MediaFileName(sha, "odd.ext with space"));
            Assert.Throws<ArgumentException>(() => ArchiveFormat.MediaFileName(sha.ToUpperInvariant(), "a.jpg"));
        }

        [Fact]
        public void ATrailingNewline_NeverPassesAsAHashOrAnExtension()
        {
            var sha = new string('a', 64);

            Assert.True(ArchiveFormat.IsSha256(sha));
            Assert.False(ArchiveFormat.IsSha256(sha + "\n"));
            Assert.Equal(".jpg", ArchiveFormat.SafeExtension(".jpg"));
            Assert.Equal("", ArchiveFormat.SafeExtension(".jpg\n"));
            Assert.Equal(sha, ArchiveFormat.MediaFileName(sha, "a.jpg\n"));
            Assert.Throws<ArgumentException>(() => ArchiveFormat.MediaFileName(sha + "\n", "a.jpg"));
        }

        [Fact]
        public void Compaction_KeepsVacuumsTemporaryCopyInMemory()
        {
            Assert.Equal(["PRAGMA temp_store = MEMORY;", "VACUUM;"], ArchiveWriter.CompactionStatements);
        }

        [Fact]
        public void CopyWithHash_SeparatesAnUnreadableSourceFromAnUnwritableDestination()
        {
            var source = Path.Combine(_folder.Path, "source.bin");
            File.WriteAllText(source, "synthetic");

            var blocked = Path.Combine(_folder.Path, "blocked.bin");
            Directory.CreateDirectory(blocked);

            Assert.Throws<ArchiveDestinationWriteException>(() => ArchiveHashing.CopyWithHash(source, blocked));

            var missing = Assert.ThrowsAny<IOException>(
                () => ArchiveHashing.CopyWithHash(Path.Combine(_folder.Path, "absent.bin"), Path.Combine(_folder.Path, "out.bin")));

            Assert.IsNotType<ArchiveDestinationWriteException>(missing);
            Assert.False(File.Exists(Path.Combine(_folder.Path, "out.bin")));
        }

        [Fact]
        public void Reader_RefusesAnAttachmentWithAnUnusableHash()
        {
            ArchiveSample.WriteDatabase(_folder.Path);

            // Foreign keys switched off on the tampering connection, so the bad value goes in.
            ArchiveSample.Tamper(
                _folder.Path,
                "PRAGMA foreign_keys = OFF; UPDATE Attachment SET MediaSha256 = 'not-a-hash' WHERE AttachmentID = 10;");

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Throws<ArchiveFormatException>(() => reader.ReadFirstPage(ArchiveReader.MaximumPageSize));

            var bad = ArchiveSample.Attachment(
                10, 102, ArchiveAttachmentState.Restored, sha: "NOT-A-HASH", name: "a.jpg");

            Assert.Throws<ArchiveFormatException>(() => reader.MediaFullPath(bad));
        }

        [Theory]
        [InlineData("line\nbreak.jpg")]
        [InlineData("carriage\rreturn.jpg")]
        [InlineData("tab\there.jpg")]
        [InlineData("nul\0.jpg")]
        [InlineData("dir/name.jpg")]
        [InlineData("dir\\name.jpg")]
        [InlineData("C:name.jpg")]
        [InlineData("")]
        public void Writer_RefusesAnOriginalFileNameThatIsNotABareName(string name)
        {
            using var writer = Prepared();
            writer.WriteMedia(ArchiveSample.Media[0]);

            var attachment = ArchiveSample.Attachment(
                1, 1, ArchiveAttachmentState.Restored, sha: ArchiveSample.AlphaSha, name: name);

            Assert.Throws<InvalidOperationException>(
                () => writer.WriteMessage(ArchiveSample.Message(1, 1, "2026-03-02T09:00:00", 1, "x", attachment)));
        }

        // ---------------------------------------------------------------------------------------
        // Version rule.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Reader_RefusesAHigherMajorVersion()
        {
            ArchiveSample.WriteDatabase(_folder.Path);
            ArchiveSample.Tamper(_folder.Path, $"PRAGMA user_version = {ArchiveFormat.FormatVersion + 1};");

            var refusal = Assert.Throws<ArchiveFormatException>(() => ArchiveReader.Open(_folder.Path));

            Assert.Contains("format version 2", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Reader_RefusesAnArchiveTheWriterDidNotComplete()
        {
            using (var writer = ArchiveWriter.Create(Database, ArchiveSample.Info()))
            {
                writer.WriteParticipant(ArchiveSample.Participants[0]);
            }

            Assert.Throws<ArchiveFormatException>(() => ArchiveReader.Open(_folder.Path));
        }

        [Fact]
        public void Reader_RefusesAFolderWithNoDatabase()
        {
            Assert.Throws<ArchiveFormatException>(() => ArchiveReader.Open(_folder.Path));
        }

        [Fact]
        public void Reader_RefusesADatabaseMissingAFormatTable()
        {
            ArchiveSample.WriteDatabase(_folder.Path);
            ArchiveSample.Tamper(_folder.Path, "DROP TABLE AttachmentBasis;");

            Assert.Throws<ArchiveFormatException>(() => ArchiveReader.Open(_folder.Path));
        }

        [Fact]
        public void Reader_RefusesAFileThatIsNotSqlite()
        {
            File.WriteAllText(Database, "not a database at all, just synthetic text");

            Assert.Throws<ArchiveFormatException>(() => ArchiveReader.Open(_folder.Path));
        }

        // ---------------------------------------------------------------------------------------
        // Writer rules.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Writer_RefusesAnExistingFile()
        {
            File.WriteAllText(Database, "already here");

            Assert.Throws<InvalidOperationException>(() => ArchiveWriter.Create(Database, ArchiveSample.Info()));
            Assert.Equal("already here", File.ReadAllText(Database));
        }

        [Fact]
        public void Writer_RefusesAnotherFormatVersion()
        {
            Assert.Throws<InvalidOperationException>(() => ArchiveWriter.Create(Database, ArchiveSample.Info(2)));
            Assert.False(File.Exists(Database));
        }

        [Fact]
        public void Writer_RefusesARestoredAttachmentWithoutABasis()
        {
            using var writer = Prepared();

            var attachment = ArchiveSample.Attachment(
                1, 1, ArchiveAttachmentState.Restored, sha: ArchiveSample.AlphaSha, name: "a.jpg");

            var unbased = new ArchiveAttachment
            {
                AttachmentID = attachment.AttachmentID,
                MessageID = attachment.MessageID,
                Ordinal = attachment.Ordinal,
                State = attachment.State,
                MediaSha256 = attachment.MediaSha256,
                OriginalFileName = attachment.OriginalFileName,
                IsForwarded = null,
                ForwardScore = null,
                LowQualityCopyOfAttachmentID = null,
                Basis = null,
            };

            Assert.Throws<InvalidOperationException>(
                () => writer.WriteMessage(ArchiveSample.Message(1, 1, "2026-03-02T09:00:00", 1, "x", unbased)));
        }

        [Fact]
        public void Writer_RefusesAMissingAttachmentCarryingMedia()
        {
            using var writer = Prepared();

            var wrong = ArchiveSample.Attachment(
                1, 1, ArchiveAttachmentState.Missing, sha: ArchiveSample.AlphaSha, name: "a.jpg");

            Assert.Throws<InvalidOperationException>(
                () => writer.WriteMessage(ArchiveSample.Message(1, 1, "2026-03-02T09:00:00", 1, "x", wrong)));
        }

        [Fact]
        public void Writer_RefusesAUserMessageWithoutASender()
        {
            using var writer = Prepared();

            var message = new ArchiveMessage
            {
                MessageID = 1,
                SequenceNumber = 1,
                DateTimeLocal = new DateTime(2026, 3, 2, 9, 0, 0),
                Kind = ArchiveMessageKind.User,
                SenderParticipantID = null,
                Text = "x",
                IsForwarded = null,
                ForwardScore = null,
                Attachments = [],
            };

            Assert.Throws<InvalidOperationException>(() => writer.WriteMessage(message));
        }

        [Fact]
        public void Complete_RefusesMediaNobodyNames()
        {
            using var writer = Prepared();
            writer.WriteMedia(ArchiveSample.Media[0]);

            Assert.Throws<InvalidOperationException>(() => writer.Complete());
        }

        [Fact]
        public void Complete_RefusesALowQualityLinkToNoAttachment()
        {
            using var writer = Prepared();

            writer.WriteMessage(ArchiveSample.Message(
                1, 1, "2026-03-02T09:00:00", 1, "x",
                ArchiveSample.Attachment(1, 1, ArchiveAttachmentState.Unmatched, lowQualityOf: 999)));

            Assert.Throws<InvalidOperationException>(() => writer.Complete());
        }

        [Fact]
        public void Complete_RefusesAnArchiveWithNoLocalParticipant()
        {
            using var writer = ArchiveWriter.Create(Database, ArchiveSample.Info());
            writer.WriteParticipant(ArchiveSample.Participants[1]);

            Assert.Throws<InvalidOperationException>(() => writer.Complete());
        }

        [Fact]
        public void Writer_RefusesAParticipantMarkedLocalAgainstTheInfo()
        {
            using var writer = ArchiveWriter.Create(Database, ArchiveSample.Info());

            Assert.Throws<InvalidOperationException>(() => writer.WriteParticipant(
                new ArchiveParticipant { ParticipantID = ArchiveSample.Other, DisplayName = "Sam", IsLocal = true }));
        }

        [Fact]
        public void Writer_AfterComplete_WritesNothingMore()
        {
            using var writer = Prepared();
            writer.Complete();

            Assert.Throws<InvalidOperationException>(() => writer.WriteParticipant(ArchiveSample.Participants[1]));
        }

        /// <summary>A writer with both participants written.</summary>
        private ArchiveWriter Prepared()
        {
            var writer = ArchiveWriter.Create(Database, ArchiveSample.Info());

            foreach (var participant in ArchiveSample.Participants)
            {
                writer.WriteParticipant(participant);
            }

            return writer;
        }
    }
}
