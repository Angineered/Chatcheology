using Chatcheology.Archive;
using Chatcheology.Mobile.Import;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Synthetic;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>CH-58: the photo-location permission's decisions around an import.</summary>
    public sealed class MediaLocationAccessTests : IDisposable
    {
        private static readonly SyntheticArchiveOptions Small = new(Messages: 300, Days: 10, Images: 4, Missing: 1, Unmatched: 1);

        private readonly TemporaryFolder _root = new();

        public void Dispose() => _root.Dispose();

        [Theory]
        [InlineData(26, false, false)]
        [InlineData(28, false, false)]
        [InlineData(28, true, false)]
        [InlineData(29, false, true)]
        [InlineData(29, true, false)]
        [InlineData(33, false, true)]
        [InlineData(35, true, false)]
        public void It_is_asked_for_on_Android_10_and_later_only_when_not_granted(int apiLevel, bool granted, bool asks)
        {
            Assert.Equal(asks, MediaLocationAccess.ShouldAsk(apiLevel, granted));
        }

        [Theory]
        [InlineData(28, false, ImportReadPath.Document, false)]
        [InlineData(28, true, ImportReadPath.Document, false)]
        [InlineData(29, false, ImportReadPath.Document, true)]
        [InlineData(29, true, ImportReadPath.MediaUri, false)]
        [InlineData(33, false, ImportReadPath.Document, true)]
        [InlineData(33, true, ImportReadPath.MediaUri, false)]
        public void The_media_store_address_is_used_only_on_Android_10_and_later_with_the_permission(
            int apiLevel, bool granted, ImportReadPath path, bool refused)
        {
            Assert.Equal(path, MediaLocationAccess.ChooseReadPath(apiLevel, granted));
            Assert.Equal(refused, MediaLocationAccess.IsRefused(apiLevel, granted));
        }

        [Fact]
        public void A_photo_changed_while_the_permission_was_refused_adds_the_generic_line()
        {
            var result = ImportWithOneChangedFile(ArchiveFormat.MediaFolderName);

            Assert.Equal(ImportRefusal.FailedValidation, result.Refusal);
            Assert.True(MediaLocationAccess.ShowsRefusedLine(refused: true, result));
            Assert.False(MediaLocationAccess.ShowsRefusedLine(refused: false, result));
        }

        [Fact]
        public void The_line_is_generic_text_only()
        {
            Assert.Equal(
                "Some photos may have been changed by Android because photo-location access was refused. Allow it in the app's settings and import again.",
                MediaLocationAccess.RefusedLine);
        }

        [Fact]
        public void Other_outcomes_never_add_the_line_even_when_refused()
        {
            var source = Path.Combine(_root.Path, "installed");
            SyntheticArchive.Write(source, Small);
            var installed = Importer("installed-files").Import(new FolderSource(source), null, CancellationToken.None);

            Assert.Equal(ImportOutcome.Installed, installed.Outcome);
            Assert.False(MediaLocationAccess.ShowsRefusedLine(refused: true, installed));

            var unreadable = Importer("unreadable-files").Import(
                new FolderSource(source) { Unreadable = ArchiveFormat.DatabaseFileName },
                null,
                CancellationToken.None);

            Assert.Equal(ImportRefusal.SourceUnreadable, unreadable.Refusal);
            Assert.False(MediaLocationAccess.ShowsRefusedLine(refused: true, unreadable));
        }

        [Fact]
        public void A_failure_outside_the_contents_and_media_checks_does_not_add_the_line()
        {
            // A changed manifest fails its own checks, but the contents list is made to match it, so neither
            // the contents nor the media check fails.
            var source = Path.Combine(_root.Path, "manifest");
            SyntheticArchive.Write(source, Small);
            File.WriteAllText(Path.Combine(source, ArchiveFormat.ManifestFileName), "{}");
            File.WriteAllBytes(Path.Combine(source, ArchiveContents.FileName), ArchiveContents.Build(source));

            var result = Importer("manifest-files").Import(new FolderSource(source), null, CancellationToken.None);

            Assert.Equal(ImportRefusal.FailedValidation, result.Refusal);
            Assert.DoesNotContain(result.FailedChecks, check => check.Check is ArchiveCheck.Contents or ArchiveCheck.Media);
            Assert.False(MediaLocationAccess.ShowsRefusedLine(refused: true, result));
        }

        [Fact]
        public void The_read_counts_are_numbers_only()
        {
            var counts = new ImportReadCounts();
            counts.CountMediaUri();
            counts.CountMediaUri();
            counts.CountFallback();

            Assert.Equal("mediaUri=2 fallback=1", counts.ToString());
        }

        private ImportResult ImportWithOneChangedFile(string folder)
        {
            var source = Path.Combine(_root.Path, "changed");
            SyntheticArchive.Write(source, Small);

            var media = Directory.EnumerateFiles(Path.Combine(source, folder)).Order(StringComparer.Ordinal).First();
            var bytes = File.ReadAllBytes(media);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(media, bytes);

            return Importer("changed-files").Import(new FolderSource(source), null, CancellationToken.None);
        }

        private ArchiveImporter Importer(string files) =>
            new(new ArchiveLibrary(Path.Combine(_root.Path, files)), new FixedSpace(long.MaxValue), new ArchiveSettings(new MemorySettings()), new FixedClock(DateTime.UtcNow));
    }
}
