using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Presentation;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// CH-29, CH-31 and CH-34: the waiting change of "me" after the date picker's day read, the date
    /// picker's year and month selectors, what a message shares, and a photo's Info.
    /// </summary>
    public sealed class ShareInfoAndDateSelectorTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        /// <summary>Writes the archive and loads the whole conversation's message rows.</summary>
        private List<MessageRow> Messages(ArchiveBuilder builder)
        {
            builder.Write(_folder.Path);

            using var reader = ArchiveReader.Open(_folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);
            timeline.OpenAt(1);

            while (timeline.ReadLater() is { } page)
            {
                timeline.Append(page);
            }

            return timeline.Rows.OfType<MessageRow>().ToList();
        }

        private static Dictionary<string, string> Details(AttachmentCard card) =>
            card.Details.ToDictionary(detail => detail.Label, detail => detail.Value);

        // ---------------------------------------------------------------------------------------
        // CH-34: sharing a message's text.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_message_shares_its_text_only_with_links_and_emoji_as_written()
        {
            const string text = "See https://example.org/a?b=1 and mail sam@example.com \U0001F600\U0001F44D\nSecond line";

            var builder = new ArchiveBuilder();
            builder.Add(text, ArchiveBuilder.Other, new DateTime(2024, 5, 6, 7, 8, 9), forwarded: true, score: 9);
            builder.Add("Mine too", ArchiveBuilder.Local);

            var messages = Messages(builder);

            // Exactly the text: no date, no time, no sender, no forwarded label.
            Assert.Equal(text, messages[0].ShareText);
            Assert.True(messages[0].CanShareText);
            Assert.DoesNotContain("Sam", messages[0].ShareText!.Replace("sam@", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain(messages[0].TimeText, messages[0].ShareText!, StringComparison.Ordinal);
            Assert.Equal("Mine too", messages[1].ShareText);
        }

        [Fact]
        public void A_placeholder_only_message_and_a_system_line_offer_no_text_share()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));
            builder.Add("<Media omitted>", ArchiveBuilder.Local, null, null, null, new AttachmentSpec(2));
            builder.Add("Look: <Media omitted>", ArchiveBuilder.Other, null, null, null, new AttachmentSpec(3));
            builder.Add("Messages are end-to-end encrypted.", sender: null);

            var messages = Messages(builder);

            Assert.Null(messages[0].ShareText);
            Assert.False(messages[0].CanShareText);
            Assert.Null(messages[1].ShareText);

            // Only the exact placeholder counts as no text; anything else is shared as written.
            Assert.Equal("Look: <Media omitted>", messages[2].ShareText);

            Assert.True(messages[3].IsSystem);
            Assert.Null(messages[3].ShareText);
        }

        // ---------------------------------------------------------------------------------------
        // CH-34: a photo's Info.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Info_shows_every_detail_the_archive_holds_in_order()
        {
            var at = new DateTime(2019, 11, 23, 21, 47, 13);
            var builder = new ArchiveBuilder();
            builder.Add(
                "<Media omitted>",
                ArchiveBuilder.Other,
                at,
                null,
                null,
                new AttachmentSpec(7, Width: 4032, Height: 3024, IsForwarded: true, ForwardScore: 2));

            var card = Assert.Single(Assert.Single(Messages(builder)).Attachments);

            Assert.Equal(
                [
                    ("Date", DateSeparatorRow.DayText(DateOnly.FromDateTime(at))),
                    ("Time", MessageRow.TimeTextOf(at)),
                    ("Sender", "Sam"),
                    ("Type", "Image"),
                    ("Dimensions", string.Format(CultureInfo.CurrentCulture, "{0} × {1}", 4032, 3024)),
                    ("Size", MediaDetails.SizeText("synthetic media payload 7".Length)),
                    ("Forwarded", "Yes"),
                    ("File name", "FILE-7.jpg"),
                ],
                card.Details.Select(detail => (detail.Label, detail.Value)));
        }

        [Fact]
        public void Info_leaves_out_what_the_archive_does_not_hold()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1, Width: null, Height: null));
            builder.Media(new AttachmentSpec(2, Width: 640, Height: null));
            builder.Media(new AttachmentSpec(3, Width: null, Height: 480));

            foreach (var message in Messages(builder))
            {
                var details = Details(Assert.Single(message.Attachments));

                Assert.DoesNotContain("Dimensions", details.Keys);
                Assert.DoesNotContain("Forwarded", details.Keys);
                Assert.True(details.Values.All(value => value.Length > 0));
                Assert.DoesNotContain(details.Values, value => value.Contains("unknown", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(["Date", "Time", "Sender", "Type", "Size", "File name"], details.Keys);
            }
        }

        [Fact]
        public void Info_of_a_photo_without_a_sender_has_no_sender_line()
        {
            var builder = new ArchiveBuilder();
            builder.Add("<Media omitted>", sender: null, null, null, null, new AttachmentSpec(1, Width: 10, Height: 10));

            var details = Details(Assert.Single(Assert.Single(Messages(builder)).Attachments));

            Assert.DoesNotContain("Sender", details.Keys);
            Assert.DoesNotContain(details.Values, value => value == "System");
        }

        [Theory]
        [InlineData(null, null, null, null)]
        [InlineData(false, null, null, null)]
        [InlineData(false, 9, null, null)]
        [InlineData(true, null, null, "Yes")]
        [InlineData(true, 4, null, "Yes")]
        [InlineData(true, 5, null, "Many times")]
        [InlineData(null, null, true, null)]
        public void Info_shows_forwarded_only_when_the_attachment_stores_one(
            bool? forwarded, int? score, bool? messageForwarded, string? expected)
        {
            var builder = new ArchiveBuilder();
            builder.Add("<Media omitted>", ArchiveBuilder.Other, null, messageForwarded, null, new AttachmentSpec(1, IsForwarded: forwarded, ForwardScore: score));

            var details = Details(Assert.Single(Assert.Single(Messages(builder)).Attachments));

            Assert.Equal(expected, details.GetValueOrDefault("Forwarded"));
        }

        [Fact]
        public void Info_of_an_attachment_without_a_media_row_or_file_name_keeps_only_the_message_facts()
        {
            var attachment = new ArchiveAttachment
            {
                AttachmentID = 1,
                MessageID = 1,
                Ordinal = 1,
                State = ArchiveAttachmentState.Missing,
                MediaSha256 = null,
                OriginalFileName = null,
                IsForwarded = null,
                ForwardScore = null,
                LowQualityCopyOfAttachmentID = null,
                Basis = null,
            };

            var details = MediaDetails.Of(new DateTime(2020, 1, 2, 3, 4, 5), "Alex", attachment, media: null);

            Assert.Equal(["Date", "Time", "Sender"], details.Select(detail => detail.Label));
        }

        [Theory]
        [InlineData(0, "0 bytes")]
        [InlineData(1, "1 byte")]
        [InlineData(999, "999 bytes")]
        [InlineData(1000, "1 KB")]
        [InlineData(1550, "1.6 KB")]
        [InlineData(999_949, "999.9 KB")]
        [InlineData(999_950, "1 MB")]
        [InlineData(2_400_000, "2.4 MB")]
        [InlineData(3_000_000_000, "3 GB")]
        public void Sizes_are_shown_in_steps_of_a_thousand(long bytes, string expected)
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            try
            {
                Assert.Equal(expected, MediaDetails.SizeText(bytes));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        // ---------------------------------------------------------------------------------------
        // CH-31: the year and month selectors.
        // ---------------------------------------------------------------------------------------

        private static DayCalendar Calendar() => new(
            [
                new DateOnly(2012, 2, 14), new DateOnly(2012, 2, 15), new DateOnly(2012, 9, 1),
                new DateOnly(2016, 6, 30),
                new DateOnly(2025, 1, 3), new DateOnly(2025, 3, 9), new DateOnly(2025, 12, 31),
            ],
            DayOfWeek.Monday,
            CultureInfo.InvariantCulture);

        [Fact]
        public void The_year_selector_offers_only_years_with_messages_oldest_first()
        {
            Assert.Equal([2012, 2016, 2025], Calendar().Years);
            Assert.Empty(new DayCalendar([]).Years);
        }

        [Fact]
        public void The_month_selector_offers_only_that_years_months_with_messages()
        {
            var calendar = Calendar();

            var months = calendar.MonthsIn(2025);

            Assert.Equal([1, 3, 12], months.Select(choice => choice.Month));
            Assert.Equal(["January", "March", "December"], months.Select(choice => choice.Name));

            // Each choice moves the grid to exactly that month.
            foreach (var choice in months)
            {
                var month = calendar.Month(choice.Index);
                Assert.Equal((2025, choice.Month), (month.Year, month.Month));
            }

            Assert.Empty(calendar.MonthsIn(2013));
        }

        [Theory]
        [InlineData(2012, 2, 2012, 2)]
        [InlineData(2012, 12, 2012, 9)]
        [InlineData(2012, 5, 2012, 2)]
        [InlineData(2012, 6, 2012, 9)]
        [InlineData(2016, 1, 2016, 6)]
        [InlineData(2025, 2, 2025, 3)]
        public void Picking_a_year_moves_to_its_month_nearest_the_one_shown(int year, int shownMonth, int expectedYear, int expectedMonth)
        {
            var calendar = Calendar();

            var month = calendar.Month(calendar.IndexInYear(year, shownMonth));

            Assert.Equal((expectedYear, expectedMonth), (month.Year, month.Month));
        }

        [Fact]
        public void A_year_without_messages_is_not_a_place_to_go()
        {
            Assert.Equal(-1, Calendar().IndexInYear(2014, 6));
        }

        [Fact]
        public void A_far_past_day_is_two_choices_away_and_days_without_messages_stay_disabled()
        {
            var calendar = Calendar();

            // From the newest month: the year, then the month.
            var shown = calendar.Month(calendar.MonthCount - 1);
            var september = calendar.Month(calendar.IndexInYear(2012, shown.Month));

            Assert.Equal((2012, 9), (september.Year, september.Month));

            var february = calendar.Month(calendar.MonthsIn(2012).Single(choice => choice.Month == 2).Index);
            var days = february.Cells.Where(cell => cell.Day is not null).ToList();

            Assert.Equal((2012, 2), (february.Year, february.Month));
            Assert.Equal(29, days.Count);
            Assert.Equal([14, 15], days.Where(cell => cell.HasMessages).Select(cell => cell.Day!.Value.Day));
        }

        // ---------------------------------------------------------------------------------------
        // CH-29: a change of "me" during the date picker's day read.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task A_change_of_me_during_the_day_read_is_rebuilt_through_the_gate_when_the_read_ends()
        {
            var gate = new ArchiveReadGate();
            var turn = new ReadTurn(gate);
            var reading = new TaskCompletionSource();
            var rebuilds = 0;

            // The date picker's day read takes the turn and runs through the gate.
            Assert.True(turn.TryBegin());
            var days = gate.RunAsync(() => reading.Task, () => 3);

            // "Me" changes in Settings while the days are read: it waits.
            Assert.False(turn.MeChanged());
            Assert.True(turn.IsMeChangePending);

            // Nor can a reopen start meanwhile.
            Assert.False(turn.TryBegin());

            reading.SetResult();
            Assert.True((await days).Ran);

            // The read ends: the waiting change is rebuilt now, through the gate like any read.
            Assert.True(turn.End());
            Assert.False(turn.IsMeChangePending);
            Assert.True(turn.TryBegin());

            var rebuild = await gate.RunAsync(() => Task.CompletedTask, () => ++rebuilds);

            Assert.True(rebuild.Ran);
            Assert.False(turn.End());
            Assert.Equal(1, rebuilds);
        }

        [Fact]
        public void A_change_of_me_with_no_read_under_way_is_rebuilt_at_once_and_none_is_left_waiting()
        {
            var turn = new ReadTurn(new ArchiveReadGate());

            Assert.True(turn.MeChanged());
            Assert.False(turn.IsMeChangePending);

            Assert.True(turn.TryBegin());
            Assert.False(turn.End());
        }

        [Fact]
        public void Several_changes_during_one_read_are_rebuilt_once()
        {
            var turn = new ReadTurn(new ArchiveReadGate());

            Assert.True(turn.TryBegin());
            Assert.False(turn.MeChanged());
            Assert.False(turn.MeChanged());

            Assert.True(turn.End());
            Assert.False(turn.End());
        }

        [Fact]
        public async Task A_waiting_change_is_dropped_when_the_page_closes_during_the_read()
        {
            var gate = new ArchiveReadGate();
            var turn = new ReadTurn(gate);

            Assert.True(turn.TryBegin());
            Assert.False(turn.MeChanged());

            await gate.CloseAsync(() => Task.CompletedTask, () => { });

            // Closing: nothing is rebuilt, and no read takes the turn again.
            Assert.False(turn.End());
            Assert.False(turn.TryBegin());
            Assert.False(turn.MeChanged());
        }
    }
}
