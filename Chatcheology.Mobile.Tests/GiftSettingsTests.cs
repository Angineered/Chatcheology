using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// The simpler Settings for the gift (CH-78): the start screen's import placement and the More control. Invented
    /// values only.
    /// </summary>
    public sealed class GiftSettingsTests
    {
        private static InstalledArchive Installed(string id, bool readable = true) =>
            new(id, "folder-" + id, readable ? "hash-" + id : null, readable ? "Invented chat" : null, null, 3, null, readable);

        [Fact]
        public void Import_is_the_main_button_with_no_readable_archive_and_under_more_with_one()
        {
            Assert.Equal(ImportPlacement.Main, StartImport.For(OpeningRule.Decide([])));
            Assert.Equal(ImportPlacement.Main, StartImport.For(null));
            Assert.Equal(ImportPlacement.UnderMore, StartImport.For(OpeningRule.Decide([Installed("a")])));
            Assert.Equal(ImportPlacement.UnderMore, StartImport.For(OpeningRule.Decide([Installed("a"), Installed("b")])));

            // A readable archive beside an unreadable one: the readable one is there to open, Import under More.
            Assert.Equal(ImportPlacement.UnderMore, StartImport.For(OpeningRule.Decide([Installed("a", readable: false), Installed("b")])));
        }

        [Fact]
        public void Import_stays_the_main_button_when_only_unreadable_archives_are_installed()
        {
            // Nothing can be opened: importing again is the way back (recovery), so it stays the main button.
            Assert.Equal(ImportPlacement.Main, StartImport.For(OpeningRule.Decide([Installed("a", readable: false)])));
            Assert.Equal(ImportPlacement.Main, StartImport.For(OpeningRule.Decide([Installed("a", readable: false), Installed("b", readable: false)])));
        }

        [Fact]
        public void More_control_starts_closed_and_says_so_to_screen_readers()
        {
            var more = new MoreControl("More settings");

            Assert.False(more.IsExpanded);
            Assert.Equal("More settings, collapsed", more.ScreenReaderText);
            Assert.Equal("▶︎", more.Mark);

            var open = more.Toggled();
            Assert.True(open.IsExpanded);
            Assert.Equal("More settings, expanded", open.ScreenReaderText);
            Assert.Equal("▼︎", open.Mark);
            Assert.False(open.Toggled().IsExpanded);
        }
    }
}
