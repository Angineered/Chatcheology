using Chatcheology.Archive;

namespace Chatcheology.Archive.Tests
{
    /// <summary>CH-54: the personal memory field rules shared by the desktop and the phone.</summary>
    public sealed class PersonalMemoryFieldRulesTests
    {
        [Fact]
        public void The_limits_are_the_desktop_ones()
        {
            Assert.Equal(200, PersonalMemoryFieldRules.MaxTitleLength);
            Assert.Equal(2000, PersonalMemoryFieldRules.MaxSummaryLength);
            Assert.Equal(200, PersonalMemoryFieldRules.MaxSourceLength);
            Assert.Equal(500, PersonalMemoryFieldRules.MaxCaptionLength);
        }

        [Fact]
        public void Texts_are_checked_after_trimming()
        {
            Assert.Equal("Give the memory a title.", PersonalMemoryFieldRules.TitleError("  "));
            Assert.Equal("Give the memory a title.", PersonalMemoryFieldRules.TitleError(null));
            Assert.Null(PersonalMemoryFieldRules.TitleError(" " + new string('t', 200) + " "));
            Assert.Equal("Keep the title to 200 characters.", PersonalMemoryFieldRules.TitleError(new string('t', 201)));

            Assert.Equal("Say where this memory comes from.", PersonalMemoryFieldRules.SourceError(""));
            Assert.Null(PersonalMemoryFieldRules.SourceError(new string('s', 200)));
            Assert.Equal("Keep the source to 200 characters.", PersonalMemoryFieldRules.SourceError(new string('s', 201)));

            Assert.Null(PersonalMemoryFieldRules.SummaryError(null));
            Assert.Null(PersonalMemoryFieldRules.SummaryError(new string('s', 2000) + "  "));
            Assert.Equal("Keep the description to 2000 characters.", PersonalMemoryFieldRules.SummaryError(new string('s', 2001)));

            Assert.Null(PersonalMemoryFieldRules.CaptionError(null));
            Assert.Null(PersonalMemoryFieldRules.CaptionError(new string('c', 500)));
            Assert.Equal("Keep each caption to 500 characters.", PersonalMemoryFieldRules.CaptionError(new string('c', 501)));

            Assert.Null(PersonalMemoryFieldRules.NormaliseText("   "));
            Assert.Equal("a b", PersonalMemoryFieldRules.NormaliseText(" a b "));
        }

        [Fact]
        public void Dates_are_compared_at_the_precision_and_the_future_is_allowed()
        {
            var start = new DateOnly(2010, 4, 20);

            Assert.Equal(new DateOnly(2010, 4, 1), PersonalMemoryFieldRules.Normalise(start, MemoryDatePrecision.Month));
            Assert.Equal(start, PersonalMemoryFieldRules.Normalise(start, MemoryDatePrecision.Day));

            Assert.Null(PersonalMemoryFieldRules.EndError(start, null, MemoryDatePrecision.Day));
            Assert.Null(PersonalMemoryFieldRules.EndError(start, new DateOnly(2099, 1, 1), MemoryDatePrecision.Day));
            Assert.Null(PersonalMemoryFieldRules.EndError(start, new DateOnly(2010, 4, 2), MemoryDatePrecision.Month));
            Assert.Equal("The end cannot be before the start.", PersonalMemoryFieldRules.EndError(start, new DateOnly(2010, 4, 19), MemoryDatePrecision.Day));
            Assert.Equal("The end cannot be before the start.", PersonalMemoryFieldRules.EndError(start, new DateOnly(2010, 3, 31), MemoryDatePrecision.Month));
        }

        [Fact]
        public void Every_year_needs_a_day_without_an_end()
        {
            Assert.Null(PersonalMemoryFieldRules.EveryYearError(false, MemoryDatePrecision.Month, new DateOnly(2016, 3, 20)));
            Assert.Null(PersonalMemoryFieldRules.EveryYearError(true, MemoryDatePrecision.Day, null));
            Assert.Equal(
                "Every year needs a memory dated to the day, not a month.",
                PersonalMemoryFieldRules.EveryYearError(true, MemoryDatePrecision.Month, null));
            Assert.Equal(
                "Every year is only for a memory without an end date.",
                PersonalMemoryFieldRules.EveryYearError(true, MemoryDatePrecision.Day, new DateOnly(2016, 3, 21)));
        }
    }
}
