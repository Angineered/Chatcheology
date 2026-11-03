using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Synthetic;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// External gallery links on the phone (CH-48 step C): the shared rules reused, gifted links read from the
    /// archive, own dates' links in settings (damaged values, order, Forget), the card text for one and several,
    /// and the second check before a link is handed to another app. Invented addresses only (example.org and
    /// its kind), never a real album.
    /// </summary>
    public sealed class GalleryLinksTests : IDisposable
    {
        private const string One = "https://photos.example.org/album/one";
        private const string Two = "https://pictures.example.net/share/two?key=abc";

        private static readonly DateTime Day = new(2025, 3, 10, 20, 0, 0);

        private readonly TemporaryFolder _root = new();
        private readonly MemorySettings _store = new();
        private readonly ArchiveSettings _settings;

        public GalleryLinksTests()
        {
            _settings = new ArchiveSettings(_store);
        }

        public void Dispose() => _root.Dispose();

        private (ArchiveSession Session, string Folder) Open(MemoryArchiveBuilder builder)
        {
            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library-" + Guid.NewGuid().ToString("N")));
            var staging = library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = library.Install(staging);
            Assert.True(outcome.Installed, string.Join(", ", outcome.Validation.FirstProblems));

            var archive = library.ListArchives(_settings).Single();

            return (ArchiveSession.Open(archive, _settings), archive.Folder);
        }

        private static List<EntryCardRow> Cards(ArchiveSession session)
        {
            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            return timeline.Rows.OfType<EntryCardRow>().OrderBy(r => r.Key.EntryID).ToList();
        }

        // ---------------------------------------------------------------------------------------------
        // The shared rules, reused.
        // ---------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("http://photos.example.org/a")]
        [InlineData("javascript:alert(1)")]
        [InlineData("intent://x#Intent;end")]
        [InlineData("file:///sdcard/x")]
        [InlineData("content://x/y")]
        [InlineData("https://photos.example.org@evil.example.com/a")]
        [InlineData("https://127.0.0.1/a")]
        [InlineData("https://2130706433/a")]
        [InlineData("https://photos.example.org/a b")]
        [InlineData("https://photos.example.org/‮b")]
        [InlineData("/relative")]
        [InlineData("https://")]
        public void A_link_the_shared_rules_refuse_is_never_kept(string url)
        {
            Assert.NotNull(ExternalLinkRules.UrlError(ExternalLinkRules.NormaliseUrl(url)));
            Assert.Null(GalleryLinks.Keep(url, null));
            Assert.False(GalleryLinks.Check([(url, null)]).IsValid);
        }

        [Fact]
        public void An_edited_row_left_wholly_blank_is_left_out_but_a_label_alone_is_a_problem()
        {
            Assert.True(GalleryLinks.Check([("", null), ("  ", "  "), (null, null)]).IsValid);
            Assert.Empty(GalleryLinks.Check([("", null)]).Links);
            Assert.Equal([0], GalleryLinks.Check([(null, "Day 1")]).RowErrors.Keys);
        }

        [Fact]
        public void A_kept_link_is_normalised_and_shows_the_shared_host()
        {
            var link = GalleryLinks.Keep("  https://Photos.Example.ORG/Album  ", "  Day 1  ")!;

            Assert.Equal("https://Photos.Example.ORG/Album", link.Url);
            Assert.Equal("Day 1", link.Label);
            Assert.Equal(ExternalLinkRules.DisplayHost(link.Url), link.Host);
            Assert.Equal("photos.example.org", link.Host);
            Assert.Equal("Day 1", link.Text);

            var noLabel = GalleryLinks.Keep(One, "   ")!;
            Assert.Null(noLabel.Label);
            Assert.Equal("photos.example.org", noLabel.Text);

            // A label that breaks the rules drops the link.
            Assert.Null(GalleryLinks.Keep(One, new string('x', ExternalLinkRules.MaxLabelLength + 1)));
            Assert.Null(GalleryLinks.Keep(One, "a​b"));
        }

        [Fact]
        public void An_internationalised_host_is_shown_in_punycode()
        {
            var link = GalleryLinks.Keep("https://bücher.example/album", null)!;

            Assert.Equal("xn--bcher-kva.example", link.Host);
            Assert.Equal("Gallery · xn--bcher-kva.example", GalleryLinks.Summary([link]));
        }

        // ---------------------------------------------------------------------------------------------
        // Display text.
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public void The_card_says_gallery_and_host_for_one_and_a_count_for_several()
        {
            var one = GalleryLinks.Keep(One, "Day 1")!;
            var two = GalleryLinks.Keep(Two, null)!;

            Assert.Equal(string.Empty, GalleryLinks.Summary([]));
            Assert.Equal("Gallery · photos.example.org", GalleryLinks.Summary([one]));
            Assert.Equal("2 galleries", GalleryLinks.Summary([one, two]));
            Assert.Equal(["1. Day 1", "2. pictures.example.net"], GalleryLinks.Choices([one, two]));
        }

        // ---------------------------------------------------------------------------------------------
        // Opening: checked again, immediately before use.
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public void Open_hands_over_the_launch_form_never_the_stored_text()
        {
            var link = GalleryLinks.Keep("https://Photos.Example.ORG/Album?x=1", null)!;
            var started = new List<string>();

            var result = GalleryLinks.Open(link, link.Host!, launch =>
            {
                started.Add(launch);
                return true;
            });

            Assert.Equal(GalleryOpenResult.Opened, result);
            Assert.Equal([ExternalLinkRules.LaunchUri(link.Url)!], started);
            Assert.Equal("https://photos.example.org/Album?x=1", started[0]);
            Assert.NotEqual(link.Url, started[0]);
            Assert.Null(GalleryLinks.ResultText(result));
        }

        [Theory]
        [InlineData("http://photos.example.org/a")]
        [InlineData("javascript:alert(1)")]
        [InlineData("https://photos.example.org@evil.example.com/a")]
        [InlineData("https://10.0.0.1/a")]
        [InlineData("intent://scan/#Intent;scheme=zxing;end")]
        [InlineData("https://photos.example.org/‮a")]
        public void A_damaged_stored_link_is_refused_before_anything_is_started(string damaged)
        {
            // A link changed after it was read (damaged settings, an edited value): checked again at Open.
            var link = new GalleryLink(damaged, "Looks fine");
            var started = 0;

            var result = GalleryLinks.Open(link, "photos.example.org", _ =>
            {
                started++;
                return true;
            });

            Assert.Equal(GalleryOpenResult.Refused, result);
            Assert.Equal(0, started);
            Assert.Equal(GalleryLinks.RefusedText, GalleryLinks.ResultText(result));
            Assert.Null(GalleryLinks.ConfirmMessage(link));
        }

        [Fact]
        public void A_link_whose_host_is_not_the_one_confirmed_is_refused()
        {
            var link = GalleryLinks.Keep(Two, null)!;
            var started = 0;

            Assert.Equal(GalleryOpenResult.Refused, GalleryLinks.Open(link, "photos.example.org", _ => ++started > 0));
            Assert.Equal(0, started);
        }

        [Fact]
        public void No_app_is_said_plainly()
        {
            var link = GalleryLinks.Keep(One, null)!;

            var result = GalleryLinks.Open(link, GalleryLinks.ConfirmMessage(link)!, _ => false);

            Assert.Equal(GalleryOpenResult.NoApp, result);
            Assert.Equal(GalleryLinks.NoAppText, GalleryLinks.ResultText(result));
        }

        [Fact]
        public void The_confirmation_shows_the_host_only()
        {
            var link = GalleryLinks.Keep("https://photos.example.org/album/secret-key-123", "Day 1")!;

            Assert.Equal("Opens outside Chatcheology", GalleryLinks.ConfirmTitle);
            Assert.Equal("photos.example.org", GalleryLinks.ConfirmMessage(link));
        }

        // ---------------------------------------------------------------------------------------------
        // Gifted links from the archive.
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public void Archive_rows_are_ordered_and_a_row_that_breaks_a_rule_is_left_out()
        {
            ArchiveExternalLink Row(long id, int ordinal, string url, string? label = null) => new()
            {
                ExternalLinkID = id, OwnerKind = ExternalLinkOwnerKind.Entry, OwnerID = 5, Ordinal = ordinal, Url = url, Label = label,
            };

            var links = GalleryLinks.FromArchive(
            [
                Row(1, 3, Two),
                Row(2, 1, One, "First"),
                Row(3, 2, "http://photos.example.org/plain"),
                Row(4, 4, "https://photos.example.org/x", "bad\u0007label"),
            ]);

            Assert.Equal(["First", "pictures.example.net"], links.Select(l => l.Text));

            var many = GalleryLinks.FromArchive(Enumerable.Range(1, 12).Select(i => Row(i, i, One + i)));
            Assert.Equal(ExternalLinkRules.MaxLinksPerOwner, many.Count);
        }

        [Fact]
        public void A_personal_memory_and_a_gifted_date_get_their_links_from_the_archive_and_nothing_is_written_there()
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(Day);
            builder.Memory(20, "Beach week", new DateTime(2025, 3, 10));
            builder.Memory(21, "Two albums", new DateTime(2025, 3, 10));
            builder.Memory(22, "No album", new DateTime(2025, 3, 10));
            builder.NotedDate(7, "Invented day", 3, 10, 2020);
            builder.NotedDate(8, "Plain day", 3, 10);
            builder.Gallery(ExternalLinkOwnerKind.Entry, 20, One);
            builder.Gallery(ExternalLinkOwnerKind.Entry, 21, One, "Day 1");
            builder.Gallery(ExternalLinkOwnerKind.Entry, 21, Two);
            builder.Gallery(ExternalLinkOwnerKind.NotedDate, 7, Two, "Album");

            var (session, folder) = Open(builder);
            var before = ArchiveContents.Build(folder);

            using (session)
            {
                var cards = Cards(session);
                Assert.Equal(["Gallery · photos.example.org", "2 galleries", string.Empty], cards.Select(c => c.GalleryText));
                Assert.Equal([true, true, false], cards.Select(c => c.HasGalleries));

                var details = session.MemoryDetails(new MemoryKey(21, null))!;
                Assert.Equal(["Day 1", "pictures.example.net"], details.Galleries.Select(g => g.Text));
                Assert.Empty(session.MemoryDetails(new MemoryKey(22, null))!.Galleries);

                var listings = session.NotedDateListings;
                Assert.Equal("Gallery · pictures.example.net", listings.Single(l => l.Id == "7").GalleryText);
                Assert.Equal(["Album"], listings.Single(l => l.Id == "7").Galleries.Select(g => g.Text));
                Assert.Empty(listings.Single(l => l.Id == "8").Galleries);

                var card = session.OnThisDayFor(session.ReadDays(), new DateOnly(2026, 3, 10));
                Assert.Equal(["Gallery · pictures.example.net", string.Empty], card.Noted.Select(n => n.GalleryText));
            }

            Assert.Equal(before, ArchiveContents.Build(folder));
        }

        [Fact]
        public void An_imported_memory_never_shows_a_link_whatever_the_source_gives()
        {
            var link = GalleryLinks.Keep(One, null)!;
            var start = new DateTime(2025, 3, 10, 12, 0, 0);

            ArchiveCollection Collection(long id, string kind) => new()
            {
                CollectionID = id,
                Kind = kind,
                Name = kind,
                SourceDescription = null,
                ShownByDefault = true,
                PeriodGapMinutes = kind == ArchiveMemoryVocabulary.KindImported ? 60 : null,
            };

            ArchiveEntry Entry(long id, long collection) => new()
            {
                EntryID = id,
                CollectionID = collection,
                SourceKey = null,
                Title = "Entry " + id,
                StartLocal = start,
                EndLocal = null,
                StartUtc = null,
                EndUtc = null,
                DatePrecision = "Day",
                Summary = null,
                ImageSha256 = null,
                ImageExtension = null,
                Source = null,
                DayLevel = null,
            };

            var index = MemoryIndex.Build(new MemorySource
            {
                Collections = [Collection(1, ArchiveMemoryVocabulary.KindImported), Collection(2, ArchiveMemoryVocabulary.KindPersonal)],
                Entries =
                [
                    new MemorySourceEntry(Entry(10, 1), [], null, []) { Galleries = [link] },
                    new MemorySourceEntry(Entry(11, 2), [], null, []) { Galleries = [link, new GalleryLink("http://photos.example.org/x", null)] },
                ],
                ParticipantNames = new Dictionary<long, string>(),
                Definitions = [],
                IconPath = _ => null,
                MessageTimes = [start.AddMinutes(-1)],
            });

            Assert.Empty(index.Details(new MemoryKey(10, null))!.Galleries);
            Assert.Equal([link], index.Details(new MemoryKey(11, null))!.Galleries);
        }

        // ---------------------------------------------------------------------------------------------
        // Own dates' links in settings.
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public void Own_date_links_are_saved_in_order_and_cleared_by_forget()
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(Day);
            var (session, _) = Open(builder);
            var today = new DateOnly(2026, 3, 10);

            using (session)
            {
                var result = session.SaveOwnNotedDate(
                    "own", "Trip", 3, 10, null, null,
                    [(Two, "Second first"), ("  ", null), (" " + One + " ", "  ")],
                    today);

                Assert.True(result.Saved);

                var own = session.NotedDateListings.Single(l => !l.IsGifted);
                Assert.Equal([Two, One], own.Galleries.Select(g => g.Url));
                Assert.Equal(["Second first", null], own.Galleries.Select(g => g.Label));
                Assert.Equal("2 galleries", own.GalleryText);

                // Reordered and one removed.
                Assert.True(session.SaveOwnNotedDate("own", "Trip", 3, 10, null, null, [(One, null), (Two, "Second first")], today).Saved);
                Assert.Equal([One, Two], session.NotedDateListings.Single(l => !l.IsGifted).Galleries.Select(g => g.Url));

                // The overload without links keeps them.
                Assert.Empty(session.SaveOwnNotedDate("own", "Trip renamed", 3, 10, null, null, today));
                Assert.Equal(2, session.NotedDateListings.Single(l => !l.IsGifted).Galleries.Count);

                Assert.True(session.SaveOwnNotedDate("own", "Trip", 3, 10, null, null, [(One, null)], today).Saved);
                Assert.Equal("Gallery · photos.example.org", session.NotedDateListings.Single(l => !l.IsGifted).GalleryText);
                Assert.Equal("Gallery · photos.example.org", session.OnThisDayFor([], today).Noted.Single().GalleryText);

                var hash = session.Archive.ContentsSha256!;
                Assert.Contains(One, _store.Values["archive." + hash + ".ownNotedDates"], StringComparison.Ordinal);

                _settings.Forget(hash);
                Assert.Empty(_settings.GetOwnNotedDates(hash));
                Assert.False(_store.Values.ContainsKey("archive." + hash + ".ownNotedDates"));
            }
        }

        [Fact]
        public void A_bad_link_shows_every_problem_and_nothing_is_stored()
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(Day);
            var (session, _) = Open(builder);
            var today = new DateOnly(2026, 3, 10);

            using (session)
            {
                var result = session.SaveOwnNotedDate(
                    "own", " ", 3, 10, null, null,
                    [(One, null), ("http://photos.example.org/x", null), (Two, new string('x', 61)), (null, "Label only")],
                    today);

                Assert.False(result.Saved);
                Assert.Equal([NotedDateField.Title], result.Errors.Keys);
                Assert.Equal([1, 2, 3], result.Links.RowErrors.Keys.Order());
                Assert.Equal(ExternalLinkRules.UrlError("http://photos.example.org/x"), result.Links.RowErrors[1]);
                Assert.Equal(ExternalLinkRules.LabelError(new string('x', 61)), result.Links.RowErrors[2]);
                Assert.Equal(ExternalLinkRules.UrlError(null), result.Links.RowErrors[3]);
                Assert.Empty(session.NotedDateListings);

                // A good date with a bad link is not stored either.
                Assert.False(session.SaveOwnNotedDate("own", "Trip", 3, 10, null, null, [("javascript:alert(1)", null)], today).Saved);
                Assert.Empty(session.NotedDateListings);

                // Eleven links: the count.
                var eleven = Enumerable.Range(1, 11).Select(i => ((string?)(One + i), (string?)null)).ToList();
                var tooMany = session.SaveOwnNotedDate("own", "Trip", 3, 10, null, null, eleven, today);
                Assert.Equal(ExternalLinkRules.CountError(11), tooMany.Links.CountError);
                Assert.Empty(session.NotedDateListings);
            }
        }

        [Fact]
        public void Damaged_link_values_in_settings_are_dropped_and_never_throw()
        {
            const string Date = "\"Id\":\"a\",\"Title\":\"Trip\",\"Month\":3,\"Day\":10";

            foreach (var links in new[]
            {
                "\"Links\":\"not a list\"",
                "\"Links\":42",
                "\"Links\":{\"Url\":\"" + One + "\"}",
                "\"Links\":[1,\"x\",null,[],{}]",
                "\"Links\":[{\"Url\":5},{\"Url\":\"http://photos.example.org/x\"},{\"Url\":\"" + One + "\",\"Label\":7}]",
                "\"Links\":[{\"Url\":\"javascript:alert(1)\"},{\"Url\":\"https://a@b.example/x\"}]",
            })
            {
                _store.Set("archive.hash.ownNotedDates", "[{" + Date + "," + links + "}]");

                var date = Assert.Single(_settings.GetOwnNotedDates("hash"));
                Assert.Equal("Trip", date.Title);
                Assert.Empty(date.Links);
            }

            // Good links survive beside bad ones, in order, up to ten.
            var mixed = string.Join(",", Enumerable.Range(1, 12).Select(i => "{\"Url\":\"" + One + i + "\",\"Label\":\"L" + i + "\"}"));
            _store.Set("archive.hash.ownNotedDates", "[{" + Date + ",\"Links\":[{\"Url\":\"http://x.example/\"}," + mixed + "]}]");

            var kept = Assert.Single(_settings.GetOwnNotedDates("hash")).Links;
            Assert.Equal(Enumerable.Range(1, 10).Select(i => "L" + i), kept.Select(l => l.Label));

            // Older values without links read as before.
            _store.Set("archive.hash.ownNotedDates", "[{" + Date + "}]");
            Assert.Empty(Assert.Single(_settings.GetOwnNotedDates("hash")).Links);
        }

        [Fact]
        public void The_synthetic_archive_with_memories_carries_gallery_links_and_validates()
        {
            using var folder = new TemporaryFolder();
            SyntheticArchive.Write(folder.Path, SyntheticDebugInstalls.WithMemories);

            var validation = ArchiveValidator.Validate(folder.Path);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

            using var reader = ArchiveReader.Open(folder.Path);
            var grouped = GalleryLinks.GroupFromArchive(reader.ReadAllExternalLinks());

            Assert.Equal("2 galleries", GalleryLinks.Summary(grouped[(ExternalLinkOwnerKind.Entry, 10)]));
            Assert.Equal("Gallery · example.org", GalleryLinks.Summary(grouped[(ExternalLinkOwnerKind.Entry, 501)]));
        }

        [Fact]
        public void Own_dates_with_links_round_trip_through_settings()
        {
            var date = new OwnNotedDate("x", "Trip", 9, 3, 2018, "Note")
            {
                Links = [GalleryLinks.Keep(One, "Day 1")!, GalleryLinks.Keep(Two, null)!],
            };

            _settings.SetOwnNotedDates("hash", [date]);

            Assert.Equal([date], _settings.GetOwnNotedDates("hash"));
            Assert.NotEqual(date, date with { Links = [date.Links[1], date.Links[0]] });
        }
    }
}
