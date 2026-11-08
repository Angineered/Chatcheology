using System.Security.Cryptography;
using System.Text;

namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// Format version 2: a version 1 archive still reads and validates; message sources, export
    /// periods and the memory tables round trip through the writer, reader and validator.
    /// </summary>
    /// <remarks>Invented names, text and bytes only.</remarks>
    public sealed class ArchiveVersionTwoTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        private static readonly byte[] ImageBytes = Encoding.ASCII.GetBytes("synthetic entry artwork");

        private static string ImageSha => ArchiveHashing.Hex(SHA256.HashData(ImageBytes));

        private static readonly byte[] IconBytes = Encoding.ASCII.GetBytes("synthetic trophy icon");

        private static string IconSha => ArchiveHashing.Hex(SHA256.HashData(IconBytes));

        private static readonly byte[] CoverBytes = Encoding.ASCII.GetBytes("synthetic cover photo");

        private static string CoverSha => ArchiveHashing.Hex(SHA256.HashData(CoverBytes));

        private static readonly byte[] PhotoBytes = Encoding.ASCII.GetBytes("synthetic extra photo");

        private static string PhotoSha => ArchiveHashing.Hex(SHA256.HashData(PhotoBytes));

        // ---------------------------------------------------------------------------------------
        // Extra photos (CH-46).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ExtraPhotos_RoundTrip_WithCaptions_AndValidate()
        {
            var counts = WriteMemoryArchive(_folder.Path, photos: true);

            Assert.Equal(2, counts.EntryImages);

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.True(reader.HasEntryImages);
            var photos = reader.ReadEntryImages(20);
            Assert.Equal([1, 2], photos.Select(p => p.Ordinal));
            Assert.Equal([PhotoSha, ImageSha], photos.Select(p => p.Sha256));
            Assert.Equal(["By the sea", null], photos.Select(p => p.Caption));
            Assert.All(photos, p => Assert.True(File.Exists(reader.EntryImageFullPath(p))));
            Assert.Empty(reader.ReadEntryImages(10));
            Assert.Equal(2, reader.ReadAllEntryImages().Count);

            // The game image is shared with the second photo: one file, one Media row.
            Assert.Equal(4, Directory.GetFiles(Path.Combine(_folder.Path, "media")).Length);
        }

        [Fact]
        public void AnArchiveWithoutTheEntryImageTable_ReadsAsNoExtraPhotos_AndValidates()
        {
            WriteMemoryArchive(_folder.Path);
            ArchiveSample.Tamper(_folder.Path, "DROP TABLE EntryImage; VACUUM;");

            // The manifest of a build before CH-46 had no entryImages count; it reads as 0.
            var manifestPath = Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName);
            File.WriteAllText(
                manifestPath,
                File.ReadAllText(manifestPath).Replace("  \"entryImages\": 0,\n", string.Empty, StringComparison.Ordinal)
                    .Replace("    \"entryImages\": 0,\n", string.Empty, StringComparison.Ordinal));
            Assert.DoesNotContain("entryImages", File.ReadAllText(manifestPath), StringComparison.Ordinal);
            File.WriteAllBytes(Path.Combine(_folder.Path, ArchiveContents.FileName), ArchiveContents.Build(_folder.Path));

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.True(reader.HasMemories);
            Assert.False(reader.HasEntryImages);
            Assert.Empty(reader.ReadEntryImages(20));
            Assert.Empty(reader.ReadAllEntryImages());
            Assert.Equal(2, reader.ReadCollections().Count);
        }

        [Fact]
        public void AnExtraPhotoWhoseFileIsGoneOrDifferent_FailsMedia()
        {
            WriteMemoryArchive(_folder.Path, photos: true);
            var photo = Path.Combine(_folder.Path, "media", PhotoSha + ".jpg");
            File.WriteAllBytes(photo, Encoding.ASCII.GetBytes("altered"));

            Assert.Equal(1, ArchiveValidator.Validate(_folder.Path).ProblemCounts[ArchiveProblemKind.EntryImageFileDiffers]);

            File.Delete(photo);
            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.EntryImageFileMissing]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.Media).Passed);
        }

        [Fact]
        public void AnExtraPhotoOnAnImportedEntry_OrRepeatingTheCover_FailsLinks_AndTheWriterRefusesIt()
        {
            WriteMemoryArchive(_folder.Path, photos: true);
            ArchiveSample.Tamper(_folder.Path, $"INSERT INTO EntryImage VALUES (10, 1, '{PhotoSha}', '.jpg', NULL);");
            ArchiveSample.Tamper(_folder.Path, $"INSERT INTO EntryImage VALUES (20, 3, '{CoverSha}', '.png', NULL);");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(2, result.ProblemCounts[ArchiveProblemKind.ExtraPhotoMisplaced]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.LinksAndBasis).Passed);

            var database = Path.Combine(_folder.Path, "second.db");

            using var writer = Begin(database, photos: true);
            writer.WriteEntry(new ArchiveEntry
            {
                EntryID = 30,
                CollectionID = 1,
                SourceKey = "setC|setD|2016-06-01",
                Title = "Another Game",
                StartLocal = new DateTime(2016, 6, 1, 20, 0, 0),
                EndLocal = null,
                StartUtc = null,
                EndUtc = null,
                DatePrecision = "Day",
                Summary = null,
                ImageSha256 = null,
                ImageExtension = null,
                Source = null,
                DayLevel = null,
                Images = [new ArchiveEntryImage { EntryID = 30, Ordinal = 1, Sha256 = PhotoSha, Extension = ".jpg", Caption = null }],
            });

            var refusal = Assert.Throws<InvalidOperationException>(() => writer.Complete());
            Assert.Contains("personal collection", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnExtraPhotoWithoutAMediaRow_IsRefusedByTheWriter()
        {
            using var writer = Begin(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName), photos: true, photoMedia: false);

            Assert.Throws<InvalidOperationException>(() => writer.Complete());
        }

        // ---------------------------------------------------------------------------------------
        // Noted dates (CH-47).
        // ---------------------------------------------------------------------------------------

        /// <summary>Three invented noted dates; the archive is created in 2026 (<see cref="ArchiveSample.Created"/>).</summary>
        private static readonly ArchiveNotedDate[] NotedDates =
        [
            new() { NotedDateID = 1, Title = "Invented anniversary", Month = 2, Day = 14, Year = 2019, Note = "An invented note" },
            new() { NotedDateID = 2, Title = "Leap day", Month = 2, Day = 29, Year = null, Note = null },
            new() { NotedDateID = 4, Title = "This year", Month = 12, Day = 31, Year = 2026, Note = null },
        ];

        [Fact]
        public void NotedDates_RoundTrip_AndValidate()
        {
            var counts = WriteMemoryArchive(_folder.Path, notedDates: true);

            Assert.Equal(3, counts.NotedDates);
            Assert.Contains("\"notedDates\": 3", File.ReadAllText(Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName)), StringComparison.Ordinal);

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.True(reader.HasNotedDates);
            Assert.Equal(
                NotedDates.Select(d => (d.NotedDateID, d.Title, d.Month, d.Day, d.Year, d.Note)),
                reader.ReadNotedDates().Select(d => (d.NotedDateID, d.Title, d.Month, d.Day, d.Year, d.Note)));
        }

        [Fact]
        public void AnArchiveWithoutTheNotedDateTable_ReadsAsNoNotedDates_AndValidates()
        {
            WriteMemoryArchive(_folder.Path);
            ArchiveSample.Tamper(_folder.Path, "DROP TABLE NotedDate; VACUUM;");

            // The manifest of a build before CH-47 had no notedDates count; it reads as 0.
            var manifestPath = Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName);
            File.WriteAllText(
                manifestPath,
                File.ReadAllText(manifestPath).Replace("    \"notedDates\": 0,\n", string.Empty, StringComparison.Ordinal));
            Assert.DoesNotContain("notedDates", File.ReadAllText(manifestPath), StringComparison.Ordinal);
            Assert.Equal(0, ArchiveManifest.Parse(File.ReadAllBytes(manifestPath)).Counts.NotedDates);
            File.WriteAllBytes(Path.Combine(_folder.Path, ArchiveContents.FileName), ArchiveContents.Build(_folder.Path));

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.True(reader.HasMemories);
            Assert.False(reader.HasNotedDates);
            Assert.Empty(reader.ReadNotedDates());
            Assert.Equal(2, reader.ReadCollections().Count);
        }

        [Fact]
        public void ANotedDateBreakingEachRule_IsFoundByTheValidator()
        {
            WriteMemoryArchive(_folder.Path, notedDates: true);

            // The schema states the same rules; a damaged or hand-edited file can still break them.
            ArchiveSample.Tamper(
                _folder.Path,
                $"""
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO NotedDate VALUES (10, '  ', 1, 1, NULL, NULL);
                INSERT INTO NotedDate VALUES (11, '{new string('t', 81)}', 1, 1, NULL, NULL);
                INSERT INTO NotedDate VALUES (12, 'Day zero', 2, 0, NULL, NULL);
                INSERT INTO NotedDate VALUES (13, 'Thirty-first of April', 4, 31, NULL, NULL);
                INSERT INTO NotedDate VALUES (14, 'Leap day in a common year', 2, 29, 2019, NULL);
                INSERT INTO NotedDate VALUES (15, 'Month thirteen', 13, 1, NULL, NULL);
                INSERT INTO NotedDate VALUES (16, 'Future', 5, 1, 2027, NULL);
                INSERT INTO NotedDate VALUES (17, 'Too early', 5, 1, 1899, NULL);
                INSERT INTO NotedDate VALUES (18, 'Blank note', 5, 1, NULL, '   ');
                INSERT INTO NotedDate VALUES (19, 'Long note', 5, 1, NULL, '{new string('n', 501)}');
                """);

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(2, result.ProblemCounts[ArchiveProblemKind.NotedDateTitleInvalid]);
            Assert.Equal(4, result.ProblemCounts[ArchiveProblemKind.NotedDateDateInvalid]);
            Assert.Equal(2, result.ProblemCounts[ArchiveProblemKind.NotedDateYearInvalid]);
            Assert.Equal(2, result.ProblemCounts[ArchiveProblemKind.NotedDateNoteInvalid]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.LinksAndBasis).Passed);

            // The manifest's count is now behind too.
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ManifestCountDiffers]);
            Assert.All(result.FirstProblems, p => Assert.Null(p.SequenceNumber));
        }

        [Fact]
        public void ANotedDateOfTheWrongKind_StopsTheRowChecks()
        {
            WriteMemoryArchive(_folder.Path, notedDates: true);
            ArchiveSample.Tamper(_folder.Path, "PRAGMA ignore_check_constraints = ON; UPDATE NotedDate SET Month = 'February' WHERE NotedDateID = 1;");

            using (var reader = ArchiveReader.Open(_folder.Path))
            {
                Assert.Throws<ArchiveFormatException>(() => reader.ReadNotedDates());
            }

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.False(result.Passed);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.DatabaseUnreadable]);
        }

        [Fact]
        public void AManifestNotedDateCount_ThatDiffers_IsFound()
        {
            WriteMemoryArchive(_folder.Path, notedDates: true);
            ArchiveSample.Tamper(_folder.Path, "DELETE FROM NotedDate WHERE NotedDateID = 4;");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ManifestCountDiffers]);
            Assert.Equal(0, result.ProblemCounts[ArchiveProblemKind.NotedDateTitleInvalid]);
        }

        [Theory]
        [InlineData("", 1, 1, null, null)]
        [InlineData("x", 2, 30, null, null)]
        [InlineData("x", 2, 29, 2019, null)]
        [InlineData("x", 1, 1, 2027, null)]
        [InlineData("x", 1, 1, null, " ")]
        public void TheWriter_RefusesANotedDateBreakingARule(string title, int month, int day, int? year, string? note)
        {
            using var writer = Begin(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName));

            var refusal = Assert.Throws<InvalidOperationException>(() => writer.WriteNotedDate(new ArchiveNotedDate
            {
                NotedDateID = 1,
                Title = title,
                Month = month,
                Day = day,
                Year = year,
                Note = note,
            }));

            Assert.Contains("noted date", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheLatestYear_IsTheArchivesCreationYear_AnywhereOnEarth()
        {
            Assert.Equal(2026, NotedDateRules.LatestYearAt(ArchiveSample.Created));
            Assert.Equal(2027, NotedDateRules.LatestYearAt(new DateTime(2026, 12, 31, 11, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(2026, NotedDateRules.LatestYearAt(new DateTime(2026, 12, 31, 9, 59, 59, DateTimeKind.Utc)));
        }

        [Fact]
        public void AVersionOneArchive_HasNoNotedDates()
        {
            ArchiveSample.WriteFolder(_folder.Path, gapless: true, formatVersion: 1);

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.False(reader.HasNotedDates);
            Assert.Empty(reader.ReadNotedDates());
        }

        [Fact]
        public void AVersionOneArchive_RefusesANotedDate()
        {
            var info = ArchiveSample.Info();

            using var writer = ArchiveWriter.Create(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName), new ArchiveInfo
            {
                FormatVersion = 1,
                CreatedUtc = info.CreatedUtc,
                ProducerVersion = info.ProducerVersion,
                ConversationTitle = info.ConversationTitle,
                SourceExportSha256 = info.SourceExportSha256,
                TimeZoneID = info.TimeZoneID,
                LocalParticipantID = info.LocalParticipantID,
            });

            Assert.Throws<InvalidOperationException>(() => writer.WriteNotedDate(NotedDates[0]));
        }

        // ---------------------------------------------------------------------------------------
        // External links (CH-48).
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Two links on the personal entry 20 (written out of order) and one on noted date 1; invented addresses.
        /// The identifiers are not 1..n, so keeping them is visible.
        /// </summary>
        private static readonly ArchiveExternalLink[] ExternalLinks =
        [
            new() { ExternalLinkID = 7, OwnerKind = ExternalLinkOwnerKind.Entry, OwnerID = 20, Ordinal = 2, Url = "https://photos.example.org/album/b", Label = null },
            new() { ExternalLinkID = 3, OwnerKind = ExternalLinkOwnerKind.Entry, OwnerID = 20, Ordinal = 1, Url = "https://photos.example.org/album/a", Label = "Day 1" },
            new() { ExternalLinkID = 9, OwnerKind = ExternalLinkOwnerKind.NotedDate, OwnerID = 1, Ordinal = 1, Url = "https://photos.example.org/album/c?key=x#top", Label = "Invented album" },
        ];

        [Fact]
        public void ExternalLinks_RoundTrip_AsStored_AndValidate()
        {
            var counts = WriteMemoryArchive(_folder.Path, externalLinks: true);

            Assert.Equal(3, counts.ExternalLinks);
            Assert.Contains("\"externalLinks\": 3", File.ReadAllText(Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName)), StringComparison.Ordinal);

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.True(reader.HasExternalLinks);
            Assert.Equal(
                [(3L, 1, "https://photos.example.org/album/a", (string?)"Day 1"), (7L, 2, "https://photos.example.org/album/b", null)],
                reader.ReadExternalLinks(ExternalLinkOwnerKind.Entry, 20).Select(l => (l.ExternalLinkID, l.Ordinal, l.Url, l.Label)));
            Assert.Equal([9L], reader.ReadExternalLinks(ExternalLinkOwnerKind.NotedDate, 1).Select(l => l.ExternalLinkID));
            Assert.Empty(reader.ReadExternalLinks(ExternalLinkOwnerKind.Entry, 1));
            Assert.Empty(reader.ReadExternalLinks(ExternalLinkOwnerKind.NotedDate, 20));
            Assert.Equal(
                ExternalLinks.OrderBy(l => l.ExternalLinkID).Select(l => (l.ExternalLinkID, l.OwnerKind, l.OwnerID, l.Ordinal, l.Url, l.Label)),
                reader.ReadAllExternalLinks().Select(l => (l.ExternalLinkID, l.OwnerKind, l.OwnerID, l.Ordinal, l.Url, l.Label)));
        }

        [Fact]
        public void AnArchiveWithoutTheExternalLinkTable_ReadsAsNoLinks_AndValidates()
        {
            WriteMemoryArchive(_folder.Path, notedDates: true);
            ArchiveSample.Tamper(_folder.Path, "DROP TABLE ExternalLink; VACUUM;");

            // The manifest of a build before CH-48 had no externalLinks count; it reads as 0.
            var manifestPath = Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName);
            File.WriteAllText(
                manifestPath,
                File.ReadAllText(manifestPath).Replace("    \"externalLinks\": 0,\n", string.Empty, StringComparison.Ordinal));
            Assert.DoesNotContain("externalLinks", File.ReadAllText(manifestPath), StringComparison.Ordinal);
            Assert.Equal(0, ArchiveManifest.Parse(File.ReadAllBytes(manifestPath)).Counts.ExternalLinks);
            File.WriteAllBytes(Path.Combine(_folder.Path, ArchiveContents.FileName), ArchiveContents.Build(_folder.Path));

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.True(reader.HasMemories);
            Assert.True(reader.HasNotedDates);
            Assert.False(reader.HasExternalLinks);
            Assert.Empty(reader.ReadAllExternalLinks());
            Assert.Empty(reader.ReadExternalLinks(ExternalLinkOwnerKind.Entry, 20));
        }

        [Fact]
        public void AnExternalLinkBreakingEachRule_IsFoundByTheValidator()
        {
            WriteMemoryArchive(_folder.Path, externalLinks: true);

            var tooLong = "https://photos.example.org/" + new string('x', 2001 - 27);
            Assert.Equal(2001, tooLong.Length);

            // The schema states the address and label rules; a damaged or hand-edited file can still break them.
            ArchiveSample.Tamper(
                _folder.Path,
                $"""
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO ExternalLink VALUES (20, 'NotedDate', 2, 1, 'http://photos.example.org/x', NULL);
                INSERT INTO ExternalLink VALUES (21, 'NotedDate', 2, 2, 'javascript:alert(1)', NULL);
                INSERT INTO ExternalLink VALUES (22, 'NotedDate', 2, 3, 'https://photos.example.org@evil.example/x', NULL);
                INSERT INTO ExternalLink VALUES (23, 'NotedDate', 2, 4, '{tooLong}', NULL);
                INSERT INTO ExternalLink VALUES (24, 'NotedDate', 2, 5, '/relative/path', NULL);
                INSERT INTO ExternalLink VALUES (25, 'NotedDate', 2, 6, 'https://photos.example.org/y', '   ');
                INSERT INTO ExternalLink VALUES (26, 'NotedDate', 2, 7, 'https://photos.example.org/z', '{new string('l', 61)}');
                INSERT INTO ExternalLink VALUES (30, 'Entry', 10, 1, 'https://photos.example.org/imported', NULL);
                INSERT INTO ExternalLink VALUES (31, 'NotedDate', 99, 1, 'https://photos.example.org/gone', NULL);
                INSERT INTO ExternalLink VALUES (32, 'Entry', 999, 1, 'https://photos.example.org/gone', NULL);
                INSERT INTO ExternalLink VALUES (33, 'NotedDate', 4, 2, 'https://photos.example.org/gap', NULL);
                INSERT INTO ExternalLink (ExternalLinkID, OwnerKind, OwnerID, Ordinal, Url, Label)
                WITH RECURSIVE n(value) AS (SELECT 3 UNION ALL SELECT value + 1 FROM n WHERE value < 11)
                SELECT 40 + value, 'Entry', 20, value, 'https://photos.example.org/many', NULL FROM n;
                """);

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(5, result.ProblemCounts[ArchiveProblemKind.ExternalLinkUrlInvalid]);
            Assert.Equal(2, result.ProblemCounts[ArchiveProblemKind.ExternalLinkLabelInvalid]);
            Assert.Equal(3, result.ProblemCounts[ArchiveProblemKind.ExternalLinkOwnerInvalid]);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ExternalLinkOrdinalsInvalid]);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ExternalLinkTooMany]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.LinksAndBasis).Passed);
            Assert.Equal(ArchiveCheck.LinksAndBasis, ArchiveValidator.CheckOf(ArchiveProblemKind.ExternalLinkTooMany));

            // The manifest's count is now behind too; no problem names a message.
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ManifestCountDiffers]);
            Assert.All(result.FirstProblems, p => Assert.Null(p.SequenceNumber));
        }

        [Fact]
        public void AnExternalLinkOnANotedDate_InAnArchiveWithoutTheNotedDateTable_HasNoOwner()
        {
            WriteMemoryArchive(_folder.Path, externalLinks: true);
            ArchiveSample.Tamper(_folder.Path, "DROP TABLE NotedDate;");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ExternalLinkOwnerInvalid]);
        }

        [Fact]
        public void AnExternalLinkOfTheWrongKind_StopsTheRowChecks()
        {
            WriteMemoryArchive(_folder.Path, externalLinks: true);
            ArchiveSample.Tamper(_folder.Path, "PRAGMA ignore_check_constraints = ON; UPDATE ExternalLink SET OwnerKind = 'Album' WHERE ExternalLinkID = 3;");

            using (var reader = ArchiveReader.Open(_folder.Path))
            {
                Assert.Throws<ArchiveFormatException>(() => reader.ReadAllExternalLinks());
            }

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.False(result.Passed);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.DatabaseUnreadable]);
        }

        [Fact]
        public void AManifestExternalLinkCount_ThatDiffers_IsFound()
        {
            WriteMemoryArchive(_folder.Path, externalLinks: true);
            ArchiveSample.Tamper(_folder.Path, "DELETE FROM ExternalLink WHERE ExternalLinkID = 9;");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ManifestCountDiffers]);
            Assert.Equal(0, result.ProblemCounts[ArchiveProblemKind.ExternalLinkOrdinalsInvalid]);
        }

        [Theory]
        [InlineData("http://photos.example.org/a", null, 1)]
        [InlineData("javascript:alert(1)", null, 1)]
        [InlineData("file:///C:/synthetic.jpg", null, 1)]
        [InlineData("intent://photos.example.org#Intent;end", null, 1)]
        [InlineData("/relative/path", null, 1)]
        [InlineData("#fragment", null, 1)]
        [InlineData("https://someone@photos.example.org/a", null, 1)]
        [InlineData(" https://photos.example.org/a", null, 1)]
        [InlineData("https://photos.example.org/a", " ", 1)]
        [InlineData("https://photos.example.org/a", "Label sixty-one characters long, which is one more than ok!!!", 1)]
        [InlineData("https://photos.example.org/a", null, 0)]
        public void TheWriter_RefusesAnExternalLinkBreakingARule(string url, string? label, int ordinal)
        {
            using var writer = Begin(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName));

            var refusal = Assert.Throws<InvalidOperationException>(() => writer.WriteExternalLink(new ArchiveExternalLink
            {
                ExternalLinkID = 1,
                OwnerKind = ExternalLinkOwnerKind.Entry,
                OwnerID = 20,
                Ordinal = ordinal,
                Url = url,
                Label = label,
            }));

            // Names the rule, never the value.
            Assert.Contains("external link", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("example", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWriter_RefusesALinkTooLong_ButKeepsOneOfTheLongestLength()
        {
            var longest = "https://photos.example.org/" + new string('x', 2000 - 27);

            using (var writer = Begin(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName)))
            {
                Assert.Throws<InvalidOperationException>(() => writer.WriteExternalLink(Link(1, 20, 1, longest + "x")));
                writer.WriteExternalLink(Link(1, 20, 1, longest));
                writer.WriteExternalLink(Link(2, 20, 2, "https://photos.example.org/a", new string('l', 60)));
                Assert.Equal(2, writer.Complete().ExternalLinks);
            }
        }

        [Theory]
        [InlineData("Entry", 10, 1)]
        [InlineData("Entry", 999, 1)]
        [InlineData("NotedDate", 1, 1)]
        [InlineData("Entry", 20, 2)]
        public void TheWriter_RefusesALinkWithoutAPersonalOwner_OrOutOfOrder(string kind, long owner, int ordinal)
        {
            using var writer = Begin(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName));

            // No noted date is written here, so noted date 1 is not there either.
            writer.WriteExternalLink(new ArchiveExternalLink
            {
                ExternalLinkID = 1,
                OwnerKind = Enum.Parse<ExternalLinkOwnerKind>(kind),
                OwnerID = owner,
                Ordinal = ordinal,
                Url = "https://photos.example.org/a",
                Label = null,
            });

            Assert.Throws<InvalidOperationException>(() => writer.Complete());
        }

        [Fact]
        public void TheWriter_RefusesMoreThanTenLinksOnOneOwner()
        {
            using (var writer = Begin(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName)))
            {
                for (var ordinal = 1; ordinal <= 11; ordinal++)
                {
                    writer.WriteExternalLink(Link(ordinal, 20, ordinal, "https://photos.example.org/a"));
                }

                Assert.Throws<InvalidOperationException>(() => writer.Complete());
            }

            File.Delete(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName));

            using (var writer = Begin(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName)))
            {
                for (var ordinal = 1; ordinal <= 10; ordinal++)
                {
                    writer.WriteExternalLink(Link(ordinal, 20, ordinal, "https://photos.example.org/a"));
                }

                Assert.Equal(10, writer.Complete().ExternalLinks);
            }
        }

        [Fact]
        public void AVersionOneArchive_HasNoExternalLinks_AndRefusesOne()
        {
            ArchiveSample.WriteFolder(_folder.Path, gapless: true, formatVersion: 1);

            using (var reader = ArchiveReader.Open(_folder.Path))
            {
                Assert.False(reader.HasExternalLinks);
                Assert.Empty(reader.ReadAllExternalLinks());
            }

            var info = ArchiveSample.Info();
            using var writer = ArchiveWriter.Create(Path.Combine(_folder.Path, "other.db"), new ArchiveInfo
            {
                FormatVersion = 1,
                CreatedUtc = info.CreatedUtc,
                ProducerVersion = info.ProducerVersion,
                ConversationTitle = info.ConversationTitle,
                SourceExportSha256 = info.SourceExportSha256,
                TimeZoneID = info.TimeZoneID,
                LocalParticipantID = info.LocalParticipantID,
            });

            Assert.Throws<InvalidOperationException>(() => writer.WriteExternalLink(ExternalLinks[0]));
        }

        private static ArchiveExternalLink Link(long id, long entry, int ordinal, string url, string? label = null) => new()
        {
            ExternalLinkID = id,
            OwnerKind = ExternalLinkOwnerKind.Entry,
            OwnerID = entry,
            Ordinal = ordinal,
            Url = url,
            Label = label,
        };

        // ---------------------------------------------------------------------------------------
        // Version 1.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AVersionOneArchive_StillReadsAndValidates_WithNoMemories()
        {
            ArchiveSample.WriteFolder(_folder.Path, gapless: true, formatVersion: 1);

            // The manifest exactly as a version 1 build wrote it: no version 2 counts at all.
            var manifestPath = Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName);
            File.WriteAllText(
                manifestPath,
                """
                {
                  "format": "chatcheology-portable-archive",
                  "formatVersion": 1,
                  "producerVersion": "Synthetic producer 1.0",
                  "createdUtc": "2026-10-19T08:30:15Z",
                  "counts": {
                    "participants": 2,
                    "messages": 6,
                    "attachments": 5,
                    "restored": 3,
                    "missing": 1,
                    "unmatched": 1,
                    "mediaFiles": 2
                  }
                }

                """.Replace("\r", "", StringComparison.Ordinal));
            File.WriteAllBytes(Path.Combine(_folder.Path, ArchiveContents.FileName), ArchiveContents.Build(_folder.Path));

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Equal(1, reader.Info.FormatVersion);
            Assert.False(reader.HasMemories);
            Assert.Empty(reader.Info.ExportPeriods);
            Assert.All(reader.ReadAllPages().SelectMany(p => p.Messages), m => Assert.Equal(ArchiveMessageSource.Workspace, m.Source));
            Assert.Empty(reader.ReadCollections());
            Assert.Empty(reader.ReadEntries(DateTime.MinValue, DateTime.MaxValue));
            Assert.Empty(reader.ReadEntriesOf(1));
            Assert.Empty(reader.ReadPeriods(1));
            Assert.Empty(reader.ReadLinks(1));
        }

        // ---------------------------------------------------------------------------------------
        // Version 2.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void SourcesExportPeriodsAndMemories_RoundTrip_AndValidate()
        {
            var counts = WriteMemoryArchive(_folder.Path);

            Assert.Equal(1, counts.ExportMessages);
            Assert.Equal(2, counts.WorkspaceMessages);
            Assert.Equal(1, counts.ExportPeriods);
            Assert.Equal(2, counts.EntryLinks);
            Assert.Equal(2, counts.ItemDefinitions);
            Assert.Equal(
                [new ArchiveCollectionCounts(1, 1, 2, 3), new ArchiveCollectionCounts(2, 1, 0, 0)],
                counts.Collections);

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Equal(2, reader.Info.FormatVersion);
            Assert.Equal(
                [new ArchiveExportPeriod(new DateTime(2015, 1, 1, 10, 0, 0), new DateTime(2015, 1, 1, 10, 0, 0))],
                reader.Info.ExportPeriods);

            var messages = reader.ReadAllPages().SelectMany(p => p.Messages).ToList();
            Assert.Equal(
                [ArchiveMessageSource.Export, ArchiveMessageSource.Workspace, ArchiveMessageSource.Workspace],
                messages.Select(m => m.Source));

            var collections = reader.ReadCollections();
            Assert.Equal(["Shared gaming", "Holidays"], collections.Select(c => c.Name));
            Assert.Equal(60, collections[0].PeriodGapMinutes);
            Assert.False(collections[0].ShownByDefault);
            Assert.Null(collections[1].PeriodGapMinutes);

            var all = reader.ReadEntries(new DateTime(2000, 1, 1), new DateTime(2100, 1, 1));
            Assert.Equal([10L, 20L], all.Select(e => e.EntryID));

            var game = all[0];
            Assert.Equal("setA|setB|2016-05-01", game.SourceKey);
            Assert.Equal("strong", game.DayLevel);
            Assert.Equal(ImageSha, game.ImageSha256);
            Assert.Equal("media/" + ImageSha + ".png", game.ImageRelativePath);
            Assert.True(File.Exists(reader.ImageFullPath(game)));
            Assert.Equal("Image", reader.ReadMediaOf(ImageSha)!.MediaType);

            // A range is start-inclusive and end-exclusive on the entry's start.
            Assert.Equal([10L], reader.ReadEntries(game.StartLocal, game.StartLocal.AddSeconds(1)).Select(e => e.EntryID));
            Assert.Empty(reader.ReadEntries(game.StartLocal.AddSeconds(1), new DateTime(2016, 5, 2)));
            Assert.Equal([20L], reader.ReadEntriesOf(2).Select(e => e.EntryID));

            var periods = reader.ReadPeriods(10);
            Assert.Equal(["strong", "one-account"], periods.Select(p => p.Label));
            Assert.Equal([ArchiveSample.Local, ArchiveSample.Other], periods[0].Items.Select(i => i.ParticipantID));
            Assert.Equal(new DateTime(2016, 5, 1, 18, 0, 0, DateTimeKind.Utc), periods[0].Items[0].TimeUtc);
            Assert.Equal(DateTimeKind.Utc, periods[0].Items[0].TimeUtc.Kind);
            Assert.Single(periods[1].Items);

            var links = reader.ReadLinks(20);
            Assert.Equal(2, links.Count);
            Assert.Equal(10L, links[0].ToEntryID);
            Assert.Equal(3, links[1].ToMessageSequenceNumber);
            Assert.Equal(2, reader.ReadAllLinks().Count);
        }

        [Fact]
        public void ItemDefinitions_RoundTrip_WithTheirIcons_ForAnEntrysItemsOnly()
        {
            WriteMemoryArchive(_folder.Path);

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            var definitions = reader.ReadItemDefinitions(10);
            Assert.Equal([("setA", "1"), ("setB", "2")], definitions.Select(d => (d.SetId, d.ItemId)));

            var named = definitions[0];
            Assert.Equal("Invented First Steps", named.Name);
            Assert.Equal("Reach the invented first door.", named.Detail);
            Assert.True(named.Hidden);
            Assert.Equal("default", named.GroupId);
            Assert.Equal(IconSha, named.IconSha256);
            Assert.Equal("media/" + IconSha + ".png", named.IconRelativePath);
            Assert.True(File.Exists(reader.IconFullPath(named)));
            Assert.Equal("Image", reader.ReadMediaOf(IconSha)!.MediaType);

            // A definition without a name or icon is carried as it is.
            Assert.Null(definitions[1].Name);
            Assert.Null(definitions[1].IconRelativePath);
            Assert.Null(reader.IconFullPath(definitions[1]));

            // Item 3 (setB, "3") has no definition; the personal entry has no items.
            Assert.Empty(reader.ReadItemDefinitions(20));
            Assert.Equal(2, reader.ReadAllItemDefinitions().Count);
        }

        [Fact]
        public void AnItemIconWhoseFileIsGoneOrDifferent_FailsMedia()
        {
            WriteMemoryArchive(_folder.Path);
            var icon = Path.Combine(_folder.Path, "media", IconSha + ".png");
            File.WriteAllBytes(icon, Encoding.ASCII.GetBytes("altered"));

            Assert.Equal(1, ArchiveValidator.Validate(_folder.Path).ProblemCounts[ArchiveProblemKind.ItemIconFileDiffers]);

            File.Delete(icon);
            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ItemIconFileMissing]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.Media).Passed);
        }

        [Fact]
        public void AnUnusedItemDefinition_IsFound_AndTheWriterRefusesOne()
        {
            WriteMemoryArchive(_folder.Path);
            ArchiveSample.Tamper(_folder.Path, "INSERT INTO ItemDefinition (SetId, ItemId, ItemType) VALUES ('setZ', '99', 'gold');");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ItemDefinitionUnused]);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ManifestCountDiffers]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.LinksAndBasis).Passed);

            var database = Path.Combine(_folder.Path, "second.db");

            using var writer = Begin(database);
            writer.WriteItemDefinition(new ArchiveItemDefinition
            {
                SetId = "setZ",
                ItemId = "99",
                ItemType = "gold",
                Name = null,
                Detail = null,
                Hidden = null,
                GroupId = null,
                IconSha256 = null,
                IconExtension = null,
            });

            var refusal = Assert.Throws<InvalidOperationException>(() => writer.Complete());
            Assert.Contains("item definitions", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWriter_RefusesAnIconWithoutAnExtension()
        {
            using var writer = Begin(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName));

            Assert.Throws<InvalidOperationException>(() => writer.WriteItemDefinition(new ArchiveItemDefinition
            {
                SetId = "setA",
                ItemId = "1",
                ItemType = "bronze",
                Name = null,
                Detail = null,
                Hidden = null,
                GroupId = null,
                IconSha256 = IconSha,
                IconExtension = null,
            }));
        }

        [Fact]
        public void AnEntryImageWhoseFileIsGone_FailsMedia()
        {
            WriteMemoryArchive(_folder.Path);
            File.Delete(Path.Combine(_folder.Path, "media", ImageSha + ".png"));

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.False(result.Passed);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.EntryImageFileMissing]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.Media).Passed);
        }

        [Fact]
        public void AnEntryImageWithOtherBytes_FailsMedia()
        {
            WriteMemoryArchive(_folder.Path);
            File.WriteAllBytes(Path.Combine(_folder.Path, "media", ImageSha + ".png"), Encoding.ASCII.GetBytes("altered"));

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.EntryImageFileDiffers]);
        }

        [Fact]
        public void ATamperedPeriodCount_AndALinkFromAnImportedEntry_FailLinks()
        {
            WriteMemoryArchive(_folder.Path);
            ArchiveSample.Tamper(_folder.Path, "UPDATE EntryPeriod SET LocalItemCount = 2 WHERE EntryID = 10 AND PeriodIndex = 0;");
            ArchiveSample.Tamper(_folder.Path, "UPDATE EntryLink SET FromEntryID = 10, ToEntryID = 20 WHERE EntryLinkID = 1;");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.PeriodCountsDiffer]);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.LinkNotFromPersonal]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.LinksAndBasis).Passed);
        }

        [Fact]
        public void ADanglingLink_AndAManifestCollectionCount_AreFound()
        {
            WriteMemoryArchive(_folder.Path);
            ArchiveSample.Tamper(
                _folder.Path, "PRAGMA foreign_keys = OFF; UPDATE EntryLink SET ToMessageSequenceNumber = 99 WHERE EntryLinkID = 2;");
            ArchiveSample.Tamper(_folder.Path, "DELETE FROM EntryItem WHERE EntryItemID = 3;");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.MemoryRowDangling]);

            // The deleted item leaves its period's count wrong and the manifest's collection count wrong.
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.PeriodCountsDiffer]);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ManifestCountDiffers]);
        }

        [Fact]
        public void TheWriter_RefusesALinkFromAnImportedEntry_AndCountsThatDifferFromItems()
        {
            var database = Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName);

            using (var writer = Begin(database))
            {
                writer.WriteEntryLink(new ArchiveEntryLink { EntryLinkID = 9, FromEntryID = 10, ToEntryID = 20, ToMessageSequenceNumber = null });

                var refusal = Assert.Throws<InvalidOperationException>(() => writer.Complete());
                Assert.Contains("personal", refusal.Message, StringComparison.Ordinal);
            }

            File.Delete(database);

            using (var writer = Begin(database, localItems: 2))
            {
                var refusal = Assert.Throws<InvalidOperationException>(() => writer.Complete());
                Assert.Contains("item counts", refusal.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void TheWriter_RefusesAnExportMessageWithAForwardedFact()
        {
            using var writer = ArchiveWriter.Create(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName), ArchiveSample.Info());

            Assert.Throws<InvalidOperationException>(() => writer.WriteMessage(Message(1, 1, "2015-01-01T10:00:00", ArchiveMessageSource.Export, forwarded: false)));
        }

        [Fact]
        public void AnExportPeriodKeyGap_IsRefusedByTheReader()
        {
            WriteMemoryArchive(_folder.Path);
            ArchiveSample.Tamper(_folder.Path, "INSERT INTO ArchiveInfo (Key, Value) VALUES ('ExportPeriod.3', '2015-01-01T10:00:00/2015-01-01T10:00:00');");

            Assert.Throws<ArchiveFormatException>(() => ArchiveReader.Open(_folder.Path));
        }

        // ---------------------------------------------------------------------------------------
        // The archive.
        // ---------------------------------------------------------------------------------------

        private static ArchiveMessage Message(
            long id, int sequence, string time, ArchiveMessageSource source, bool? forwarded = null) => new()
        {
            MessageID = id,
            SequenceNumber = sequence,
            DateTimeLocal = DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture),
            Kind = ArchiveMessageKind.User,
            SenderParticipantID = sequence % 2 == 0 ? ArchiveSample.Local : ArchiveSample.Other,
            Text = "Message " + sequence,
            IsForwarded = forwarded,
            ForwardScore = null,
            Attachments = [],
            Source = source,
        };

        private static ArchiveEntryItem Item(long id, long entry, int period, long participant, string utc) => new()
        {
            EntryItemID = id,
            EntryID = entry,
            PeriodIndex = period,
            ParticipantID = participant,
            SetId = participant == ArchiveSample.Local ? "setA" : "setB",
            ItemId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ItemType = "bronze",
            TimeUtc = DateTime.SpecifyKind(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc),
            TimeLocal = DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture).AddHours(2),
        };

        /// <summary>A writer with participants, messages and both collections written, not completed.</summary>
        /// <param name="photos">The personal entry gets a cover and two extra photos (the second is the game image).</param>
        /// <param name="photoMedia">Whether the extra photo's Media row is written.</param>
        /// <param name="formatVersion">Version 2 (with noted dates) unless a test asks for another (CH-53).</param>
        internal static ArchiveWriter Begin(
            string database, int localItems = 1, bool photos = false, bool photoMedia = true,
            int formatVersion = ArchiveFormat.NotedDatesFormatVersion)
        {
            var info = ArchiveSample.Info();

            var writer = ArchiveWriter.Create(database, new ArchiveInfo
            {
                FormatVersion = formatVersion,
                CreatedUtc = info.CreatedUtc,
                ProducerVersion = info.ProducerVersion,
                ConversationTitle = info.ConversationTitle,
                SourceExportSha256 = info.SourceExportSha256,
                TimeZoneID = info.TimeZoneID,
                LocalParticipantID = info.LocalParticipantID,
                ExportPeriods = [new ArchiveExportPeriod(new DateTime(2015, 1, 1, 10, 0, 0), new DateTime(2015, 1, 1, 10, 0, 0))],
                ArchiveId = formatVersion >= ArchiveFormat.FirstVersionWithCaptionsAndArchiveId ? ArchiveSample.ArchiveId : null,
            });

            foreach (var participant in ArchiveSample.Participants)
            {
                writer.WriteParticipant(participant);
            }

            writer.WriteMedia(new ArchiveMedia
            {
                Sha256 = ImageSha,
                MediaType = "Image",
                SizeBytes = ImageBytes.Length,
                DurationMS = null,
                Width = null,
                Height = null,
            });

            if (photos)
            {
                writer.WriteMedia(new ArchiveMedia
                {
                    Sha256 = CoverSha,
                    MediaType = "Image",
                    SizeBytes = CoverBytes.Length,
                    DurationMS = null,
                    Width = null,
                    Height = null,
                });

                if (photoMedia)
                {
                    writer.WriteMedia(new ArchiveMedia
                    {
                        Sha256 = PhotoSha,
                        MediaType = "Image",
                        SizeBytes = PhotoBytes.Length,
                        DurationMS = null,
                        Width = null,
                        Height = null,
                    });
                }
            }

            writer.WriteMessage(Message(900, 1, "2015-01-01T10:00:00", ArchiveMessageSource.Export));
            writer.WriteMessage(Message(1, 2, "2017-09-06T20:39:00", ArchiveMessageSource.Workspace));
            writer.WriteMessage(Message(2, 3, "2017-09-06T20:40:00", ArchiveMessageSource.Workspace, forwarded: true));

            writer.WriteCollection(new ArchiveCollection
            {
                CollectionID = 1,
                Kind = ArchiveMemoryVocabulary.KindImported,
                Name = "Shared gaming",
                SourceDescription = "Synthetic activity",
                ShownByDefault = false,
                PeriodGapMinutes = 60,
            });

            writer.WriteCollection(new ArchiveCollection
            {
                CollectionID = 2,
                Kind = ArchiveMemoryVocabulary.KindPersonal,
                Name = "Holidays",
                SourceDescription = null,
                ShownByDefault = true,
                PeriodGapMinutes = null,
            });

            writer.WriteEntry(new ArchiveEntry
            {
                EntryID = 10,
                CollectionID = 1,
                SourceKey = "setA|setB|2016-05-01",
                Title = "Synthetic Game",
                StartLocal = new DateTime(2016, 5, 1, 20, 0, 0),
                EndLocal = new DateTime(2016, 5, 1, 23, 30, 0),
                StartUtc = new DateTime(2016, 5, 1, 18, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2016, 5, 1, 21, 30, 0, DateTimeKind.Utc),
                DatePrecision = "Second",
                Summary = null,
                ImageSha256 = ImageSha,
                ImageExtension = ".png",
                Source = "PSN trophies",
                DayLevel = "strong",
                Periods =
                [
                    new ArchiveEntryPeriod
                    {
                        EntryID = 10,
                        PeriodIndex = 0,
                        StartUtc = new DateTime(2016, 5, 1, 18, 0, 0, DateTimeKind.Utc),
                        EndUtc = new DateTime(2016, 5, 1, 18, 0, 30, DateTimeKind.Utc),
                        StartLocal = new DateTime(2016, 5, 1, 20, 0, 0),
                        EndLocal = new DateTime(2016, 5, 1, 20, 0, 30),
                        Label = "strong",
                        LocalItemCount = localItems,
                        OtherItemCount = 1,
                        Items =
                        [
                            Item(1, 10, 0, ArchiveSample.Local, "2016-05-01T18:00:00"),
                            Item(2, 10, 0, ArchiveSample.Other, "2016-05-01T18:00:30"),
                        ],
                    },
                    new ArchiveEntryPeriod
                    {
                        EntryID = 10,
                        PeriodIndex = 1,
                        StartUtc = new DateTime(2016, 5, 1, 21, 30, 0, DateTimeKind.Utc),
                        EndUtc = new DateTime(2016, 5, 1, 21, 30, 0, DateTimeKind.Utc),
                        StartLocal = new DateTime(2016, 5, 1, 23, 30, 0),
                        EndLocal = new DateTime(2016, 5, 1, 23, 30, 0),
                        Label = "one-account",
                        LocalItemCount = 0,
                        OtherItemCount = 1,
                        Items = [Item(3, 10, 1, ArchiveSample.Other, "2016-05-01T21:30:00")],
                    },
                ],
            });

            writer.WriteEntry(new ArchiveEntry
            {
                EntryID = 20,
                CollectionID = 2,
                SourceKey = null,
                Title = "Beach week",
                StartLocal = new DateTime(2018, 1, 2),
                EndLocal = new DateTime(2018, 1, 9),
                StartUtc = null,
                EndUtc = null,
                DatePrecision = "Day",
                Summary = "Invented",
                ImageSha256 = photos ? CoverSha : null,
                ImageExtension = photos ? ".png" : null,
                Source = null,
                DayLevel = null,
                Images = photos
                    ?
                    [
                        new ArchiveEntryImage { EntryID = 20, Ordinal = 1, Sha256 = PhotoSha, Extension = ".jpg", Caption = "By the sea" },
                        new ArchiveEntryImage { EntryID = 20, Ordinal = 2, Sha256 = ImageSha, Extension = ".png", Caption = null },
                    ]
                    : [],
            });

            return writer;
        }

        /// <summary>
        /// A whole version 2 archive folder with one export message, two collections and links. With a later
        /// <paramref name="formatVersion"/> (CH-53) there are no noted dates, and <paramref name="beforeLinks"/> may
        /// add rows (yearly entries) before the links.
        /// </summary>
        internal static ArchiveCounts WriteMemoryArchive(
            string folder, bool photos = false, bool notedDates = false, bool externalLinks = false,
            int formatVersion = ArchiveFormat.NotedDatesFormatVersion, Action<ArchiveWriter>? beforeLinks = null)
        {
            ArchiveCounts counts;

            using (var writer = Begin(Path.Combine(folder, ArchiveFormat.DatabaseFileName), photos: photos, formatVersion: formatVersion))
            {
                beforeLinks?.Invoke(writer);

                if ((notedDates || externalLinks) && formatVersion == ArchiveFormat.NotedDatesFormatVersion)
                {
                    foreach (var date in NotedDates)
                    {
                        writer.WriteNotedDate(date);
                    }
                }

                if (externalLinks)
                {
                    foreach (var link in ExternalLinks.Where(l => formatVersion == ArchiveFormat.NotedDatesFormatVersion || l.OwnerKind == ExternalLinkOwnerKind.Entry))
                    {
                        writer.WriteExternalLink(link);
                    }
                }

                writer.WriteEntryLink(new ArchiveEntryLink { EntryLinkID = 1, FromEntryID = 20, ToEntryID = 10, ToMessageSequenceNumber = null });
                writer.WriteEntryLink(new ArchiveEntryLink { EntryLinkID = 2, FromEntryID = 20, ToEntryID = null, ToMessageSequenceNumber = 3 });

                writer.WriteMedia(new ArchiveMedia
                {
                    Sha256 = IconSha,
                    MediaType = "Image",
                    SizeBytes = IconBytes.Length,
                    DurationMS = null,
                    Width = null,
                    Height = null,
                });

                writer.WriteItemDefinition(new ArchiveItemDefinition
                {
                    SetId = "setA",
                    ItemId = "1",
                    ItemType = "bronze",
                    Name = "Invented First Steps",
                    Detail = "Reach the invented first door.",
                    Hidden = true,
                    GroupId = "default",
                    IconSha256 = IconSha,
                    IconExtension = ".png",
                });

                writer.WriteItemDefinition(new ArchiveItemDefinition
                {
                    SetId = "setB",
                    ItemId = "2",
                    ItemType = "bronze",
                    Name = null,
                    Detail = null,
                    Hidden = null,
                    GroupId = null,
                    IconSha256 = null,
                    IconExtension = null,
                });

                counts = writer.Complete();
            }

            var media = Path.Combine(folder, ArchiveFormat.MediaFolderName);
            Directory.CreateDirectory(media);
            File.WriteAllBytes(Path.Combine(media, ImageSha + ".png"), ImageBytes);
            File.WriteAllBytes(Path.Combine(media, IconSha + ".png"), IconBytes);

            if (photos)
            {
                File.WriteAllBytes(Path.Combine(media, CoverSha + ".png"), CoverBytes);
                File.WriteAllBytes(Path.Combine(media, PhotoSha + ".jpg"), PhotoBytes);
            }


            File.WriteAllBytes(
                Path.Combine(folder, ArchiveFormat.ManifestFileName),
                new ArchiveManifest(ArchiveManifest.FormatName, formatVersion, "Synthetic producer 1.0", ArchiveSample.Created, counts).ToBytes());
            File.WriteAllBytes(Path.Combine(folder, ArchiveContents.FileName), ArchiveContents.Build(folder));

            return counts;
        }
    }
}
