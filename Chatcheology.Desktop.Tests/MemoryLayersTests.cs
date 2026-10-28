using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// CH-42 step 5: memory layers in the desktop conversation, from a synthetic memories file beside a
    /// synthetic workspace (five messages on 6 April 2026, 09:00 to 09:04). Invented data only.
    /// </summary>
    /// <remarks>
    /// The memories file holds a personal "Holidays" collection with one memory on 6 April (an image, a
    /// link to message 5 and one to the PSN entry 100), and an imported "Shared gaming" collection: entry
    /// 100 on 6 April (a strong period at 09:01:30, a one-account period at 11:00, a supporting period at
    /// 12:00), entry 101 on 5 April (a same-period period, before the first message) and entry 102 on
    /// 7 April (two one-account periods: a "different times" line after the last message).
    /// </remarks>
    public sealed class MemoryLayersTests : IDisposable
    {
        internal const long Gaming = 5;

        private readonly ArchiveTestWorkspace _workspace = new();
        private readonly List<ArchiveTestWorkspace> _others = [];
        private readonly string _settingsPath;
        private long _holidays;
        private long _beach;

        public MemoryLayersTests()
        {
            _settingsPath = Path.Combine(_workspace.Root, "settings", "desktop-layers.json");
        }

        public void Dispose()
        {
            foreach (var other in _others)
            {
                other.Dispose();
            }

            _workspace.Dispose();
        }

        // ---------------------------------------------------------------------------------------
        // Tests.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void EveryLayerOff_TheListIsExactlyTheMessages_AndReviewIsUnchanged() => OnDispatcher(async () =>
        {
            var plain = Track(new ArchiveTestWorkspace());
            var baseline = await OpenAsync(plain);

            AddMemories();
            var viewModel = await OpenAsync(_workspace, showAs: true);
            Assert.NotEmpty(viewModel.Layers);

            foreach (var layer in viewModel.Layers)
            {
                layer.IsShown = false;
            }

            await viewModel.LayersLoad;

            Assert.Equal(Keys(baseline), Keys(viewModel));
            Assert.All(viewModel.Rows, row => Assert.IsType<MessageViewModel>(row));
            Assert.Equal(viewModel.Messages.Cast<object>(), viewModel.Rows);
            Assert.Equal(
                baseline.Messages.Select(m => (m.SequenceNumber, m.HasDateSeparator, m.DateSeparatorText)),
                viewModel.Messages.Select(m => (m.SequenceNumber, m.HasDateSeparator, m.DateSeparatorText)));

            await baseline.OpenReviewAsync(plain.RestoredAttachmentID);
            await viewModel.OpenReviewAsync(_workspace.RestoredAttachmentID);
            Assert.True(viewModel.Review.IsOpen);
            Assert.Equal(baseline.Review.HeadlineText, viewModel.Review.HeadlineText);
            Assert.Equal(baseline.Review.Candidates.Count, viewModel.Review.Candidates.Count);

            baseline.CloseSession();
            viewModel.CloseSession();
        });

        [Fact]
        public void WithoutShowAs_ThePersonalLayerShows_AndTheImportedOneIsNotGuessed() => OnDispatcher(async () =>
        {
            AddMemories();
            var viewModel = await OpenAsync(_workspace);

            var layer = Assert.Single(viewModel.Layers);
            Assert.Equal("Holidays", layer.Name);
            Assert.True(layer.IsShown);
            Assert.False(layer.HasFilters);
            Assert.Contains("Shared gaming", viewModel.LayersNote, StringComparison.Ordinal);
            Assert.Contains("Show as", viewModel.LayersNote, StringComparison.Ordinal);

            Assert.Equal(["e" + _beach, "m1", "m2", "m3", "m4", "m5"], Keys(viewModel));

            viewModel.CloseSession();
        });

        [Fact]
        public void PsnLayerOn_RowsArePlacedBeforeAmongAndAfterTheMessages_OneAccountNotShown() => OnDispatcher(async () =>
        {
            AddMemories();
            var viewModel = await OpenAsync(_workspace, showAs: true);

            Assert.Equal(["Holidays", "Shared gaming"], viewModel.Layers.Select(l => l.Name));
            Assert.False(Layer(viewModel, Gaming).IsShown);
            Assert.Equal(MemoryFilters.All, Layer(viewModel, Gaming).Filters.Select(f => f.Filter));
            Assert.Equal(string.Empty, viewModel.LayersNote);

            await SwitchAsync(viewModel, () => Layer(viewModel, Gaming).IsShown = true);

            Assert.Equal(
                ["e101:0", "e" + _beach, "m1", "m2", "e100:0", "m3", "m4", "m5", "e100:2", "e102"],
                Keys(viewModel));

            // One-account period 1 of entry 100 is never a card.
            Assert.DoesNotContain(viewModel.Rows.OfType<MemoryRowViewModel>(), r => r.Key == new MemoryKey(100, 1));

            // A day is announced once, by whichever row begins it.
            Assert.Equal(
                [true, true, false, false, false, false, false, false, false, true],
                viewModel.Rows.Select(r => r is MessageViewModel m ? m.HasDateSeparator : ((MemoryRowViewModel)r).HasDateSeparator));

            var strong = Row(viewModel, 100, 0);
            Assert.True(strong.IsPeriodCard);
            Assert.Equal("Synthetic Game", strong.Title);
            Assert.Equal(["Ada Fictional: 1 trophy, at 09:01", "Bo Fictional: 1 trophy, at 09:01"], strong.Lines);
            Assert.Equal("This period: strong", strong.LabelText);
            Assert.Equal("PSN trophies", strong.SourceText);
            Assert.True(strong.HasImage);

            var line = Row(viewModel, 102, null);
            Assert.True(line.IsDifferentTimes);
            Assert.Equal("Both earned trophies in Another Invented Game today, at different times", line.Text);

            var card = Row(viewModel, _beach, null);
            Assert.True(card.IsEntryCard);
            Assert.Equal("Holidays", card.CollectionName);
            Assert.True(card.HasImage);

            // The messages themselves are untouched.
            Assert.Equal([1, 2, 3, 4, 5], viewModel.Messages.Select(m => m.SequenceNumber));

            viewModel.CloseSession();
        });

        [Fact]
        public void Filters_HideTheirRowsOnly() => OnDispatcher(async () =>
        {
            AddMemories();
            var viewModel = await OpenAsync(_workspace, showAs: true);
            var gaming = Layer(viewModel, Gaming);
            await SwitchAsync(viewModel, () => gaming.IsShown = true);

            await SwitchAsync(viewModel, () => Filter(gaming, MemoryFilters.Strong).IsOn = false);
            Assert.Equal(["e101:0", "e" + _beach, "m1", "m2", "m3", "m4", "m5", "e100:2", "e102"], Keys(viewModel));

            await SwitchAsync(viewModel, () => Filter(gaming, MemoryFilters.DifferentTimes).IsOn = false);
            await SwitchAsync(viewModel, () => Filter(gaming, MemoryFilters.SamePeriod).IsOn = false);
            Assert.Equal(["e" + _beach, "m1", "m2", "m3", "m4", "m5", "e100:2"], Keys(viewModel));

            await SwitchAsync(viewModel, () => Filter(gaming, MemoryFilters.Supporting).IsOn = false);
            Assert.Equal(["e" + _beach, "m1", "m2", "m3", "m4", "m5"], Keys(viewModel));

            viewModel.CloseSession();
        });

        [Fact]
        public void Choices_AreRememberedPerWorkspace_WithoutAPath() => OnDispatcher(async () =>
        {
            AddMemories();
            var first = await OpenAsync(_workspace, showAs: true);
            await SwitchAsync(first, () => Layer(first, Gaming).IsShown = true);
            await SwitchAsync(first, () => Filter(Layer(first, Gaming), MemoryFilters.Strong).IsOn = false);
            await SwitchAsync(first, () => Layer(first, _holidays).IsShown = false);
            first.CloseSession();

            var again = await OpenAsync(_workspace, showAs: true);
            Assert.True(Layer(again, Gaming).IsShown);
            Assert.False(Filter(Layer(again, Gaming), MemoryFilters.Strong).IsOn);
            Assert.True(Filter(Layer(again, Gaming), MemoryFilters.Supporting).IsOn);
            Assert.False(Layer(again, _holidays).IsShown);
            Assert.DoesNotContain("e100:0", Keys(again));
            Assert.Contains("e100:2", Keys(again));
            again.CloseSession();

            // Another workspace with the same memories starts at the defaults.
            var other = Track(new ArchiveTestWorkspace());
            AddMemories(other);
            var elsewhere = await OpenAsync(other, showAs: true);
            Assert.False(Layer(elsewhere, Gaming).IsShown);
            Assert.True(Layer(elsewhere, _holidays).IsShown);
            elsewhere.CloseSession();

            var text = File.ReadAllText(_settingsPath);
            Assert.DoesNotContain(_workspace.Root, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workspace.db", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Shared gaming", text, StringComparison.Ordinal);
            Assert.Contains(LayerSettings.KeyFor(_workspace.DatabasePath), text, StringComparison.Ordinal);

            // Nothing was written beside the workspace by remembering.
            Assert.False(File.Exists(Path.Combine(_workspace.Root, "desktop-layers.json")));
        });

        [Fact]
        public void PsnDetails_ShowThePeriodsTrophies_DayEvidence_AndOtherPeriods_WithoutTouchingReview() => OnDispatcher(async () =>
        {
            AddMemories();
            var viewModel = await OpenAsync(_workspace, showAs: true);
            await SwitchAsync(viewModel, () => Layer(viewModel, Gaming).IsShown = true);

            await viewModel.OpenReviewAsync(_workspace.MissingAttachmentID);
            var reviewed = viewModel.Review.AttachmentID;
            var messages = viewModel.Messages.ToList();

            var strong = Row(viewModel, 100, 0);
            viewModel.SelectMemoryRow(strong);

            Assert.True(strong.IsSelected);
            Assert.True(viewModel.Review.IsOpen);
            Assert.Equal(reviewed, viewModel.Review.AttachmentID);
            Assert.Equal(messages, viewModel.Messages);

            var details = viewModel.MemoryDetails!;
            Assert.False(details.IsPersonal);
            Assert.False(viewModel.EditMemoryCommand.CanExecute(null));
            Assert.Equal("Shared gaming", details.CollectionName);
            Assert.Equal(["Ada Fictional: 1 trophy, at 09:01", "Bo Fictional: 1 trophy, at 09:01", "This period: strong"], details.Lines);

            Assert.Equal("Trophies", details.ItemsHeading);
            Assert.Equal(2, details.Items.Count);
            Assert.Equal("Invented Opening Move", details.Items[0].Name);
            Assert.Equal("09:01:30 · Ada Fictional · hidden trophy", details.Items[0].Meta);
            Assert.Equal("Open the invented door.", details.Items[0].Detail);
            Assert.True(details.Items[0].HasIcon);
            Assert.Equal("Bronze trophy #2", details.Items[1].Name);
            Assert.Equal("09:01:50 · Bo Fictional", details.Items[1].Meta);
            Assert.False(details.Items[1].HasIcon);

            Assert.Equal("Day evidence: strong", details.DayEvidenceText);
            Assert.Equal("Other periods this day:", details.OtherPeriodsHeading);
            Assert.Equal(2, details.OtherPeriods.Count);
            Assert.Contains("one account", details.OtherPeriods[0], StringComparison.Ordinal);
            Assert.Contains("supporting", details.OtherPeriods[1], StringComparison.Ordinal);
            Assert.Equal("Source: PSN trophies", details.SourceLine);
            Assert.Empty(details.Links);

            // The "different times" line: every trophy of its record and its periods.
            viewModel.SelectMemoryRow(Row(viewModel, 102, null));
            Assert.False(strong.IsSelected);
            Assert.Equal("Periods this day:", viewModel.MemoryDetails!.OtherPeriodsHeading);
            Assert.Equal(2, viewModel.MemoryDetails.Items.Count);
            Assert.Equal("Day evidence: same-day", viewModel.MemoryDetails.DayEvidenceText);

            viewModel.CloseMemoryDetailsCommand.Execute(null);
            Assert.Null(viewModel.MemoryDetails);
            Assert.True(viewModel.Review.IsOpen);

            viewModel.CloseSession();
        });

        [Fact]
        public void WithoutDefinitions_TheFallbackNameShows() => OnDispatcher(async () =>
        {
            AddMemories(definitions: false);
            var viewModel = await OpenAsync(_workspace, showAs: true);
            await SwitchAsync(viewModel, () => Layer(viewModel, Gaming).IsShown = true);

            viewModel.SelectMemoryRow(Row(viewModel, 100, 0));

            Assert.Equal("Bronze trophy #1", viewModel.MemoryDetails!.Items[0].Name);
            Assert.Equal("09:01:30 · Ada Fictional", viewModel.MemoryDetails.Items[0].Meta);
            Assert.False(viewModel.MemoryDetails.Items[0].HasIcon);

            viewModel.CloseSession();
        });

        [Fact]
        public void PersonalDetails_ShowImageDatesSummaryAndLinks_AndLinksJump() => OnDispatcher(async () =>
        {
            AddMemories();
            var viewModel = await OpenAsync(_workspace, showAs: true);
            await SwitchAsync(viewModel, () => Layer(viewModel, Gaming).IsShown = true);

            viewModel.SelectMemoryRow(Row(viewModel, _beach, null));

            var details = viewModel.MemoryDetails!;
            Assert.True(details.IsPersonal);
            Assert.True(viewModel.EditMemoryCommand.CanExecute(null));
            Assert.Equal("Beach week", details.Title);
            Assert.Equal(
                DateSeparatorRow.DayText(new DateOnly(2026, 4, 6)) + " to " + DateSeparatorRow.DayText(new DateOnly(2026, 4, 12)),
                details.DateText);
            Assert.Equal("Invented days by the sea.", details.Summary);
            Assert.True(details.HasImage);
            Assert.Equal("Source: My own memory", details.SourceLine);
            Assert.Empty(details.Items);
            Assert.Equal(string.Empty, details.DayEvidenceText);
            Assert.Equal(2, details.Links.Count);

            var message = details.Links.Single(l => l.SequenceNumber is not null);
            Assert.Equal("A message on " + DateSeparatorRow.DayText(new DateOnly(2026, 4, 6)) + ", 09:04", message.Text);

            await viewModel.FollowMemoryLinkAsync(message);
            Assert.Equal(5, viewModel.Messages[0].SequenceNumber);
            Assert.Equal(["m5", "e100:2", "e102"], Keys(viewModel));

            // An entry link opens that memory's row (on its page) and its details.
            var entry = details.Links.Single(l => l.EntryID is not null);
            await viewModel.FollowMemoryLinkAsync(entry);
            Assert.Contains("e100:0", Keys(viewModel));
            Assert.Equal(new MemoryKey(100, 0), viewModel.MemoryDetails!.Key);
            Assert.True(Row(viewModel, 100, 0).IsSelected);

            // With its layer off, the link says so and moves nothing.
            await SwitchAsync(viewModel, () => Layer(viewModel, Gaming).IsShown = false);
            var before = Keys(viewModel);
            await viewModel.FollowMemoryLinkAsync(entry);
            Assert.Equal(before, Keys(viewModel));
            Assert.Equal("The linked memory is in a layer that is switched off.", viewModel.StatusText);

            viewModel.CloseSession();
        });

        [Fact]
        public void Edit_OpensTheForm_AndASave_RefreshesTheListWithoutReopening() => OnDispatcher(async () =>
        {
            AddMemories();
            var viewModel = await OpenAsync(_workspace, showAs: true);

            viewModel.SelectMemoryRow(Row(viewModel, _beach, null));
            viewModel.EditMemoryCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Memories.Form is not null);

            var form = viewModel.Memories.Form!;
            Assert.True(viewModel.Memories.IsOpen);
            Assert.Equal("Beach week", form.Title);

            form.Title = "Beach week, renamed";
            await MemoriesViewModelTests.Run(form.SaveCommand, form);
            await viewModel.Memories.AfterSave;
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Rows.OfType<MemoryRowViewModel>().Any(r => r.Title == "Beach week, renamed"));

            // The open details follow the file.
            Assert.Equal("Beach week, renamed", viewModel.MemoryDetails!.Title);

            // A new memory in the same collection appears at its date.
            var added = await MemoriesViewModelTests.SaveAsync(viewModel.Memories, "Invented picnic", new DateTime(2026, 4, 7));
            await MemoriesViewModelTests.WaitUntil(() => Keys(viewModel).Contains("e" + added));
            Assert.Equal("e" + added, Keys(viewModel)[^1]);

            viewModel.CloseSession();
        });

        [Fact]
        public void Edit_WaitsWhileTheMemoriesAreaIsBusy() => OnDispatcher(async () =>
        {
            AddMemories();
            var viewModel = await OpenAsync(_workspace);

            viewModel.SelectMemoryRow(Row(viewModel, _beach, null));
            Assert.True(viewModel.EditMemoryCommand.CanExecute(null));

            // Opening the area reads the file; Edit is not offered meanwhile.
            viewModel.Memories.OpenCommand.Execute(null);
            Assert.True(viewModel.Memories.IsBusy);
            Assert.False(viewModel.EditMemoryCommand.CanExecute(null));

            await MemoriesViewModelTests.WaitUntil(() => !viewModel.Memories.IsBusy);
            Assert.True(viewModel.EditMemoryCommand.CanExecute(null));

            viewModel.CloseSession();
        });

        [Fact]
        public void Paging_RowsAtAPagesFirstAndLastMessage_StayOnThatPage() => OnDispatcher(async () =>
        {
            // 320 messages, one a minute from 1 January 2026 00:00: pages 1-150, 151-300, 301-320.
            using var workspace = new LongTestWorkspace(320);
            SeedPeriods(workspace, [
                (200, new DateTime(2026, 1, 1, 2, 29, 30)),   // between 150 (02:29) and 151 (02:30): after 150, last of page 1
                (201, new DateTime(2026, 1, 1, 2, 30, 0)),    // the second of 151, the first of page 2: after 151
                (202, new DateTime(2026, 1, 1, 4, 59, 0)),    // the second of 300, the last of page 2: after 300
                (203, new DateTime(2025, 12, 31, 23, 0, 0)),  // before the first message: page 1 only
                (204, new DateTime(2026, 1, 2, 0, 0, 0)),     // after the last message (320, 05:19): page 3 only
            ]);

            var viewModel = new MainViewModel(
                () => workspace.DatabasePath,
                ArchiveDialogs.None,
                new ArchiveOperations(),
                MemoriesDialogs.None,
                null,
                new LayerSettings(_settingsPath));

            viewModel.OpenWorkspaceCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Messages.Count == 150 && !viewModel.IsBusy);
            await viewModel.LayersLoad;
            viewModel.LocalParticipant = viewModel.Participants[0];
            await viewModel.LayersLoad;
            await SwitchAsync(viewModel, () => Layer(viewModel, Gaming).IsShown = true);

            var page = Keys(viewModel);
            Assert.Equal(["e203:0", "m1", "m2"], page.Take(3));
            Assert.Equal(["m149", "m150", "e200:0"], page.TakeLast(3));
            Assert.Equal(152, page.Count);

            viewModel.NextCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Messages[0].SequenceNumber == 151 && !viewModel.IsBusy);

            page = Keys(viewModel);
            Assert.Equal(["m151", "e201:0", "m152"], page.Take(3));
            Assert.Equal(["m299", "m300", "e202:0"], page.TakeLast(3));
            Assert.Equal(152, page.Count);

            viewModel.NextCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Messages[0].SequenceNumber == 301 && !viewModel.IsBusy);

            page = Keys(viewModel);
            Assert.Equal("m301", page[0]);
            Assert.Equal(["m320", "e204:0"], page.TakeLast(2));
            Assert.Equal(21, page.Count);

            // Back again: page 2 is rebuilt the same.
            viewModel.PreviousCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Messages[0].SequenceNumber == 151 && !viewModel.IsBusy);
            Assert.Equal(["m151", "e201:0"], Keys(viewModel).Take(2));

            viewModel.CloseSession();
        });

        /// <summary>A Shared gaming collection whose entries each hold one strong period at the given local time.</summary>
        private static void SeedPeriods(LongTestWorkspace workspace, IReadOnlyList<(long EntryID, DateTime Local)> entries)
        {
            var path = MemoriesDatabase.PathBeside(workspace.DatabasePath);
            MemoriesDatabase.Create(path, new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc));

            using (var connection = MemoriesDatabase.OpenConnection(path))
            using (var command = connection.CreateCommand())
            {
                var sql = new System.Text.StringBuilder(
                    $"INSERT INTO Collection VALUES ({Gaming}, 'Imported', 'Shared gaming', 'Synthetic shared activity', 0, 60);\n");
                var item = 1;

                foreach (var (id, local) in entries)
                {
                    var l = local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                    var u = local.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";
                    var key = id.ToString(CultureInfo.InvariantCulture);

                    sql.Append(CultureInfo.InvariantCulture,
                        $"""
                        INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                           DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                        VALUES ({key}, {Gaming}, 'invented|{key}', 'Invented Game {key}', '{l}', '{l}', '{u}', '{u}', 'Second',
                                NULL, NULL, NULL, 'PSN trophies', 'strong');
                        INSERT INTO EntryPeriod VALUES ({key}, 0, '{u}', '{u}', '{l}', '{l}', 'strong', 1, 1);
                        INSERT INTO EntryItem (EntryItemID, EntryID, PeriodIndex, Role, SetId, ItemId, ItemType, TimeUtc, TimeLocal, Name, IconSha256)
                        VALUES ({item}, {key}, 0, 'Local', 'setA', '{item}', 'bronze', '{u}', '{l}', NULL, NULL),
                               ({item + 1}, {key}, 0, 'Other', 'setB', '{item + 1}', 'bronze', '{u}', '{l}', NULL, NULL);

                        """);
                    item += 2;
                }

                command.CommandText = sql.ToString();
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
        }

        /// <summary>A synthetic workspace of <c>count</c> invented messages, one a minute, two fictional participants.</summary>
        private sealed class LongTestWorkspace : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "Chatcheology.Desktop.Tests", Guid.NewGuid().ToString("N"));

            internal LongTestWorkspace(int count)
            {
                Directory.CreateDirectory(_root);
                DatabasePath = Path.Combine(_root, "workspace.db");
                Chatcheology.Data.Workspace.WorkspaceDatabase.Initialise(DatabasePath);

                var sql = new System.Text.StringBuilder(
                    """
                    INSERT INTO ImportSource (SourceType, DisplayName, ImportedDateTimeUtc)
                    VALUES ('SyntheticExport', 'Synthetic export', '2026-04-01T00:00:00.0000000Z');
                    INSERT INTO Conversation (Title, CreatedDateTimeUtc) VALUES ('Ada and Bo', '2026-04-01T00:00:00.0000000Z');
                    INSERT INTO Participant (DisplayName) VALUES ('Ada Fictional');
                    INSERT INTO Participant (DisplayName) VALUES ('Bo Fictional');
                    INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 1);
                    INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 2);

                    """);

                var start = new DateTime(2026, 1, 1);

                for (var i = 1; i <= count; i++)
                {
                    var time = start.AddMinutes(i - 1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                    var n = i.ToString(CultureInfo.InvariantCulture);

                    sql.Append(CultureInfo.InvariantCulture,
                        $"""
                        INSERT INTO Message (ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                            SenderParticipantID, MessageType, MessageContent, RawContent, SourceLineStart, SourceLineEnd)
                        VALUES (1, 1, {n}, '{time}', {(i % 2) + 1}, 'User', 'Invented line {n}', 'raw', {n}, {n});

                        """);
                }

                using (var connection = Chatcheology.Data.Workspace.WorkspaceDatabase.OpenConnection(DatabasePath))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql.ToString();
                    command.ExecuteNonQuery();
                }

                SqliteConnection.ClearAllPools();
            }

            internal string DatabasePath { get; }

            public void Dispose()
            {
                SqliteConnection.ClearAllPools();

                try
                {
                    Directory.Delete(_root, recursive: true);
                }
                catch (IOException)
                {
                    // A leaked handle shows up as a failure elsewhere.
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // The synthetic memories file.
        // ---------------------------------------------------------------------------------------

        private void AddMemories(bool definitions = true) => AddMemories(_workspace, definitions);

        private void AddMemories(ArchiveTestWorkspace workspace, bool definitions = true) =>
            (_holidays, _beach) = Seed(workspace, definitions);

        /// <summary>Writes the synthetic memories file beside <paramref name="workspace"/>; returns the personal collection and memory.</summary>
        internal static (long Holidays, long Beach) Seed(ArchiveTestWorkspace workspace, bool definitions = true, bool photos = false)
        {
            var path = MemoriesDatabase.PathBeside(workspace.DatabasePath);
            var now = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
            var messages = new WorkspaceMessageLinkTargets(workspace.DatabasePath, 1);
            var store = PersonalMemoryStore.Beside(workspace.DatabasePath, messages);

            var holidays = store.CreateCollection("Holidays", true, now);

            var game = Png(2);
            var sha = Convert.ToHexStringLower(SHA256.HashData(game));
            var media = MemoriesDatabase.MediaFolderFor(path);
            Directory.CreateDirectory(media);
            File.WriteAllBytes(Path.Combine(media, sha + ".png"), game);

            var size = game.Length.ToString(CultureInfo.InvariantCulture);

            using (var connection = MemoriesDatabase.OpenConnection(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    $"""
                    INSERT INTO MediaFile (Sha256, Extension, SizeBytes) VALUES ('{sha}', 'png', {size});
                    INSERT INTO Collection VALUES ({Gaming}, 'Imported', 'Shared gaming', 'Synthetic shared activity', 0, 60);

                    INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                       DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                    VALUES (100, {Gaming}, 'setA|setB|2026-04-06', 'Synthetic Game', '2026-04-06T09:01:30', '2026-04-06T12:00:40',
                            '2026-04-06T07:01:30Z', '2026-04-06T10:00:40Z', 'Second', NULL, '{sha}', NULL, 'PSN trophies', 'strong'),
                           (101, {Gaming}, 'setA|setB|2026-04-05', 'Synthetic Game', '2026-04-05T20:00:00', '2026-04-05T20:30:00',
                            '2026-04-05T18:00:00Z', '2026-04-05T18:30:00Z', 'Second', NULL, '{sha}', NULL, 'PSN trophies', 'same-day'),
                           (102, {Gaming}, 'setC|setD|2026-04-07', 'Another Invented Game', '2026-04-07T08:00:00', '2026-04-07T10:00:00',
                            '2026-04-07T06:00:00Z', '2026-04-07T08:00:00Z', 'Second', NULL, NULL, NULL, 'PSN trophies', 'same-day');

                    INSERT INTO EntryPeriod VALUES
                        (100, 0, '2026-04-06T07:01:30Z', '2026-04-06T07:01:50Z', '2026-04-06T09:01:30', '2026-04-06T09:01:50', 'strong', 1, 1),
                        (100, 1, '2026-04-06T09:00:00Z', '2026-04-06T09:00:00Z', '2026-04-06T11:00:00', '2026-04-06T11:00:00', 'one-account', 1, 0),
                        (100, 2, '2026-04-06T10:00:00Z', '2026-04-06T10:00:40Z', '2026-04-06T12:00:00', '2026-04-06T12:00:40', 'supporting', 1, 1),
                        (101, 0, '2026-04-05T18:00:00Z', '2026-04-05T18:30:00Z', '2026-04-05T20:00:00', '2026-04-05T20:30:00', 'same-period', 1, 1),
                        (102, 0, '2026-04-07T06:00:00Z', '2026-04-07T06:00:00Z', '2026-04-07T08:00:00', '2026-04-07T08:00:00', 'one-account', 1, 0),
                        (102, 1, '2026-04-07T08:00:00Z', '2026-04-07T08:00:00Z', '2026-04-07T10:00:00', '2026-04-07T10:00:00', 'one-account', 0, 1);

                    INSERT INTO EntryItem (EntryItemID, EntryID, PeriodIndex, Role, SetId, ItemId, ItemType, TimeUtc, TimeLocal, Name, IconSha256)
                    VALUES (1, 100, 0, 'Local', 'setA', '1', 'bronze', '2026-04-06T07:01:30Z', '2026-04-06T09:01:30', NULL, NULL),
                           (2, 100, 0, 'Other', 'setB', '2', 'bronze', '2026-04-06T07:01:50Z', '2026-04-06T09:01:50', NULL, NULL),
                           (3, 100, 1, 'Local', 'setA', '3', 'silver', '2026-04-06T09:00:00Z', '2026-04-06T11:00:00', NULL, NULL),
                           (4, 100, 2, 'Local', 'setA', '4', 'gold', '2026-04-06T10:00:00Z', '2026-04-06T12:00:00', NULL, NULL),
                           (5, 100, 2, 'Other', 'setB', '5', 'bronze', '2026-04-06T10:00:40Z', '2026-04-06T12:00:40', NULL, NULL),
                           (6, 101, 0, 'Local', 'setA', '6', 'bronze', '2026-04-05T18:00:00Z', '2026-04-05T20:00:00', NULL, NULL),
                           (7, 101, 0, 'Other', 'setB', '7', 'bronze', '2026-04-05T18:30:00Z', '2026-04-05T20:30:00', NULL, NULL),
                           (8, 102, 0, 'Local', 'setC', '1', 'bronze', '2026-04-07T06:00:00Z', '2026-04-07T08:00:00', NULL, NULL),
                           (9, 102, 1, 'Other', 'setD', '1', 'bronze', '2026-04-07T08:00:00Z', '2026-04-07T10:00:00', NULL, NULL);
                    """;

                if (definitions)
                {
                    command.CommandText +=
                        $"""

                        INSERT INTO ItemDefinition (SetId, ItemId, ItemType, Name, Detail, Hidden, GroupId, IconSourceUrl, IconSha256)
                        VALUES ('setA', '1', 'bronze', 'Invented Opening Move', 'Open the invented door.', 1, 'default', NULL, '{sha}');
                        """;
                }

                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            var picture = Path.Combine(workspace.Root, "chosen", "beach.png");
            Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
            File.WriteAllBytes(picture, MemoriesViewModelTests.Png);

            var beach = store.AddEntry(new PersonalEntryDraft
            {
                CollectionID = holidays,
                Title = "Beach week",
                Start = new DateOnly(2026, 4, 6),
                End = new DateOnly(2026, 4, 12),
                Precision = PersonalDatePrecision.Day,
                Summary = "Invented days by the sea.",
                Source = "My own memory",
                Image = PersonalImageChoice.FromFile(picture),
                Links = [PersonalLinkTarget.ToMessage(5), PersonalLinkTarget.ToEntry(100)],
                ExtraPhotos = photos
                    ? [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Chosen(workspace, "dunes.png", Png(3))), "Invented dunes"),
                       new PersonalPhotoDraft(PersonalImageChoice.FromFile(Chosen(workspace, "pier.png", Png(4))), null)]
                    : [],
            });

            SqliteConnection.ClearAllPools();

            return (holidays, beach);
        }

        private static string Chosen(ArchiveTestWorkspace workspace, string name, byte[] bytes)
        {
            var path = Path.Combine(workspace.Root, "chosen", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);

            return path;
        }

        // ---------------------------------------------------------------------------------------
        // Several photos (CH-46).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void APersonalCard_ShowsAPlusNBadge_AndItsDetails_AStripWithCaptions() => OnDispatcher(async () =>
        {
            (_holidays, _beach) = Seed(_workspace, photos: true);
            var viewModel = await OpenAsync(_workspace, showAs: true);

            var row = Row(viewModel, _beach, null);
            Assert.True(row.HasImage);
            Assert.True(row.HasExtraPhotos);
            Assert.Equal("+2", row.ExtraPhotosBadge);

            viewModel.SelectMemoryRow(row);
            var details = viewModel.MemoryDetails!;

            Assert.True(details.HasPhotoStrip);
            Assert.False(details.ShowsSingleImage);
            Assert.Equal([0, 1, 2], details.Photos.Select(p => p.Index));
            Assert.Equal(["", "Invented dunes", ""], details.Photos.Select(p => p.Caption));
            Assert.All(details.Photos, p => Assert.NotNull(p.Thumbnail));

            viewModel.CloseSession();
        });

        [Fact]
        public void TheLargePhotoView_MovesThroughThisMemorysPhotosOnly_AndClosesWithItsDetails() => OnDispatcher(async () =>
        {
            (_holidays, _beach) = Seed(_workspace, photos: true);
            var viewModel = await OpenAsync(_workspace, showAs: true);
            await SwitchAsync(viewModel, () => Layer(viewModel, Gaming).IsShown = true);

            viewModel.SelectMemoryRow(Row(viewModel, _beach, null));
            viewModel.OpenMemoryPhoto(1);

            var viewer = viewModel.MemoryPhotoViewer!;
            Assert.True(viewModel.IsMemoryPhotoViewerOpen);
            Assert.Equal("Photo 2 of 3", viewer.Title);
            Assert.Equal("Invented dunes", viewer.Caption);
            Assert.True(viewer.HasImage);

            viewer.NextCommand.Execute(null);
            Assert.Equal("Photo 3 of 3", viewer.Title);
            Assert.False(viewer.HasCaption);
            Assert.False(viewer.NextCommand.CanExecute(null));

            viewer.PreviousCommand.Execute(null);
            viewer.PreviousCommand.Execute(null);
            Assert.Equal("Photo 1 of 3", viewer.Title);
            Assert.False(viewer.PreviousCommand.CanExecute(null));

            // Only this memory's photos: the game image of entry 100 is not among them.
            var gameImage = viewModel.MemoryLayers.Index.Details(new MemoryKey(100, 0))!.ImagePath;
            Assert.DoesNotContain(gameImage, viewer.Browser.Photos.Select(p => p.Path));
            Assert.Equal(3, viewer.Browser.Photos.Count);

            viewer.CloseCommand.Execute(null);
            Assert.False(viewModel.IsMemoryPhotoViewerOpen);

            // Another row's details close it too.
            viewModel.OpenMemoryPhoto(0);
            Assert.True(viewModel.IsMemoryPhotoViewerOpen);
            viewModel.SelectMemoryRow(Row(viewModel, 100, 0));
            Assert.False(viewModel.IsMemoryPhotoViewerOpen);

            // A PSN period card has one image at most: no strip, and a click shows that one.
            Assert.False(viewModel.MemoryDetails!.HasPhotoStrip);
            viewModel.OpenMemoryPhoto(5);
            Assert.False(viewModel.IsMemoryPhotoViewerOpen);

            viewModel.CloseSession();
        });

        [Fact]
        public void ADeletedCollectionsLayerChoice_IsForgotten() => OnDispatcher(async () =>
        {
            AddMemories();
            var asked = new List<string>();
            var settings = new LayerSettings(_settingsPath);
            var viewModel = new MainViewModel(
                () => _workspace.DatabasePath,
                ArchiveDialogs.None,
                new ArchiveOperations(),
                new MemoriesDialogs
                {
                    ChooseImage = () => null,
                    ConfirmCreateFile = _ => false,
                    ConfirmDelete = _ => false,
                    ConfirmDeleteCollection = text =>
                    {
                        asked.Add(text);
                        return true;
                    },
                },
                null,
                settings);

            viewModel.OpenWorkspaceCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Messages.Count == 5 && !viewModel.IsBusy);
            await viewModel.LayersLoad;
            viewModel.LocalParticipant = viewModel.Participants[0];
            await viewModel.LayersLoad;

            await SwitchAsync(viewModel, () => Layer(viewModel, _holidays).IsShown = false);
            Assert.False(settings.Read(_workspace.DatabasePath).Layers[_holidays]);

            await MemoriesViewModelTests.Run(viewModel.Memories.OpenCommand, viewModel.Memories);
            viewModel.Memories.SelectedCollection = viewModel.Memories.Collections.Single(c => c.CollectionID == _holidays);
            await MemoriesViewModelTests.Run(viewModel.Memories.DeleteCollectionCommand, viewModel.Memories);

            Assert.Contains("the 1 memory in it", Assert.Single(asked), StringComparison.Ordinal);
            Assert.False(settings.Read(_workspace.DatabasePath).Layers.ContainsKey(_holidays));

            // The layers are read again: the deleted collection's layer is gone.
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Layers.All(l => l.CollectionID != _holidays));
            await viewModel.LayersLoad;
            Assert.DoesNotContain(viewModel.Rows.OfType<MemoryRowViewModel>(), r => r.Key.EntryID == _beach);

            viewModel.CloseSession();
        });

        /// <summary>A small invented PNG, distinct from <see cref="MemoriesViewModelTests.Png"/>.</summary>

        internal static byte[] Png(int size)
        {
            var pixels = Enumerable.Repeat((byte)0x7F, size * size * 4).ToArray();
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = new MemoryStream();
            encoder.Save(stream);

            return stream.ToArray();
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private ArchiveTestWorkspace Track(ArchiveTestWorkspace workspace)
        {
            _others.Add(workspace);
            return workspace;
        }

        private async Task<MainViewModel> OpenAsync(ArchiveTestWorkspace workspace, bool showAs = false)
        {
            var viewModel = new MainViewModel(
                () => workspace.DatabasePath,
                ArchiveDialogs.None,
                new ArchiveOperations(),
                MemoriesDialogs.None,
                null,
                new LayerSettings(_settingsPath));

            viewModel.OpenWorkspaceCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Messages.Count == 5 && !viewModel.IsBusy);
            await viewModel.LayersLoad;
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Rows.Count >= 5);

            if (showAs)
            {
                viewModel.LocalParticipant = viewModel.Participants[0];
                await viewModel.LayersLoad;
            }

            return viewModel;
        }

        private static async Task SwitchAsync(MainViewModel viewModel, Action change)
        {
            change();
            await viewModel.LayersLoad;
        }

        private static MemoryLayerViewModel Layer(MainViewModel viewModel, long collectionID) =>
            viewModel.Layers.Single(l => l.CollectionID == collectionID);

        private static MemoryFilterViewModel Filter(MemoryLayerViewModel layer, string filter) =>
            layer.Filters.Single(f => f.Filter == filter);

        private static MemoryRowViewModel Row(MainViewModel viewModel, long entryID, int? period) =>
            viewModel.Rows.OfType<MemoryRowViewModel>().Single(r => r.Key == new MemoryKey(entryID, period));

        /// <summary>The list as words: "m3" for message 3, "e100:0" for a period card, "e20" for an entry row.</summary>
        internal static List<string> Keys(MainViewModel viewModel) =>
            viewModel.Rows
                .Select(row => row switch
                {
                    MessageViewModel message => "m" + message.SequenceNumber.ToString(CultureInfo.InvariantCulture),
                    MemoryRowViewModel memory => "e" + memory.Key.EntryID.ToString(CultureInfo.InvariantCulture)
                        + (memory.Key.PeriodIndex is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : string.Empty),
                    _ => "?",
                })
                .ToList();

        /// <summary>
        /// Runs an asynchronous test on one dispatcher thread, as the window does, so continuations never
        /// race each other on the bound collections.
        /// </summary>
        internal static void OnDispatcher(Func<Task> body) => StaRunner.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var task = body();
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);

            task.GetAwaiter().GetResult();
        });
    }
}
