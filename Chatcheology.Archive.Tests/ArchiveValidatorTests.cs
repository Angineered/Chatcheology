using System.Text;

namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// The archive's self-check (CH-17): a fresh archive passes, and each kind of damage fails exactly
    /// the check it belongs to, with the right count and nothing private in the result.
    /// </summary>
    public sealed class ArchiveValidatorTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public ArchiveValidatorTests() => ArchiveSample.WriteFolder(_folder.Path, gapless: true);

        public void Dispose() => _folder.Dispose();

        private string MediaPath(string sha, string name) =>
            Path.Combine(_folder.Path, ArchiveFormat.MediaFolderName, ArchiveFormat.MediaFileName(sha, name));

        /// <summary>Damages the database, then lists the folder again, so only the damage itself is caught.</summary>
        private void TamperAndRelist(string sql)
        {
            // The archive declares its foreign keys; damage on purpose has to get past them.
            ArchiveSample.Tamper(_folder.Path, "PRAGMA foreign_keys = OFF; " + sql);
            Relist();
        }

        private void Relist() =>
            File.WriteAllBytes(Path.Combine(_folder.Path, ArchiveContents.FileName), ArchiveContents.Build(_folder.Path));

        /// <summary>Exactly these kinds have problems, with these counts; every other kind has none.</summary>
        private static void AssertOnly(ArchiveValidationResult result, params (ArchiveProblemKind Kind, int Count)[] expected)
        {
            Assert.False(result.Passed);

            foreach (var kind in Enum.GetValues<ArchiveProblemKind>())
            {
                var count = expected.Where(e => e.Kind == kind).Select(e => e.Count).SingleOrDefault();
                Assert.True(count == result.ProblemCounts[kind], $"{kind}: expected {count}, got {result.ProblemCounts[kind]}.");
            }

            var failed = expected.Select(e => ArchiveValidator.CheckOf(e.Kind)).ToHashSet();

            foreach (var check in result.Checks)
            {
                Assert.True(check.Ran, $"{check.Check} did not run.");
                Assert.Equal(failed.Contains(check.Check), !check.Passed);
            }
        }

        // ---------------------------------------------------------------------------------------
        // A sound archive.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AFreshArchive_Passes_WithEveryCheckRunAndCounted()
        {
            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.True(result.Passed);
            Assert.Empty(result.FirstProblems);
            Assert.All(result.ProblemCounts.Values, count => Assert.Equal(0, count));
            Assert.Equal(Enum.GetValues<ArchiveProblemKind>().Length, result.ProblemCounts.Count);
            Assert.True(result.Contents.Ok);

            var examined = result.Checks.ToDictionary(c => c.Check, c => c.Examined);

            // Four listed files; the database; two header and seven count fields; three Restored
            // attachments and two media files; five attachments; six messages.
            Assert.Equal(4, examined[ArchiveCheck.Contents]);
            Assert.Equal(1, examined[ArchiveCheck.Database]);
            Assert.Equal(9, examined[ArchiveCheck.Manifest]);
            Assert.Equal(5, examined[ArchiveCheck.Media]);
            Assert.Equal(5, examined[ArchiveCheck.LinksAndBasis]);
            Assert.Equal(6, examined[ArchiveCheck.SequenceAndSenders]);
        }

        [Fact]
        public void TheValidator_WritesNothing()
        {
            var before = ArchiveContents.Build(_folder.Path);
            var files = ArchiveContents.RelativeFiles(_folder.Path);

            ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(before, ArchiveContents.Build(_folder.Path));
            Assert.Equal(files, ArchiveContents.RelativeFiles(_folder.Path));
        }

        [Fact]
        public void NoFolder_IsRefused()
        {
            Assert.Throws<ArgumentException>(() => ArchiveValidator.Validate(Path.Combine(_folder.Path, "absent")));
        }

        // ---------------------------------------------------------------------------------------
        // Files.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AChangedByteInAMediaFile_FailsContentsAndMedia()
        {
            var path = MediaPath(ArchiveSample.BetaSha, ArchiveSample.BetaName);
            var bytes = File.ReadAllBytes(path);
            bytes[0] ^= 0x01;
            File.WriteAllBytes(path, bytes);

            var result = ArchiveValidator.Validate(_folder.Path);

            AssertOnly(result, (ArchiveProblemKind.ContentsFileChanged, 1), (ArchiveProblemKind.MediaFileDiffers, 1));
            Assert.Contains(new ArchiveProblem(ArchiveProblemKind.MediaFileDiffers, 4, 12), result.FirstProblems);
        }

        [Fact]
        public void ADeletedMediaFile_FailsContentsAndMedia_ForEveryAttachmentThatNamesIt()
        {
            // Alpha is shared by attachments 10 and 13.
            File.Delete(MediaPath(ArchiveSample.AlphaSha, ArchiveSample.AlphaName));

            var result = ArchiveValidator.Validate(_folder.Path);

            AssertOnly(result, (ArchiveProblemKind.ContentsFileMissing, 1), (ArchiveProblemKind.MediaFileMissing, 2));
        }

        [Fact]
        public void AnExtraFileInTheMediaFolder_FailsContentsAndMedia()
        {
            File.WriteAllText(Path.Combine(_folder.Path, ArchiveFormat.MediaFolderName, "extra.jpg"), "extra");

            var result = ArchiveValidator.Validate(_folder.Path);

            AssertOnly(result, (ArchiveProblemKind.ContentsFileNotListed, 1), (ArchiveProblemKind.MediaFileUnused, 1));
        }

        [Fact]
        public void AnExtraFileListedInTheMediaFolder_FailsMediaOnly()
        {
            File.WriteAllText(Path.Combine(_folder.Path, ArchiveFormat.MediaFolderName, "extra.jpg"), "extra");
            Relist();

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.MediaFileUnused, 1));
        }

        [Fact]
        public void AMissingContentsList_FailsContents()
        {
            File.Delete(Path.Combine(_folder.Path, ArchiveContents.FileName));

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.ContentsListUnusable, 1));
        }

        // ---------------------------------------------------------------------------------------
        // Manifest.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AnEditedManifestCount_FailsManifest()
        {
            var path = Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName);
            var manifest = ArchiveManifest.Parse(File.ReadAllBytes(path));

            File.WriteAllBytes(path, (manifest with { Counts = manifest.Counts with { Messages = manifest.Counts.Messages + 1 } }).ToBytes());
            Relist();

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.ManifestCountDiffers, 1));
        }

        [Fact]
        public void AManifestWithAnotherCreationTime_FailsManifest()
        {
            var path = Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName);
            var manifest = ArchiveManifest.Parse(File.ReadAllBytes(path));

            File.WriteAllBytes(path, (manifest with { CreatedUtc = manifest.CreatedUtc.AddSeconds(1) }).ToBytes());
            Relist();

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.ManifestHeaderDiffers, 1));
        }

        [Fact]
        public void AnUnreadableManifest_FailsManifest()
        {
            File.WriteAllText(Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName), "{ not json");
            Relist();

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.ManifestUnreadable, 1));
        }

        // ---------------------------------------------------------------------------------------
        // Database.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ADatabaseThatIsNotSqlite_FailsDatabase_AndTheChecksThatNeedItDoNotRun()
        {
            File.WriteAllBytes(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName), Encoding.ASCII.GetBytes("not a database"));
            Relist();

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.False(result.Passed);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.DatabaseUnreadable]);

            var checks = result.Checks.ToDictionary(c => c.Check);

            Assert.True(checks[ArchiveCheck.Contents].Passed);
            Assert.True(checks[ArchiveCheck.Database].Ran);
            Assert.False(checks[ArchiveCheck.Database].Passed);

            foreach (var check in new[] { ArchiveCheck.Manifest, ArchiveCheck.Media, ArchiveCheck.LinksAndBasis, ArchiveCheck.SequenceAndSenders })
            {
                Assert.False(checks[check].Ran);
                Assert.False(checks[check].Passed);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Media rows.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ARestoredAttachmentPointedAtAMissingHash_FailsMedia()
        {
            // Attachment 13 shares Alpha with 10, so Alpha stays in use.
            TamperAndRelist($"UPDATE Attachment SET MediaSha256 = '{new string('f', 64)}' WHERE AttachmentID = 13;");

            var result = ArchiveValidator.Validate(_folder.Path);

            AssertOnly(result, (ArchiveProblemKind.RestoredWithoutMediaRow, 1), (ArchiveProblemKind.MediaFileMissing, 1));
            Assert.Contains(new ArchiveProblem(ArchiveProblemKind.RestoredWithoutMediaRow, 5, 13), result.FirstProblems);
        }

        [Fact]
        public void AMediaRowNobodyNames_FailsMediaAndTheManifestCount()
        {
            TamperAndRelist(
                $"INSERT INTO Media (Sha256, MediaType, SizeBytes) VALUES ('{new string('e', 64)}', 'Image', 1);");

            AssertOnly(
                ArchiveValidator.Validate(_folder.Path),
                (ArchiveProblemKind.MediaRowUnused, 1),
                (ArchiveProblemKind.ManifestCountDiffers, 1));
        }

        // ---------------------------------------------------------------------------------------
        // Links and basis.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ADanglingLowQualityLink_FailsLinks()
        {
            TamperAndRelist("UPDATE Attachment SET LowQualityCopyOfAttachmentID = 999 WHERE AttachmentID = 13;");

            var result = ArchiveValidator.Validate(_folder.Path);

            AssertOnly(result, (ArchiveProblemKind.LowQualityLinkDangling, 1));
            Assert.Equal([new ArchiveProblem(ArchiveProblemKind.LowQualityLinkDangling, 5, 13)], result.FirstProblems);
        }

        [Fact]
        public void ARestoredAttachmentWithoutABasis_FailsBasis()
        {
            TamperAndRelist("DELETE FROM AttachmentBasis WHERE AttachmentID = 12;");

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.RestoredWithoutBasis, 1));
        }

        [Fact]
        public void ABasisOnAnUnmatchedAttachment_FailsBasis()
        {
            TamperAndRelist(
                """
                INSERT INTO AttachmentBasis (AttachmentID, BasisType, BasisText, Actor, ConfirmedUtc)
                VALUES (14, 'HumanContentOrContextInspection', 'Invented', NULL, '2026-09-01T12:00:00Z');
                """);

            var result = ArchiveValidator.Validate(_folder.Path);

            AssertOnly(result, (ArchiveProblemKind.BasisOnUnrestored, 1));
            Assert.Equal([new ArchiveProblem(ArchiveProblemKind.BasisOnUnrestored, null, 14)], result.FirstProblems);
        }

        // ---------------------------------------------------------------------------------------
        // Sequence and senders.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AGapInTheSequence_FailsSequence()
        {
            TamperAndRelist("UPDATE Message SET SequenceNumber = 9 WHERE MessageID = 106;");

            var result = ArchiveValidator.Validate(_folder.Path);

            AssertOnly(result, (ArchiveProblemKind.SequenceGap, 1));
            Assert.Equal([new ArchiveProblem(ArchiveProblemKind.SequenceGap, 9, null)], result.FirstProblems);
        }

        [Fact]
        public void TheReaderSampleWithItsDeliberateGap_FailsSequence()
        {
            using var other = new TemporaryFolder();

            // The reader's own sample leaves sequence 4 out on purpose.
            ArchiveSample.WriteFolder(other.Path);

            AssertOnly(ArchiveValidator.Validate(other.Path), (ArchiveProblemKind.SequenceGap, 1));
        }

        [Fact]
        public void ASenderWhoIsNotAParticipant_FailsSenders()
        {
            TamperAndRelist("UPDATE Message SET SenderParticipantID = 77 WHERE MessageID = 101;");

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.SenderNotParticipant, 1));
        }

        [Fact]
        public void TheWrongLocalParticipant_FailsSenders()
        {
            TamperAndRelist("UPDATE Participant SET IsLocal = 1 - IsLocal;");

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.LocalParticipantWrong, 1));
        }

        [Fact]
        public void AnAttachmentWhoseMessageIsGone_FailsLinks()
        {
            TamperAndRelist("UPDATE Attachment SET MessageID = 999 WHERE AttachmentID = 14;");

            var result = ArchiveValidator.Validate(_folder.Path);

            AssertOnly(result, (ArchiveProblemKind.AttachmentWithoutMessage, 1));
            Assert.Equal([new ArchiveProblem(ArchiveProblemKind.AttachmentWithoutMessage, null, 14)], result.FirstProblems);
        }

        [Fact]
        public void AnUnmatchedAttachmentNamingMedia_FailsMedia()
        {
            // Past the schema's own CHECK.
            TamperAndRelist(
                "PRAGMA ignore_check_constraints = ON; " +
                $"UPDATE Attachment SET MediaSha256 = '{ArchiveSample.AlphaSha}', OriginalFileName = 'x.jpg' WHERE AttachmentID = 14;");

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.UnrestoredWithMedia, 1));
        }

        [Fact]
        public void AnIndexThatNoLongerMatchesItsTable_FailsTheIntegrityCheckOnly()
        {
            // The day index's definition is changed under its stored entries, which then disagree with
            // the rows. No check reads through that index.
            TamperAndRelist(
                "PRAGMA writable_schema = ON; UPDATE sqlite_master SET sql = " +
                "'CREATE INDEX IX_Message_DateTimeLocal ON Message (Text)' WHERE name = 'IX_Message_DateTimeLocal';");

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.DatabaseIntegrityFailed, 1));
        }

        [Fact]
        public void AManifestOfAnotherFormatVersion_FailsManifest()
        {
            var path = Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName);
            var manifest = ArchiveManifest.Parse(File.ReadAllBytes(path));

            File.WriteAllBytes(path, (manifest with { FormatVersion = ArchiveFormat.FormatVersion + 1 }).ToBytes());
            Relist();

            AssertOnly(ArchiveValidator.Validate(_folder.Path), (ArchiveProblemKind.ManifestHeaderDiffers, 1));
        }

        // ---------------------------------------------------------------------------------------
        // Values of the wrong kind or size (review round).
        // ---------------------------------------------------------------------------------------

        /// <summary>Validates on another thread and fails the test, rather than hanging it, if it never returns.</summary>
        private static async Task<ArchiveValidationResult> ValidateWithinAsync(string folder)
        {
            var task = Task.Run(() => ArchiveValidator.Validate(folder));

            Assert.True(
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))) == task, "The validator did not finish.");

            return await task;
        }

        private static void AssertRowChecksNotRun(ArchiveValidationResult result)
        {
            var checks = result.Checks.ToDictionary(c => c.Check);

            Assert.False(result.Passed);
            Assert.True(checks[ArchiveCheck.Contents].Passed);
            Assert.False(checks[ArchiveCheck.Media].Ran);
            Assert.False(checks[ArchiveCheck.LinksAndBasis].Ran);
        }

        [Theory]
        [InlineData("'abc'")]
        [InlineData("1.5")]
        [InlineData("3000000000")]
        [InlineData("0")]
        [InlineData("-1")]
        public async Task ASequenceNumberThatIsNotAWholeNumberFromOne_FailsSequence_WithoutHanging(string value)
        {
            TamperAndRelist(
                $"PRAGMA ignore_check_constraints = ON; UPDATE Message SET SequenceNumber = {value} WHERE MessageID = 106;");

            var result = await ValidateWithinAsync(_folder.Path);

            AssertRowChecksNotRun(result);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.SequenceNumberInvalid]);
            Assert.Equal(0, result.ProblemCounts[ArchiveProblemKind.DatabaseUnreadable]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.SequenceAndSenders).Passed);
        }

        [Theory]
        [InlineData("UPDATE Media SET Width = 3000000000;")]
        [InlineData("UPDATE Attachment SET ForwardScore = 3000000000 WHERE AttachmentID = 10;")]
        [InlineData("UPDATE Attachment SET Ordinal = 3000000000 WHERE AttachmentID = 10;")]
        [InlineData("UPDATE Attachment SET ForwardScore = 'many' WHERE AttachmentID = 10;")]
        [InlineData("UPDATE Message SET Text = X'01' WHERE MessageID = 101;")]
        [InlineData("UPDATE Participant SET DisplayName = X'02' WHERE ParticipantID = 2;")]
        [InlineData("UPDATE ArchiveInfo SET Key = NULL WHERE Key = 'TimeZoneID';")]
        public async Task AValueOfTheWrongKindOrSize_FailsDatabase_InsteadOfThrowing(string sql)
        {
            TamperAndRelist(sql);

            var result = await ValidateWithinAsync(_folder.Path);

            AssertRowChecksNotRun(result);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.DatabaseUnreadable]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.Database).Passed);
        }

        [Fact]
        public void TheReader_RefusesAValueOfTheWrongKind_AndPagingThatCannotReadEveryMessage()
        {
            TamperAndRelist("PRAGMA ignore_check_constraints = ON; UPDATE Message SET SequenceNumber = 'abc' WHERE MessageID = 106;");

            using (var reader = ArchiveReader.Open(_folder.Path))
            {
                Assert.Throws<ArchiveFormatException>(() => reader.ReadFirstPage(ArchiveReader.MaximumPageSize));
                Assert.Throws<ArchiveFormatException>(() => reader.ReadAllPages().ToList());
            }

            TamperAndRelist("PRAGMA ignore_check_constraints = ON; UPDATE Message SET SequenceNumber = 0 WHERE MessageID = 106;");

            using (var reader = ArchiveReader.Open(_folder.Path))
            {
                // Paging starts at 1, so the message at 0 is never on a page: the count gives it away.
                Assert.Throws<ArchiveFormatException>(() => reader.ReadAllPages().ToList());
            }
        }

        [Fact]
        public async Task AFileThatCannotBeRead_IsAProblem_NotAnException()
        {
            var path = MediaPath(ArchiveSample.BetaSha, ArchiveSample.BetaName);

            ArchiveValidationResult result;

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = await ValidateWithinAsync(_folder.Path);
            }

            Assert.False(result.Passed);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.FileUnreadable]);

            // The database checks still ran; the locked file counts as missing for its attachment.
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.MediaFileMissing]);
            Assert.Empty(result.Contents.Problems);
        }

        // ---------------------------------------------------------------------------------------
        // Nothing private.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheResult_CarriesNoTextNameFileNameOrPath()
        {
            File.Delete(MediaPath(ArchiveSample.AlphaSha, ArchiveSample.AlphaName));
            File.WriteAllText(Path.Combine(_folder.Path, ArchiveFormat.MediaFolderName, "extra.jpg"), "extra");
            ArchiveSample.Tamper(_folder.Path, "PRAGMA foreign_keys = OFF; UPDATE Message SET SenderParticipantID = 77 WHERE MessageID = 101;");
            ArchiveSample.Tamper(_folder.Path, "PRAGMA foreign_keys = OFF; UPDATE Attachment SET LowQualityCopyOfAttachmentID = 999 WHERE AttachmentID = 13;");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.False(result.Passed);

            var text = string.Join(
                "\n",
                result.FirstProblems.Select(p => p.ToString())
                    .Concat(result.ProblemCounts.Select(p => p.ToString()))
                    .Concat(result.Checks.Select(c => $"{c.Check} {c.Ran} {c.Examined} {c.Problems}"))
                    .Concat(result.Contents.Problems));

            foreach (var secret in new[]
                     {
                         "Alex", "Sam", "Hello", "second line", "Late", "System line", ArchiveSample.AlphaName,
                         ArchiveSample.BetaName, ArchiveSample.AlphaSha, "extra.jpg", _folder.Path, "Basis for",
                     })
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }
    }
}
