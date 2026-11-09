using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.NewerMessages;
using Chatcheology.Data.Tests.Matching;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.ArchiveExport.ArchiveExportFixture;

namespace Chatcheology.Data.Tests.ArchiveExport
{
    /// <summary>
    /// CH-71 step 3b: the archive export reads <c>newer-messages.db</c> beside the workspace and adds its messages after
    /// the workspace conversation (<c>Source = Msgstore</c>), numbered by <see cref="NewerMessagesArchiveIds"/>; restored
    /// files copied and hashed again; captions, forwarded facts, low-quality links and placeholders as stored; a damaged
    /// file refused before anything is written; the comparer and the preview count them. Invented text, ids and bytes only.
    /// </summary>
    public sealed class ArchiveExportNewerMessagesTests : IDisposable
    {
        private const long Local = MatchingTestWorkspace.LocalParticipantID;
        private const long Other = MatchingTestWorkspace.OtherParticipantID;

        private static readonly DateTime RunTime = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);

        private static readonly byte[] FullPhoto = Encoding.UTF8.GetBytes("invented newer full-size photo bytes");
        private static readonly byte[] SmallPhoto = Encoding.UTF8.GetBytes("invented newer small copy");

        private readonly ArchiveExportFixture _fixture = new();
        private readonly string _phoneRoot;
        private int _runs;

        public ArchiveExportNewerMessagesTests()
        {
            _phoneRoot = Path.Combine(Path.GetDirectoryName(_fixture.ExportsFolder)!, "phone-copy", "WhatsApp");
            Directory.CreateDirectory(_phoneRoot);
        }

        public void Dispose() => _fixture.Dispose();

        private string NewerPath => NewerMessagesDatabase.PathBeside(_fixture.DatabasePath);

        private static NewerMessagesInfo Info(long local = Local, long other = Other, DateOnly? lastDay = null) =>
            new(MatchingTestWorkspace.ConversationID, local, other, 7, lastDay ?? Day, RunTime);

        // -------------------------------------------------------------------------------------
        // Synthetic building blocks.
        // -------------------------------------------------------------------------------------

        /// <summary>The workspace: a text, a restored photo, a text. Returns the photo's attachment.</summary>
        private long BuildWorkspace()
        {
            _fixture.AddText("Invented first line", Local);
            var photo = _fixture.AddPhoto(Other);
            _fixture.AddText("Invented last workspace line", Other);

            var payload = _fixture.AddPayload("invented workspace payload", ("workspace-photo.jpg", Copy.Good));
            _fixture.RunEvidence();
            _fixture.Confirm(photo, payload.AssetID);

            return photo;
        }

        private static long Utc(DateOnly day, int hour, int minute) =>
            new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(2)).ToUnixTimeMilliseconds();

        private static NewerMessageRow Row(
            string key, int minute, bool fromMe, string kind = NewerMessagesDatabase.KindUser, string? text = null,
            NewerAttachmentRow? attachment = null, string? caption = null, bool? forwarded = null, int? score = null,
            string? placeholderKind = null, long localID = Local, long otherID = Other)
        {
            var utc = Utc(Day.AddDays(1), 10, minute);

            return new NewerMessageRow
            {
                KeyID = key,
                MsgstoreRowID = 100 + minute,
                SortID = 100 + minute,
                FromMe = fromMe,
                SenderParticipantID = kind == NewerMessagesDatabase.KindSystem ? null : fromMe ? localID : otherID,
                TimestampUtcMs = utc,
                DateTimeLocal = NewerMessagesTimeZone.Format(NewerMessagesTimeZone.ToLocal(utc)!.Value),
                MessageType = attachment is not null ? 1 : kind == NewerMessagesDatabase.KindSystem ? 7 : kind == NewerMessagesDatabase.KindPlaceholder ? 66 : 0,
                Kind = kind,
                PlaceholderKind = placeholderKind,
                Text = text,
                Caption = caption,
                IsForwarded = forwarded,
                ForwardScore = score,
                Attachment = attachment,
            };
        }

        /// <summary>A file in the phone copy; returns its relative path and SHA-256.</summary>
        private (string Relative, string Sha256) PhoneFile(string relative, byte[] bytes)
        {
            var full = Path.Combine(_phoneRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);

            return (relative, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        private static NewerAttachmentRow Restored((string Relative, string Sha256) file, string? lowQualityOf = null) => new()
        {
            FileName = file.Relative[(file.Relative.LastIndexOf('/') + 1)..],
            FileSizeBytes = 10,
            MimeType = "image/jpeg",
            FileHash = "invented",
            RelativePath = file.Relative,
            State = NewerMessagesDatabase.StateRestored,
            Sha256 = file.Sha256,
            LowQualityCopyOfKeyID = lowQualityOf,
        };

        private static NewerAttachmentRow Unmatched() => new()
        {
            FileName = "IMG-NEWER-LOST.jpg",
            State = NewerMessagesDatabase.StateUnmatched,
        };

        private NewerRefreshRun Run(string handOff, string handOffKey, bool handOffFromMe) => new()
        {
            RunDateTimeUtc = RunTime.AddDays(_runs),
            ManifestSha256 = new string((char)('1' + _runs++), 64),
            MsgstoreSizeBytes = 4096,
            MsgstoreUserVersion = 1,
            MsgstoreSchemaSha256 = new string('c', 64),
            HandOff = handOff,
            HandOffKeyID = handOffKey,
            HandOffFromMe = handOffFromMe,
            MediaRootPath = _phoneRoot,
        };

        /// <summary>The first run: text, two restored photos (the second the first's small copy), an unmatched picture, a poll and a system row.</summary>
        private void FirstRun(NewerMessagesInfo? info = null)
        {
            var full = PhoneFile("Media/WhatsApp Images/IMG-NEWER-0001.jpg", FullPhoto);
            var small = PhoneFile("Media/WhatsApp Images/Sent/IMG-NEWER-0002.JPG", SmallPhoto);

            NewerMessagesWriter.Append(NewerPath, info ?? Info(), Run(NewerMessagesDatabase.HandOffWorkspace, "K0", false),
            [
                Row("K1", 1, false, text: "Invented newer text", forwarded: true, score: 2),
                Row("K2", 2, true, attachment: Restored(full), caption: "Invented newer caption", forwarded: false),
                Row("K3", 3, true, attachment: Restored(small, lowQualityOf: "K2")),
                Row("K4", 4, false, attachment: Unmatched(), caption: "Invented caption of a lost picture"),
                Row("K5", 5, false, NewerMessagesDatabase.KindPlaceholder, "Poll (not shown in this archive)", placeholderKind: "Poll"),
                Row("K6", 6, false, NewerMessagesDatabase.KindSystem, "System message (not shown in this archive)"),
            ]);

            SqliteConnection.ClearAllPools();
        }

        private void SecondRun()
        {
            NewerMessagesWriter.Append(NewerPath, Info(), Run(NewerMessagesDatabase.HandOffPreviousRun, "K6", false),
            [
                Row("K7", 7, true, text: "Invented text of the second run"),
            ]);

            SqliteConnection.ClearAllPools();
        }

        private ArchiveExportResult Export(string output) =>
            new ArchiveExporter().Export(_fixture.Request(output, 1, 0));

        private ArchiveComparisonResult Compare(string archive) =>
            new ArchiveWorkspaceComparer().Compare(new ArchiveComparisonRequest
            {
                WorkspacePath = _fixture.DatabasePath,
                ConversationID = MatchingTestWorkspace.ConversationID,
                LocalParticipantID = Local,
                ArchivePath = archive,
            });

        private static List<ArchiveMessage> Read(string archive)
        {
            using var reader = ArchiveReader.Open(archive);

            return reader.ReadAllPages().SelectMany(p => p.Messages).ToList();
        }

        // -------------------------------------------------------------------------------------
        // The newer part of the archive.
        // -------------------------------------------------------------------------------------

        [Fact]
        public void Newer_messages_follow_the_workspace_with_msgstore_source_fixed_ids_captions_and_placeholders()
        {
            var workspacePhoto = BuildWorkspace();
            FirstRun();

            var output = _fixture.Output();
            var result = Export(output);

            Assert.Equal(6, result.Counts.MsgstoreMessages);
            Assert.Equal(3, result.Counts.WorkspaceMessages);
            Assert.Equal(9, result.Counts.Messages);
            Assert.Equal(2, result.Counts.Captions);
            Assert.Equal(3, result.Counts.Restored);
            Assert.Equal(1, result.Counts.Unmatched);

            var validation = ArchiveValidator.Validate(output);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

            var messages = Read(output);
            Assert.Equal(Enumerable.Range(1, 9), messages.Select(m => m.SequenceNumber));
            Assert.All(messages.Take(3), m => Assert.Equal(ArchiveMessageSource.Workspace, m.Source));
            Assert.All(messages.Skip(3), m => Assert.Equal(ArchiveMessageSource.Msgstore, m.Source));

            // Ids: right above the workspace's own (no older exports here), base + NewerMessageID / NewerAttachmentID.
            var messageBase = messages.Take(3).Max(m => m.MessageID);
            var attachmentBase = workspacePhoto;
            Assert.Equal(Enumerable.Range(1, 6).Select(i => messageBase + i), messages.Skip(3).Select(m => m.MessageID));

            var newer = messages.Skip(3).ToList();

            // K1: text, the forwarded fact on the message.
            Assert.Equal((ArchiveMessageKind.User, (long?)Other, "Invented newer text"), (newer[0].Kind, newer[0].SenderParticipantID, newer[0].Text));
            Assert.Equal((true, 2), (newer[0].IsForwarded, newer[0].ForwardScore));
            Assert.Equal(Day.AddDays(1).ToDateTime(new TimeOnly(10, 1)), newer[0].DateTimeLocal);

            // K2: a restored photo; empty text; caption and forwarded fact on the attachment.
            var photo = Assert.Single(newer[1].Attachments);
            Assert.Equal(string.Empty, newer[1].Text);
            Assert.Null(newer[1].IsForwarded);
            Assert.Equal(attachmentBase + 1, photo.AttachmentID);
            Assert.Equal(ArchiveAttachmentState.Restored, photo.State);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(FullPhoto)).ToLowerInvariant(), photo.MediaSha256);
            Assert.Equal("IMG-NEWER-0001.jpg", photo.OriginalFileName);
            Assert.Equal("Invented newer caption", photo.Caption);
            Assert.Equal(false, photo.IsForwarded);
            Assert.Equal(NewerMessagesArchiveSource.BasisType, photo.Basis!.BasisType);
            Assert.Equal(RunTime, photo.Basis.ConfirmedUtc);

            // K3: its low-quality copy, linked by archive id.
            var copy = Assert.Single(newer[2].Attachments);
            Assert.Equal(attachmentBase + 2, copy.AttachmentID);
            Assert.Equal(photo.AttachmentID, copy.LowQualityCopyOfAttachmentID);

            // K4: unmatched, no file, its caption kept.
            var lost = Assert.Single(newer[3].Attachments);
            Assert.Equal((ArchiveAttachmentState.Unmatched, (string?)null, (string?)null), (lost.State, lost.MediaSha256, lost.OriginalFileName));
            Assert.Equal("Invented caption of a lost picture", lost.Caption);
            Assert.Null(lost.Basis);

            // K5: a placeholder is a user message of its sender with the fixed wording only.
            Assert.Equal((ArchiveMessageKind.User, (long?)Other, "Poll (not shown in this archive)"), (newer[4].Kind, newer[4].SenderParticipantID, newer[4].Text));
            Assert.Empty(newer[4].Attachments);

            // K6: a system row is a System message with no sender.
            Assert.Equal((ArchiveMessageKind.System, (long?)null, "System message (not shown in this archive)"), (newer[5].Kind, newer[5].SenderParticipantID, newer[5].Text));

            // The restored files were copied under their hash, the extension lower-cased; the media rows describe them.
            var media = Path.Combine(output, ArchiveFormat.MediaFolderName);
            Assert.Equal(FullPhoto, File.ReadAllBytes(Path.Combine(media, photo.MediaSha256 + ".jpg")));
            Assert.Equal(SmallPhoto, File.ReadAllBytes(Path.Combine(media, copy.MediaSha256 + ".jpg")));

            using (var reader = ArchiveReader.Open(output))
            {
                var row = reader.ReadMediaOf(photo.MediaSha256!)!;
                Assert.Equal(("Image", (long)FullPhoto.Length), (row.MediaType, row.SizeBytes));
            }

            var comparison = Compare(output);
            Assert.True(comparison.Passed, string.Join(", ", comparison.FirstMismatches));
            Assert.Equal(6, comparison.MsgstoreMessages);
            Assert.Equal(9, comparison.MessagesCompared);
        }

        [Fact]
        public void Adding_newer_messages_never_moves_an_earlier_message_or_attachment()
        {
            BuildWorkspace();

            var before = _fixture.Output("before");
            Export(before);

            FirstRun();
            var once = _fixture.Output("once");
            Export(once);

            SecondRun();
            var twice = _fixture.Output("twice");
            Export(twice);

            var a = Read(before);
            var b = Read(once);
            var c = Read(twice);

            Assert.Equal((3, 9, 10), (a.Count, b.Count, c.Count));

            // Every field the phone's refresh digest covers (and the ids), over each earlier range.
            Assert.Equal(Fingerprint(a), Fingerprint(b.Take(a.Count)));
            Assert.Equal(Fingerprint(b), Fingerprint(c.Take(b.Count)));

            Assert.Equal(10, c[^1].SequenceNumber);
            Assert.Equal(c.Take(3).Max(m => m.MessageID) + 7, c[^1].MessageID);
            Assert.True(Compare(twice).Passed);
        }

        private static List<string> Fingerprint(IEnumerable<ArchiveMessage> messages) =>
            messages.Select(m => string.Join(
                "|",
                m.MessageID, m.SequenceNumber, ArchiveFormat.FormatLocal(m.DateTimeLocal), m.Kind, m.SenderParticipantID, m.Source, m.Text,
                string.Join(";", m.Attachments.Select(x => $"{x.AttachmentID},{x.Ordinal},{x.State},{x.MediaSha256},{x.LowQualityCopyOfAttachmentID}"))))
                .ToList();

        [Fact]
        public void With_no_newer_file_nothing_changes_and_the_preview_counts_newer_messages()
        {
            BuildWorkspace();

            var preview = new ArchiveExporter().Preview(_fixture.DatabasePath, MatchingTestWorkspace.ConversationID, Local);
            Assert.Equal(0, preview.NewerMessages);
            Assert.Equal(0, Export(_fixture.Output("plain")).Counts.MsgstoreMessages);
            Assert.True(Compare(_fixture.Output("plain")).Passed);

            FirstRun();

            var after = new ArchiveExporter().Preview(_fixture.DatabasePath, MatchingTestWorkspace.ConversationID, Local);
            Assert.Equal(preview with { NewerMessages = 6 }, after);
        }

        [Fact]
        public void The_comparer_notices_newer_messages_on_one_side_only()
        {
            BuildWorkspace();
            Export(_fixture.Output("plain"));

            FirstRun();

            // The older archive lacks them...
            var missing = Compare(_fixture.Output("plain"));
            Assert.Equal(6, missing.MismatchCounts[ArchiveMismatchKind.MessageMissingFromArchive]);
            Assert.Equal(1, missing.MismatchCounts[ArchiveMismatchKind.SourceCountDiffers]);

            // ...and an archive with them, compared once the file is gone, holds messages the sources do not.
            Export(_fixture.Output("newer"));
            File.Move(NewerPath, NewerPath + ".moved");

            var extra = Compare(_fixture.Output("newer"));
            Assert.Equal(6, extra.MismatchCounts[ArchiveMismatchKind.MessageNotInWorkspace]);
            Assert.Equal(1, extra.MismatchCounts[ArchiveMismatchKind.SourceCountDiffers]);
            Assert.False(extra.Passed);
        }

        // -------------------------------------------------------------------------------------
        // Refusals: nothing written.
        // -------------------------------------------------------------------------------------

        [Fact]
        public void A_damaged_newer_file_refuses_the_export_before_anything_is_written()
        {
            BuildWorkspace();
            FirstRun();

            var bytes = File.ReadAllBytes(NewerPath);

            // Every page after the header overwritten: SQLite cannot read the tables.
            var damaged = bytes.ToArray();
            Array.Fill(damaged, (byte)0x5A, 4096, damaged.Length - 4096);
            File.WriteAllBytes(NewerPath, damaged);

            AssertRefusedAsDamaged();

            // Not a database at all.
            File.WriteAllText(NewerPath, "invented, not a database");
            AssertRefusedAsDamaged();

            // A database that is not a newer-messages file (here: a copy of the workspace).
            SqliteConnection.ClearAllPools();
            File.Copy(_fixture.DatabasePath, NewerPath, overwrite: true);
            AssertRefusedAsDamaged();
        }

        private void AssertRefusedAsDamaged()
        {
            SqliteConnection.ClearAllPools();

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(_fixture.Output()));
            Assert.Contains("newer-messages file", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_phoneRoot, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(NewerPath, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(_fixture.ExportsContent());

            Assert.Throws<ArchiveExportRefusedException>(
                () => new ArchiveExporter().Preview(_fixture.DatabasePath, MatchingTestWorkspace.ConversationID, Local));
            Assert.Throws<ArchiveComparisonRefusedException>(() => Compare(_fixture.Output()));
        }

        [Fact]
        public void A_row_that_breaks_the_files_rules_refuses_the_export()
        {
            BuildWorkspace();
            FirstRun();

            // A write the writer never makes: a Restored attachment's path that leaves the media folder.
            using (var connection = new SqliteConnection($"Data Source={NewerPath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE NewerAttachment SET RelativePath = 'Media/../../escape.jpg' WHERE NewerAttachmentID = 1;";
                command.ExecuteNonQuery();
            }

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(_fixture.Output()));
            Assert.Contains("leaves the registered media folder", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void A_placeholder_whose_text_is_not_a_fixed_wording_refuses_the_export()
        {
            BuildWorkspace();
            FirstRun();

            // A write the import never makes: a placeholder carrying other text (here invented), which the phone would
            // show muted as if it were a fixed wording.
            using (var connection = new SqliteConnection($"Data Source={NewerPath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE NewerMessage SET Text = 'Invented poll question' WHERE Kind = 'Placeholder';";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(_fixture.Output()));
            Assert.Contains("1 row(s) of the newer-messages file break its own rules", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Invented poll question", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());

            // Every fixed wording is accepted.
            foreach (var wording in NewerPlaceholderWordings.All)
            {
                using (var connection = new SqliteConnection($"Data Source={NewerPath};Pooling=False"))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "UPDATE NewerMessage SET Text = $text WHERE Kind = 'Placeholder';";
                    command.Parameters.AddWithValue("$text", wording);
                    command.ExecuteNonQuery();
                }

                SqliteConnection.ClearAllPools();
                Assert.Equal(6, new ArchiveExporter().Preview(_fixture.DatabasePath, MatchingTestWorkspace.ConversationID, Local).NewerMessages);
            }
        }

        [Theory]
        [InlineData(@"F:\", "Media/WhatsApp Images/IMG-1.jpg", @"F:\Media\WhatsApp Images\IMG-1.jpg")]
        [InlineData(@"F:\Phone\WhatsApp", "Media/a.jpg", @"F:\Phone\WhatsApp\Media\a.jpg")]
        [InlineData(@"F:\Phone\WhatsApp\", "Media/a.jpg", @"F:\Phone\WhatsApp\Media\a.jpg")]
        [InlineData(@"F:\", "Media/../../a.jpg", null)]
        [InlineData(@"F:\", "G:/a.jpg", null)]
        public void A_drive_root_media_folder_holds_its_restored_files(string root, string relative, string? expected)
        {
            Assert.Equal(expected, NewerMessagesArchiveSource.FullPathUnder(root, relative));
        }

        [Fact]
        public void A_restored_file_gone_from_the_media_folder_refuses_before_writing()
        {
            BuildWorkspace();
            FirstRun();

            File.Delete(Path.Combine(_phoneRoot, "Media", "WhatsApp Images", "IMG-NEWER-0001.jpg"));

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(_fixture.Output()));
            Assert.Contains("1 restored newer-message file(s) are not in the registered media folder", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void A_restored_file_whose_bytes_changed_stops_the_export_and_leaves_no_archive()
        {
            BuildWorkspace();
            FirstRun();

            // Same length, other bytes: only the hash on copy can tell.
            var path = Path.Combine(_phoneRoot, "Media", "WhatsApp Images", "IMG-NEWER-0001.jpg");
            var altered = FullPhoto.ToArray();
            altered[0] ^= 0xFF;
            File.WriteAllBytes(path, altered);

            var stop = Assert.Throws<ArchiveExportStoppedException>(() => Export(_fixture.Output()));
            Assert.Contains("1 of 2 newer-message media files", stop.Message, StringComparison.Ordinal);
            Assert.Contains("1 with different bytes", stop.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void A_newer_file_of_other_participants_refuses_the_export()
        {
            BuildWorkspace();

            // The file says participant 2 is "me": the request's local participant is 1.
            NewerMessagesWriter.Append(NewerPath, Info(local: Other, other: Local), Run(NewerMessagesDatabase.HandOffWorkspace, "K0", false),
                [Row("K1", 1, true, text: "Invented", localID: Other, otherID: Local)]);
            SqliteConnection.ClearAllPools();

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(_fixture.Output()));
            Assert.Contains("participants", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void A_workspace_that_no_longer_ends_on_the_hand_off_day_refuses_the_export()
        {
            BuildWorkspace();
            FirstRun(Info(lastDay: Day.AddDays(-1)));

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(_fixture.Output()));
            Assert.Contains("no longer ends on the day", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void An_output_inside_the_newer_media_folder_is_refused()
        {
            BuildWorkspace();
            FirstRun();

            var refusal = Assert.Throws<ArchiveExportRefusedException>(
                () => new ArchiveExporter().Export(_fixture.Request(Path.Combine(_phoneRoot, "archive"), 1, 0)));

            Assert.Contains("inside a media source folder", refusal.Message, StringComparison.Ordinal);
        }
    }
}
