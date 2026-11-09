namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// Path containment (CH-71 step 5): a drive root keeps its separator after trimming, so a path beneath it starts with
    /// the root itself. Invented paths only; nothing is created.
    /// </summary>
    public sealed class ArchivePathsTests
    {
        [Theory]
        [InlineData(@"F:\Media\a.jpg", @"F:\", true)]
        [InlineData(@"F:\", @"F:\", true)]
        [InlineData(@"F:\Phone\Media\a.jpg", @"F:\Phone", true)]
        [InlineData(@"F:\Phone\Media\a.jpg", @"F:\Phone\", true)]
        [InlineData(@"F:\Phone", @"F:\Phone", true)]
        [InlineData(@"F:\PhoneOther\a.jpg", @"F:\Phone", false)]
        [InlineData(@"G:\Media\a.jpg", @"F:\", false)]
        [InlineData(@"f:\media\A.JPG", @"F:\", true)]
        public void IsWithin_holds_for_a_drive_root_too(string path, string folder, bool within)
        {
            Assert.Equal(within, ArchivePaths.IsWithin(path, folder));
        }

        [Theory]
        [InlineData(@"F:\Media\a.jpg", @"F:\", true)]
        [InlineData(@"F:\", @"F:\", false)]
        [InlineData(@"F:\Phone\a.jpg", @"F:\Phone", true)]
        [InlineData(@"F:\Phone", @"F:\Phone\", false)]
        [InlineData(@"F:\PhoneOther\a.jpg", @"F:\Phone", false)]
        public void IsBeneath_excludes_the_folder_itself(string path, string folder, bool beneath)
        {
            Assert.Equal(beneath, ArchivePaths.IsBeneath(path, folder));
        }
    }
}
