using System.Text;

namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// <c>CONTENTS.sha256</c>: built over every file, checked strictly, and never followed outside the
    /// folder.
    /// </summary>
    public sealed class ArchiveContentsTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public ArchiveContentsTests() => ArchiveSample.WriteFolder(_folder.Path);

        public void Dispose() => _folder.Dispose();

        [Fact]
        public void ACompleteArchive_Checks()
        {
            var check = ArchiveContents.Check(_folder.Path);

            Assert.True(check.Ok);
            // archive.db, two media files and manifest.json.
            Assert.Equal(4, check.Listed);
            Assert.Equal(4, check.Matching);
        }

        [Fact]
        public void Contents_IsSha256sumFormat_SortedWithLfEndings()
        {
            var text = File.ReadAllText(Path.Combine(_folder.Path, ArchiveContents.FileName), Encoding.UTF8);
            var lines = text.TrimEnd('\n').Split('\n');

            Assert.DoesNotContain('\r', text);
            Assert.Equal(lines.Select(l => l[66..]).Order(StringComparer.Ordinal), lines.Select(l => l[66..]));
            Assert.All(lines, l => Assert.Equal("  ", l[64..66]));
            Assert.Contains(lines, l => l.EndsWith("  " + ArchiveFormat.DatabaseFileName, StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.EndsWith(ArchiveContents.FileName, StringComparison.Ordinal));
        }

        [Fact]
        public void AChangedByte_IsCaught()
        {
            var path = Path.Combine(
                _folder.Path, ArchiveFormat.MediaFolderName,
                ArchiveFormat.MediaFileName(ArchiveSample.AlphaSha, ArchiveSample.AlphaName));

            var bytes = File.ReadAllBytes(path);
            bytes[0] ^= 0x01;
            File.WriteAllBytes(path, bytes);

            var check = ArchiveContents.Check(_folder.Path);

            Assert.False(check.Ok);
            Assert.Equal(1, check.Changed);
        }

        [Fact]
        public void AMissingFile_IsCaught()
        {
            File.Delete(Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName));

            var check = ArchiveContents.Check(_folder.Path);

            Assert.False(check.Ok);
            Assert.Equal(1, check.Missing);
        }

        [Fact]
        public void AnExtraFile_IsCaught()
        {
            File.WriteAllText(Path.Combine(_folder.Path, ArchiveFormat.MediaFolderName, "extra.txt"), "extra");

            var check = ArchiveContents.Check(_folder.Path);

            Assert.False(check.Ok);
            Assert.Equal(1, check.NotListed);
        }

        [Fact]
        public void AMissingContentsFile_IsAProblem()
        {
            File.Delete(Path.Combine(_folder.Path, ArchiveContents.FileName));

            var check = ArchiveContents.Check(_folder.Path);

            Assert.False(check.Ok);
            Assert.Single(check.Problems);
        }

        [Theory]
        [InlineData("../outside.txt")]
        [InlineData("media/../../outside.txt")]
        [InlineData("/rooted.txt")]
        [InlineData("C:/drive.txt")]
        [InlineData("media\\back.txt")]
        [InlineData("media//double.txt")]
        [InlineData("./here.txt")]
        public void AnUnusablePath_IsRefused(string path)
        {
            var line = new string('a', 64) + "  " + path + "\n";

            Assert.Throws<FormatException>(() => ArchiveContents.Parse(Encoding.UTF8.GetBytes(line)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("no newline at the end")]
        [InlineData("short  a.txt\n")]
        public void AMalformedFile_IsRefused(string content)
        {
            Assert.Throws<FormatException>(() => ArchiveContents.Parse(Encoding.UTF8.GetBytes(content)));
        }

        [Fact]
        public void CrLineEndingsAndRepeatedNames_AreRefused()
        {
            var hash = new string('a', 64);

            Assert.Throws<FormatException>(
                () => ArchiveContents.Parse(Encoding.UTF8.GetBytes(hash + "  a.txt\r\n")));

            Assert.Throws<FormatException>(
                () => ArchiveContents.Parse(Encoding.UTF8.GetBytes(hash + "  a.txt\n" + hash + "  a.txt\n")));
        }

        [Fact]
        public void APathOutsideTheFolder_IsNeverRead()
        {
            File.WriteAllText(
                Path.Combine(_folder.Path, ArchiveContents.FileName),
                new string('a', 64) + "  ../escape.txt\n");

            var check = ArchiveContents.Check(_folder.Path);

            Assert.False(check.Ok);
            Assert.Equal(0, check.Listed);
            Assert.Single(check.Problems);
        }
    }
}
