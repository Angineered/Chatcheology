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
        public void First_start_opens_the_only_readable_archive_by_itself()
        {
            var only = Installed("a");

            Assert.Equal(new AutoOpen(only, ShowOnThisDay: false), StartAutoOpen.Choose(OpeningRule.Decide([only]), null));
            Assert.Equal(new AutoOpen(only, ShowOnThisDay: false), StartAutoOpen.Choose(OpeningRule.Decide([only]), "not-installed"));
        }

        [Fact]
        public void First_start_shows_the_start_screen_when_nothing_is_to_be_opened()
        {
            // List not read, none installed, the only one unreadable, several to choose from.
            Assert.Null(StartAutoOpen.Choose(null, null));
            Assert.Null(StartAutoOpen.Choose(null, "a"));
            Assert.Null(StartAutoOpen.Choose(OpeningRule.Decide([]), null));
            Assert.Null(StartAutoOpen.Choose(OpeningRule.Decide([Installed("a", readable: false)]), null));
            Assert.Null(StartAutoOpen.Choose(OpeningRule.Decide([Installed("a"), Installed("b")]), null));
        }

        [Fact]
        public void First_start_from_a_notification_opens_that_archive_with_on_this_day_when_readable()
        {
            var a = Installed("a");
            var b = Installed("b");

            Assert.Equal(new AutoOpen(b, ShowOnThisDay: true), StartAutoOpen.Choose(OpeningRule.Decide([a, b]), "b"));
            Assert.Equal(new AutoOpen(a, ShowOnThisDay: true), StartAutoOpen.Choose(OpeningRule.Decide([a]), "a"));

            // Not installed among several, or unreadable: the start screen (the list), never another archive.
            Assert.Null(StartAutoOpen.Choose(OpeningRule.Decide([a, b]), "gone"));
            Assert.Null(StartAutoOpen.Choose(OpeningRule.Decide([a, Installed("c", readable: false)]), "c"));

            // Unreadable while it is the only one: nothing to open either.
            Assert.Null(StartAutoOpen.Choose(OpeningRule.Decide([Installed("c", readable: false)]), "c"));
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
