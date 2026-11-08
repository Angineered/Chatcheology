namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// Format version 4 (CH-71): <c>Attachment.Caption</c>, the required <c>ArchiveId</c> info row, and the message
    /// source <c>Msgstore</c>. Round trip, the writer's and reader's refusals, the manifest counts the validator
    /// checks, and that versions 1 to 3 still read with no captions and no id.
    /// </summary>
    /// <remarks>Invented names, text, ids and bytes only.</remarks>
    public sealed class ArchiveVersionFourTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        private string Database => Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName);

        private static ArchiveAttachment WithCaption(ArchiveAttachment attachment, string? caption) => new()
        {
            AttachmentID = attachment.AttachmentID,
            MessageID = attachment.MessageID,
            Ordinal = attachment.Ordinal,
            State = attachment.State,
            MediaSha256 = attachment.MediaSha256,
            OriginalFileName = attachment.OriginalFileName,
            IsForwarded = attachment.IsForwarded,
            ForwardScore = attachment.ForwardScore,
            LowQualityCopyOfAttachmentID = attachment.LowQualityCopyOfAttachmentID,
            Basis = attachment.Basis,
            Caption = caption,
        };

        private static ArchiveMessage WithSource(ArchiveMessage message, ArchiveMessageSource source, bool? forwarded = null, int? score = null) => new()
        {
            MessageID = message.MessageID,
            SequenceNumber = message.SequenceNumber,
            DateTimeLocal = message.DateTimeLocal,
            Kind = message.Kind,
            SenderParticipantID = message.SenderParticipantID,
            Text = message.Text,
            IsForwarded = forwarded ?? message.IsForwarded,
            ForwardScore = score ?? message.ForwardScore,
            Attachments = message.Attachments,
            Source = source,
        };

        /// <summary>
        /// The gapless sample with captions on the restored picture (10), the Missing slot (11) and the Unmatched slot
        /// (14), and one message from the message store, forwarded.
        /// </summary>
        private static IReadOnlyList<ArchiveMessage> CaptionedMessages() =>
            ArchiveSample.GaplessMessages.Select(message =>
            {
                var attachments = message.Attachments.Select(a => a.AttachmentID switch
                {
                    10 => WithCaption(a, "Invented caption with a link www.example.org/one"),
                    11 => WithCaption(a, "Invented caption\non two lines"),
                    14 => WithCaption(a, "  "),
                    _ => a,
                }).ToList();

                var copy = new ArchiveMessage
                {
                    MessageID = message.MessageID,
                    SequenceNumber = message.SequenceNumber,
                    DateTimeLocal = message.DateTimeLocal,
                    Kind = message.Kind,
                    SenderParticipantID = message.SenderParticipantID,
                    Text = message.Text,
                    IsForwarded = message.IsForwarded,
                    ForwardScore = message.ForwardScore,
                    Attachments = attachments,
                };

                return message.MessageID == 106 ? WithSource(copy, ArchiveMessageSource.Msgstore, true, 2) : copy;
            }).ToList();

        private ArchiveCounts WriteFolder(IReadOnlyList<ArchiveMessage>? messages = null)
        {
            ArchiveCounts counts;

            using (var writer = ArchiveWriter.Create(Database, ArchiveSample.Info()))
            {
                foreach (var participant in ArchiveSample.Participants)
                {
                    writer.WriteParticipant(participant);
                }

                foreach (var media in ArchiveSample.Media)
                {
                    writer.WriteMedia(media);
                }

                foreach (var message in messages ?? CaptionedMessages())
                {
                    writer.WriteMessage(message);
                }

                counts = writer.Complete();
            }

            var media2 = Path.Combine(_folder.Path, ArchiveFormat.MediaFolderName);
            Directory.CreateDirectory(media2);
            File.WriteAllBytes(Path.Combine(media2, ArchiveFormat.MediaFileName(ArchiveSample.AlphaSha, ArchiveSample.AlphaName)), ArchiveSample.AlphaBytes);
            File.WriteAllBytes(Path.Combine(media2, ArchiveFormat.MediaFileName(ArchiveSample.BetaSha, ArchiveSample.BetaName)), ArchiveSample.BetaBytes);

            File.WriteAllBytes(
                Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName),
                new ArchiveManifest(ArchiveManifest.FormatName, ArchiveFormat.FormatVersion, "Synthetic producer 1.0", ArchiveSample.Created, counts).ToBytes());
            File.WriteAllBytes(Path.Combine(_folder.Path, ArchiveContents.FileName), ArchiveContents.Build(_folder.Path));

            return counts;
        }

        private void RebuildContents() =>
            File.WriteAllBytes(Path.Combine(_folder.Path, ArchiveContents.FileName), ArchiveContents.Build(_folder.Path));

        [Fact]
        public void CaptionsArchiveIdAndMsgstoreSource_RoundTrip_AndValidate()
        {
            var counts = WriteFolder();

            Assert.Equal(3, counts.Captions);
            Assert.Equal(1, counts.MsgstoreMessages);
            Assert.Equal(5, counts.WorkspaceMessages);

            var manifest = File.ReadAllText(Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName));
            Assert.Contains("\"formatVersion\": 4", manifest, StringComparison.Ordinal);
            Assert.Contains("\"captions\": 3", manifest, StringComparison.Ordinal);
            Assert.Contains("\"msgstoreMessages\": 1", manifest, StringComparison.Ordinal);
            Assert.DoesNotContain(ArchiveSample.ArchiveId, manifest, StringComparison.Ordinal);

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Equal(4, reader.Info.FormatVersion);
            Assert.True(reader.HasCaptions);
            Assert.Equal(ArchiveSample.ArchiveId, reader.Info.ArchiveId);

            var messages = reader.ReadAllPages().SelectMany(p => p.Messages).ToList();
            var attachments = messages.SelectMany(m => m.Attachments).ToDictionary(a => a.AttachmentID);

            // Stored exactly as given: no trimming, line breaks kept, any state.
            Assert.Equal("Invented caption with a link www.example.org/one", attachments[10].Caption);
            Assert.Equal("Invented caption\non two lines", attachments[11].Caption);
            Assert.Equal("  ", attachments[14].Caption);
            Assert.Null(attachments[12].Caption);
            Assert.Null(attachments[13].Caption);

            var fromStore = messages.Single(m => m.MessageID == 106);
            Assert.Equal(ArchiveMessageSource.Msgstore, fromStore.Source);
            Assert.True(fromStore.IsForwarded);
            Assert.Equal(2, fromStore.ForwardScore);
        }

        [Fact]
        public void TheWriter_RefusesAVersionFourArchiveWithoutAValidId()
        {
            foreach (var id in new string?[]
                     {
                         null, "", "not a guid", "5A1D0C3E-7B2F-4E6A-9C81-0D2F4B6A8E10", "5a1d0c3e7b2f4e6a9c810d2f4b6a8e10",
                         "00000000-0000-0000-0000-000000000000", "{5a1d0c3e-7b2f-4e6a-9c81-0d2f4b6a8e10}", "5a1d0c3e-7b2f-4e6a-9c81-0d2f4b6a8e10\n",
                     })
            {
                var info = ArchiveSample.Info();
                var refusal = Assert.Throws<InvalidOperationException>(() => ArchiveWriter.Create(Database, new ArchiveInfo
                {
                    FormatVersion = info.FormatVersion,
                    CreatedUtc = info.CreatedUtc,
                    ProducerVersion = info.ProducerVersion,
                    ConversationTitle = info.ConversationTitle,
                    SourceExportSha256 = info.SourceExportSha256,
                    TimeZoneID = info.TimeZoneID,
                    LocalParticipantID = info.LocalParticipantID,
                    ArchiveId = id,
                }));

                Assert.Contains("archive id", refusal.Message, StringComparison.Ordinal);
                Assert.False(File.Exists(Database));
            }
        }

        [Fact]
        public void TheWriter_RefusesAnIdACaptionOrAMsgstoreMessageBelowVersionFour()
        {
            var v3 = ArchiveSample.Info(3);

            Assert.Throws<InvalidOperationException>(() => ArchiveWriter.Create(Database, new ArchiveInfo
            {
                FormatVersion = 3,
                CreatedUtc = v3.CreatedUtc,
                ProducerVersion = v3.ProducerVersion,
                ConversationTitle = v3.ConversationTitle,
                SourceExportSha256 = v3.SourceExportSha256,
                TimeZoneID = v3.TimeZoneID,
                LocalParticipantID = v3.LocalParticipantID,
                ArchiveId = ArchiveSample.ArchiveId,
            }));

            using var writer = ArchiveWriter.Create(Database, v3);

            foreach (var participant in ArchiveSample.Participants)
            {
                writer.WriteParticipant(participant);
            }

            var captioned = CaptionedMessages().Single(m => m.MessageID == 102);
            var caption = Assert.Throws<InvalidOperationException>(() => writer.WriteMessage(captioned));
            Assert.Contains("version 4", caption.Message, StringComparison.Ordinal);

            var store = WithSource(ArchiveSample.GaplessMessages[0], ArchiveMessageSource.Msgstore);
            var source = Assert.Throws<InvalidOperationException>(() => writer.WriteMessage(store));
            Assert.Contains("version 4", source.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWriter_RefusesAnEmptyCaption_AndAForwardedFactOnAnOlderExportMessage()
        {
            using var writer = ArchiveWriter.Create(Database, ArchiveSample.Info());

            foreach (var participant in ArchiveSample.Participants)
            {
                writer.WriteParticipant(participant);
            }

            var message = ArchiveSample.GaplessMessages.Single(m => m.MessageID == 106);
            var empty = new ArchiveMessage
            {
                MessageID = message.MessageID,
                SequenceNumber = message.SequenceNumber,
                DateTimeLocal = message.DateTimeLocal,
                Kind = message.Kind,
                SenderParticipantID = message.SenderParticipantID,
                Text = message.Text,
                IsForwarded = null,
                ForwardScore = null,
                Attachments = [WithCaption(message.Attachments[0], string.Empty)],
            };

            var refusal = Assert.Throws<InvalidOperationException>(() => writer.WriteMessage(empty));
            Assert.Contains("must not be empty", refusal.Message, StringComparison.Ordinal);

            // An older export's message still has no forwarded fact; a message-store one may.
            var export = WithSource(ArchiveSample.GaplessMessages[0], ArchiveMessageSource.Export, true, 1);
            Assert.Throws<InvalidOperationException>(() => writer.WriteMessage(export));
        }

        [Theory]
        [InlineData("DELETE FROM ArchiveInfo WHERE Key = 'ArchiveId';")]
        [InlineData("UPDATE ArchiveInfo SET Value = 'not-an-id' WHERE Key = 'ArchiveId';")]
        [InlineData("UPDATE ArchiveInfo SET Value = upper(Value) WHERE Key = 'ArchiveId';")]
        public void TheReader_RefusesAVersionFourArchiveWithoutAValidId_AndTheValidatorFails(string damage)
        {
            WriteFolder();
            ArchiveSample.Tamper(_folder.Path, damage);
            RebuildContents();

            var refusal = Assert.Throws<ArchiveFormatException>(() => ArchiveReader.Open(_folder.Path));
            Assert.Contains("archive id", refusal.Message, StringComparison.Ordinal);

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.False(result.Passed);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.DatabaseUnreadable]);
        }

        [Fact]
        public void TheReader_RefusesAnEmptyCaption()
        {
            WriteFolder();
            ArchiveSample.Tamper(_folder.Path, "PRAGMA ignore_check_constraints = 1; UPDATE Attachment SET Caption = '' WHERE AttachmentID = 10;");
            RebuildContents();

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Throws<ArchiveFormatException>(() => reader.ReadAllPages().ToList());
            Assert.Equal(1, ArchiveValidator.Validate(_folder.Path).ProblemCounts[ArchiveProblemKind.DatabaseUnreadable]);
        }

        [Fact]
        public void TheReader_RefusesAVersionFourArchiveWithoutTheCaptionColumn()
        {
            // A version 3 database stamped as version 4, with a valid id: only the column is missing.
            ArchiveSample.WriteFolder(_folder.Path, gapless: true, formatVersion: 3);
            ArchiveSample.Tamper(
                _folder.Path,
                "PRAGMA user_version = 4; UPDATE ArchiveInfo SET Value = '4' WHERE Key = 'FormatVersion'; " +
                $"INSERT INTO ArchiveInfo (Key, Value) VALUES ('ArchiveId', '{ArchiveSample.ArchiveId}');");

            var refusal = Assert.Throws<ArchiveFormatException>(() => ArchiveReader.Open(_folder.Path));
            Assert.Contains("columns", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ACaptionCountThatDisagrees_FailsTheManifestCheck()
        {
            WriteFolder();
            ArchiveSample.Tamper(_folder.Path, "UPDATE Attachment SET Caption = NULL WHERE AttachmentID = 11;");
            RebuildContents();

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ManifestCountDiffers]);
        }

        [Fact]
        public void TheSchema_RefusesAMsgstoreSourceBelowVersionFour_AndAForwardedOlderExportMessage()
        {
            ArchiveSample.WriteFolder(_folder.Path, gapless: true, formatVersion: 3);

            Assert.ThrowsAny<Exception>(() =>
                ArchiveSample.Tamper(_folder.Path, "UPDATE Message SET Source = 'Msgstore' WHERE MessageID = 103;"));

            using var other = new TemporaryFolder();
            ArchiveSample.WriteFolder(other.Path, gapless: true);

            Assert.ThrowsAny<Exception>(() =>
                ArchiveSample.Tamper(other.Path, "UPDATE Message SET Source = 'Export' WHERE MessageID = 101;"));

            ArchiveSample.Tamper(other.Path, "UPDATE Message SET Source = 'Msgstore' WHERE MessageID = 101;");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void VersionsOneToThree_StillRead_WithNoCaptionsAndNoId(int version)
        {
            ArchiveSample.WriteFolder(_folder.Path, gapless: true, formatVersion: version);

            Assert.True(ArchiveValidator.Validate(_folder.Path).Passed);

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Equal(version, reader.Info.FormatVersion);
            Assert.False(reader.HasCaptions);
            Assert.Null(reader.Info.ArchiveId);
            Assert.All(reader.ReadAllPages().SelectMany(p => p.Messages).SelectMany(m => m.Attachments), a => Assert.Null(a.Caption));
        }

        [Fact]
        public void AVersionThreeArchive_DoesNotReadAStrayIdRow_OrAMsgstoreSource()
        {
            ArchiveSample.WriteFolder(_folder.Path, gapless: true, formatVersion: 3);
            ArchiveSample.Tamper(_folder.Path, $"INSERT INTO ArchiveInfo (Key, Value) VALUES ('ArchiveId', '{ArchiveSample.ArchiveId}');");

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Null(reader.Info.ArchiveId);
        }

        [Fact]
        public void ArchiveIds_AreLowerCaseGuids()
        {
            for (var i = 0; i < 20; i++)
            {
                Assert.True(ArchiveFormat.IsArchiveId(ArchiveFormat.NewArchiveId()));
            }

            Assert.NotEqual(ArchiveFormat.NewArchiveId(), ArchiveFormat.NewArchiveId());
            Assert.True(ArchiveFormat.IsArchiveId(ArchiveSample.ArchiveId));
            Assert.False(ArchiveFormat.IsArchiveId(null));
            Assert.False(ArchiveFormat.IsArchiveId(Guid.Empty.ToString("D")));
        }
    }
}
