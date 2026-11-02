using System.Text;
using Chatcheology.Archive;
using Chatcheology.Mobile.Import;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Synthetic;
using Chatcheology.Mobile.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>Importing an archive from a picked folder into the app's own storage (design "Import").</summary>
    public sealed class ArchiveImporterTests : IDisposable
    {
        private static readonly SyntheticArchiveOptions Small = new(Messages: 300, Days: 10, Images: 4, Missing: 1, Unmatched: 1);

        private static readonly DateTime Now = new(2026, 10, 22, 7, 30, 0, DateTimeKind.Utc);

        private readonly TemporaryFolder _root = new();
        private readonly string _source;
        private readonly ArchiveLibrary _library;
        private readonly MemorySettings _store = new();
        private readonly ArchiveSettings _settings;

        public ArchiveImporterTests()
        {
            _source = Directory.CreateDirectory(Path.Combine(_root.Path, "source")).FullName;
            _library = new ArchiveLibrary(Path.Combine(_root.Path, "files"));
            _settings = new ArchiveSettings(_store);
        }

        public void Dispose() => _root.Dispose();

        private ArchiveImporter Importer(long space = long.MaxValue) =>
            new(_library, new FixedSpace(space), _settings, new FixedClock(Now));

        private ImportResult Import(IImportSource source, long space = long.MaxValue, CancellationToken cancellationToken = default) =>
            Importer(space).Import(source, null, cancellationToken);

        private string[] StagingLeft() =>
            Directory.Exists(_library.StagingFolder) ? Directory.GetFileSystemEntries(_library.StagingFolder) : [];

        [Fact]
        public void A_valid_archive_is_copied_checked_and_installed()
        {
            SyntheticArchive.Write(_source, Small);
            var source = new FolderSource(_source);

            var result = Import(source);

            Assert.Equal(ImportOutcome.Installed, result.Outcome);
            Assert.Equal(ImportRefusal.None, result.Refusal);

            var installed = Assert.Single(_library.ListInstalled());
            Assert.Equal(result.ArchiveId, Path.GetFileName(installed));
            Assert.Equal(ArchiveLibrary.ContentsSha256Of(_source), result.ContentsSha256);
            Assert.Equal(result.ContentsSha256, ArchiveLibrary.ContentsSha256Of(installed));
            Assert.True(ArchiveValidator.Validate(installed).Passed);
            Assert.Empty(StagingLeft());

            // The source is read only: every file is still there, byte for byte.
            Assert.Equal(File.ReadAllBytes(Path.Combine(_source, ArchiveContents.FileName)), File.ReadAllBytes(Path.Combine(installed, ArchiveContents.FileName)));
            Assert.True(ArchiveContents.Check(_source).Ok);

            // The validation result and date are kept as app state, outside the archive.
            Assert.Equal((Now, 6, 6), _settings.GetValidation(result.ContentsSha256!));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_version_three_archive_with_yearly_memories_and_a_version_two_one_with_noted_dates_both_install(bool notedDates)
        {
            // CH-53: the importer accepts version 3, and a version 2 archive (gifted noted dates) still installs.
            SyntheticArchive.Write(_source, Small with { YearlyMemories = !notedDates, NotedDates = notedDates });

            var result = Import(new FolderSource(_source));

            Assert.Equal(ImportOutcome.Installed, result.Outcome);

            using var reader = ArchiveReader.Open(Assert.Single(_library.ListInstalled()));
            Assert.Equal(notedDates ? 2 : 3, reader.Info.FormatVersion);
            Assert.Equal(notedDates ? 3 : 0, reader.ReadNotedDates().Count);
            Assert.Equal(notedDates ? 0 : 3, reader.ReadYearlyEntries().Count);
        }

        [Fact]
        public void Files_the_contents_list_does_not_name_are_never_opened_or_copied()
        {
            SyntheticArchive.Write(_source, Small);
            File.WriteAllText(Path.Combine(_source, "desktop.ini"), "[.ShellClassInfo]");
            File.WriteAllText(Path.Combine(_source, "Thumbs.db"), "thumbs");
            File.WriteAllText(Path.Combine(_source, ".nomedia"), string.Empty);
            File.WriteAllText(Path.Combine(_source, ArchiveFormat.MediaFolderName, "Thumbs.db"), "thumbs");

            var source = new FolderSource(_source);
            var result = Import(source);

            Assert.Equal(ImportOutcome.Installed, result.Outcome);
            Assert.DoesNotContain(source.Opened, path => path.EndsWith("desktop.ini") || path.EndsWith("Thumbs.db") || path == ".nomedia");

            var installed = Assert.Single(_library.ListInstalled());
            Assert.Equal(
                ArchiveContents.RelativeFiles(_source).Count - 4,
                ArchiveContents.RelativeFiles(installed).Count);
        }

        [Fact]
        public void A_second_import_of_the_same_archive_is_refused_before_anything_is_copied()
        {
            SyntheticArchive.Write(_source, Small);
            var first = Import(new FolderSource(_source));

            var again = new FolderSource(_source);
            var second = Import(again);

            Assert.Equal(ImportOutcome.AlreadyInstalled, second.Outcome);
            Assert.Equal(first.ArchiveId, second.ArchiveId);
            Assert.Equal([ArchiveContents.FileName], again.Opened);
            Assert.Single(_library.ListInstalled());
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void Too_little_space_is_refused_with_both_figures_before_anything_is_copied()
        {
            SyntheticArchive.Write(_source, Small);
            var source = new FolderSource(_source);
            var size = Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

            var result = Import(source, space: 5_000);

            Assert.Equal(ImportOutcome.NotEnoughSpace, result.Outcome);
            Assert.Equal(size + ArchiveImporter.MarginFor(size), result.RequiredBytes);
            Assert.Equal(5_000, result.AvailableBytes);
            Assert.Contains(ImportResult.Bytes(result.RequiredBytes), result.Message);
            Assert.Contains(ImportResult.Bytes(5_000), result.Message);
            Assert.DoesNotContain(source.Opened, path => path != ArchiveContents.FileName && path != ArchiveFormat.ManifestFileName);
            Assert.Empty(_library.ListInstalled());
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void The_margin_is_five_percent_and_at_least_a_hundred_megabytes()
        {
            Assert.Equal(ArchiveImporter.MinimumMarginBytes, ArchiveImporter.MarginFor(0));
            Assert.Equal(ArchiveImporter.MinimumMarginBytes, ArchiveImporter.MarginFor(1_000_000_000));
            Assert.Equal(250_000_000, ArchiveImporter.MarginFor(5_000_000_000));
        }

        [Fact]
        public void Just_enough_space_is_enough()
        {
            SyntheticArchive.Write(_source, Small);
            var size = Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

            Assert.Equal(ImportOutcome.Installed, Import(new FolderSource(_source), space: size + ArchiveImporter.MarginFor(size)).Outcome);
        }

        [Fact]
        public void A_copy_that_fails_validation_is_deleted_and_the_failed_checks_are_named_with_counts()
        {
            SyntheticArchive.Write(_source, Small);

            // One media file changed after the contents list was made.
            var media = Directory.EnumerateFiles(Path.Combine(_source, ArchiveFormat.MediaFolderName)).Order(StringComparer.Ordinal).First();
            var bytes = File.ReadAllBytes(media);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(media, bytes);

            var result = Import(new FolderSource(_source));

            Assert.Equal(ImportOutcome.Refused, result.Outcome);
            Assert.Equal(ImportRefusal.FailedValidation, result.Refusal);
            Assert.Equal(6, result.CheckCount);
            Assert.Equal([ArchiveCheck.Contents, ArchiveCheck.Media], result.FailedChecks.Select(check => check.Check));
            Assert.All(result.FailedChecks, check => Assert.Equal(1, check.Problems));
            Assert.Equal("Files match the contents list", result.FailedChecks[0].Name);
            Assert.Contains("2 checks of 6 failed", result.Message);
            Assert.Empty(_library.ListInstalled());
            Assert.Empty(StagingLeft());
            Assert.Null(_settings.GetValidation(result.ContentsSha256!));
        }

        [Fact]
        public void Cancelling_during_the_copy_deletes_the_copy()
        {
            SyntheticArchive.Write(_source, Small);

            using var cancel = new CancellationTokenSource();
            var opened = 0;
            var source = new FolderSource(_source)
            {
                OnOpen = path =>
                {
                    if (path.StartsWith(ArchiveFormat.MediaFolderName, StringComparison.Ordinal) && ++opened == 2)
                    {
                        cancel.Cancel();
                    }
                },
            };

            var result = Import(source, cancellationToken: cancel.Token);

            Assert.Equal(ImportOutcome.Cancelled, result.Outcome);
            Assert.Empty(_library.ListInstalled());
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void An_import_killed_part_way_leaves_nothing_installed_and_the_next_start_cleans_it()
        {
            SyntheticArchive.Write(_source, Small);

            // What a process killed in the middle of the copy leaves: a staging folder with part of the
            // files. (An exception, unlike a kill, is cleaned up by the import itself.)
            var killed = _library.CreateStagingFolder();
            File.Copy(Path.Combine(_source, ArchiveFormat.DatabaseFileName), Path.Combine(killed, ArchiveFormat.DatabaseFileName));

            Assert.NotEmpty(StagingLeft());
            Assert.Empty(_library.ListInstalled());

            _library.CleanStaging();

            Assert.Empty(StagingLeft());
            Assert.Equal(ImportOutcome.Installed, Import(new FolderSource(_source)).Outcome);
        }

        [Fact]
        public void A_newer_format_in_the_manifest_is_refused_before_copying()
        {
            SyntheticArchive.Write(_source, Small);
            var manifest = Path.Combine(_source, ArchiveFormat.ManifestFileName);
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"formatVersion\": 3", "\"formatVersion\": 4", StringComparison.Ordinal));
            Rebuild(_source);

            var source = new FolderSource(_source);
            var result = Import(source);

            Assert.Equal(ImportOutcome.Refused, result.Outcome);
            Assert.Equal(ImportRefusal.NewerFormat, result.Refusal);
            Assert.Equal("This archive needs a newer version of the app", result.Message);
            Assert.Equal([ArchiveContents.FileName, ArchiveFormat.ManifestFileName], source.Opened);
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void A_newer_database_version_is_refused_after_copying_and_the_copy_is_deleted()
        {
            SyntheticArchive.Write(_source, Small);

            using (var connection = new SqliteConnection($"Data Source={Path.Combine(_source, ArchiveFormat.DatabaseFileName)};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 4;";
                command.ExecuteNonQuery();
            }

            Rebuild(_source);

            var result = Import(new FolderSource(_source));

            Assert.Equal(ImportRefusal.NewerFormat, result.Refusal);
            Assert.Equal(ImportResult.NewerFormatMessage, result.Message);
            Assert.Empty(_library.ListInstalled());
            Assert.Empty(StagingLeft());
        }

        [Theory]
        [InlineData("../outside.txt")]
        [InlineData("media/../../outside.txt")]
        [InlineData("/outside.txt")]
        [InlineData("C:/outside.txt")]
        [InlineData("media\\..\\..\\outside.txt")]
        [InlineData("CONTENTS.sha256")]
        public void A_listed_path_that_could_reach_outside_the_folder_is_refused_before_copying(string path)
        {
            File.WriteAllText(Path.Combine(_source, "a.txt"), "a");
            File.WriteAllBytes(
                Path.Combine(_source, ArchiveContents.FileName),
                Encoding.UTF8.GetBytes(new string('a', 64) + "  a.txt\n" + new string('b', 64) + "  " + path + "\n"));

            var source = new FolderSource(_source);
            var result = Import(source);

            Assert.Equal(ImportRefusal.ContentsListUnusable, result.Refusal);
            Assert.Equal([ArchiveContents.FileName], source.Opened);
            Assert.False(File.Exists(Path.Combine(_root.Path, "outside.txt")));
            Assert.False(File.Exists(Path.Combine(_root.Path, "files", "outside.txt")));
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void A_folder_without_a_contents_list_is_refused_before_copying()
        {
            SyntheticArchive.Write(_source, Small);
            File.Delete(Path.Combine(_source, ArchiveContents.FileName));

            var source = new FolderSource(_source);
            var result = Import(source);

            Assert.Equal(ImportRefusal.NoContentsList, result.Refusal);
            Assert.Empty(source.Opened);
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void Listed_files_missing_from_the_folder_are_counted_and_nothing_is_copied()
        {
            SyntheticArchive.Write(_source, Small);
            var media = Directory.EnumerateFiles(Path.Combine(_source, ArchiveFormat.MediaFolderName)).Order(StringComparer.Ordinal).Take(2).ToList();
            media.ForEach(File.Delete);

            var source = new FolderSource(_source);
            var result = Import(source);

            Assert.Equal(ImportRefusal.ListedFilesMissing, result.Refusal);
            Assert.Equal(2, result.MissingFiles);
            Assert.Equal("2 files the archive lists are not in the folder.", result.Message);
            Assert.Equal([ArchiveContents.FileName], source.Opened);
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void An_unexpected_exception_from_the_source_still_deletes_the_copy()
        {
            // A source adapter that lets an exception of its own kind escape (a platform exception it
            // did not translate) must not leave a partial copy in staging.
            SyntheticArchive.Write(_source, Small);
            var opened = 0;
            var source = new FolderSource(_source)
            {
                OnOpen = path =>
                {
                    if (path.StartsWith(ArchiveFormat.MediaFolderName, StringComparison.Ordinal) && ++opened == 2)
                    {
                        throw new InvalidOperationException("Simulated platform exception.");
                    }
                },
            };

            Assert.Throws<InvalidOperationException>(() => Import(source));

            Assert.Empty(_library.ListInstalled());
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void A_source_file_that_cannot_be_read_stops_the_import_and_the_copy_is_deleted()
        {
            SyntheticArchive.Write(_source, Small);
            var unreadable = "media/" + Path.GetFileName(Directory.EnumerateFiles(Path.Combine(_source, ArchiveFormat.MediaFolderName)).Order(StringComparer.Ordinal).Last());

            var result = Import(new FolderSource(_source) { Unreadable = unreadable });

            Assert.Equal(ImportRefusal.SourceUnreadable, result.Refusal);
            Assert.Empty(_library.ListInstalled());
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void Progress_counts_the_bytes_and_files_then_reports_checking()
        {
            SyntheticArchive.Write(_source, Small);
            var size = Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            var files = Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).Count();

            var reports = new List<ImportProgress>();
            var result = Importer().Import(new FolderSource(_source), new Recorder(reports), CancellationToken.None);

            Assert.Equal(ImportOutcome.Installed, result.Outcome);
            Assert.Equal(new ImportProgress(ImportStage.Copying, 0, size, 0, files), reports[0]);
            Assert.Equal(new ImportProgress(ImportStage.Checking, size, size, files, files), reports[^1]);

            var copying = reports.Where(report => report.Stage == ImportStage.Copying).ToList();
            Assert.True(copying.Zip(copying.Skip(1)).All(pair => pair.Second.BytesCopied >= pair.First.BytesCopied));
            Assert.Equal(size, copying[^1].BytesCopied);
        }

        [Fact]
        public void Results_carry_no_title_names_or_file_names()
        {
            SyntheticArchive.Write(_source, Small);
            var results = new[]
            {
                Import(new FolderSource(_source)),
                Import(new FolderSource(_source)),
            };

            foreach (var text in results.Select(result => result.Message))
            {
                Assert.DoesNotContain(SyntheticArchive.Title, text);
                Assert.DoesNotContain("Alex", text);
                Assert.DoesNotContain("IMG-", text);
                Assert.DoesNotContain(_root.Path, text);
            }
        }

        /// <summary>Rewrites the contents list after a file was changed on purpose.</summary>
        private static void Rebuild(string folder)
        {
            SqliteConnection.ClearAllPools();
            File.Delete(Path.Combine(folder, ArchiveContents.FileName));
            File.WriteAllBytes(Path.Combine(folder, ArchiveContents.FileName), ArchiveContents.Build(folder));
        }

        /// <summary>Records reports as they are made, on the importing thread.</summary>
        private sealed class Recorder(List<ImportProgress> reports) : IProgress<ImportProgress>
        {
            public void Report(ImportProgress value) => reports.Add(value);
        }
    }
}
