using Chatcheology.Archive;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Synthetic;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>Staging, installing and cleaning up in the app's own storage.</summary>
    public sealed class ArchiveLibraryTests
    {
        private static readonly SyntheticArchiveOptions Small = new(Messages: 300, Days: 10, Images: 4, Missing: 1, Unmatched: 1);

        [Fact]
        public void A_staging_copy_that_passes_is_moved_into_the_installed_archives()
        {
            using var files = new TemporaryFolder();
            var library = new ArchiveLibrary(files.Path);

            var staging = library.CreateStagingFolder();
            SyntheticArchive.Write(staging, Small);

            var outcome = library.Install(staging);

            Assert.True(outcome.Installed);
            Assert.True(outcome.Validation.Passed);
            Assert.False(Directory.Exists(staging));
            Assert.Equal([outcome.InstalledFolder!], library.ListInstalled());
            Assert.Equal(Path.GetFileName(staging), Path.GetFileName(outcome.InstalledFolder));
            Assert.True(ArchiveValidator.Validate(outcome.InstalledFolder!).Passed);
        }

        [Fact]
        public void A_staging_copy_that_fails_is_deleted_and_nothing_is_installed()
        {
            using var files = new TemporaryFolder();
            var library = new ArchiveLibrary(files.Path);

            var staging = library.CreateStagingFolder();
            SyntheticArchive.Write(staging, Small);
            File.WriteAllText(Path.Combine(staging, "extra.txt"), "not listed");

            var outcome = library.Install(staging);

            Assert.False(outcome.Installed);
            Assert.False(outcome.Validation.Passed);
            Assert.False(Directory.Exists(staging));
            Assert.Empty(library.ListInstalled());
        }

        [Fact]
        public void Cleaning_staging_removes_whatever_an_interrupted_import_left()
        {
            using var files = new TemporaryFolder();
            var library = new ArchiveLibrary(files.Path);

            var staging = library.CreateStagingFolder();
            File.WriteAllText(Path.Combine(staging, "partial.db"), "half a copy");

            library.CleanStaging();

            Assert.False(Directory.Exists(library.StagingFolder));
            Assert.Empty(library.ListInstalled());
        }

        [Fact]
        public void Only_a_folder_directly_inside_staging_can_be_installed()
        {
            using var files = new TemporaryFolder();
            var library = new ArchiveLibrary(files.Path);

            var elsewhere = Path.Combine(files.Path, "elsewhere");
            Directory.CreateDirectory(elsewhere);

            Assert.Throws<ArgumentException>(() => library.Install(elsewhere));
            Assert.Throws<ArgumentException>(() => library.Install(library.StagingFolder));
            Assert.True(Directory.Exists(elsewhere));
        }

        [Fact]
        public void No_archives_folder_means_none_installed()
        {
            using var files = new TemporaryFolder();

            Assert.Empty(new ArchiveLibrary(files.Path).ListInstalled());
        }
    }
}
