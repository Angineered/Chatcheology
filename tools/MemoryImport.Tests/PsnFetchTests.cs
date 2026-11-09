using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Chatcheology.Data.Memories.Psn;

namespace Chatcheology.Tools.MemoryImport.Tests
{
    /// <summary>psn-fetch (CH-71 PSN pass) against a fake PlayStation API. Every value is invented; no network.</summary>
    public sealed class PsnFetchTests : IDisposable
    {
        private const string AccountA = "alpha";
        private const string AccountB = "beta";
        private const string Partner = "98765432101";
        private const string Token = "zzTokenOneSecretValue123";
        private const string NewToken = "zzTokenTwoSecretValue456";

        private const string Quest = "ZZSET00001_00";
        private const string RacerA = "ZZSET00002_00";
        private const string RacerB = "ZZSET00003_00";
        private const string Border = "ZZSET00004_00";
        private const string TwinA = "ZZSET00005_00";
        private const string TwinB = "ZZSET00006_00";
        private const string Solo = "ZZSET00007_00";

        private const string QuestListIcon = "https://image.api.playstation.com/zz/quest-list.png";
        private const string QuestTitleIcon = "https://image.api.playstation.com/zz/quest-title.png";
        private const string RacerTitleIcon = "https://image.api.playstation.com/zz/racer-title.png";
        private const string RacerCurrentIcon = "https://image.api.playstation.com/zz/racer-current.png";
        private const string BorderTitleIcon = "https://image.api.playstation.com/zz/border-title.png";

        private static readonly DateTime Now = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);

        private readonly string _root;
        private readonly List<TimeSpan> _sleeps = [];

        public PsnFetchTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "Chatcheology.MemoryImport.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Server = Standard();
            PreviousList = WriteList(
                Listed(Quest, Quest, "Invented Quest", QuestListIcon),
                Listed(RacerA, RacerB, "Invented Racer", null));
            Definitions = WriteDefinitions(Quest, RacerA, RacerB);
            Decisions = WriteDecisions(Decision("Invented Border", PsnDecisionsFile.Together, Border, Border));
        }

        private FakePsnServer Server { get; set; }

        private string PreviousList { get; set; }

        private string Definitions { get; }

        private string Decisions { get; set; }

        private string Out => Path.Combine(_root, "out");

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder must not fail a test.
            }
        }

        private static DateTime Utc(int day, int hour, int minute, int second = 0) =>
            new(2015, 3, day, hour, minute, second, DateTimeKind.Utc);

        /// <summary>
        /// Account a (local, "me"): Quest, Racer (its own set), Border, Twin (its own set), Solo. Account b:
        /// Quest, Racer (its own set), Border, Twin (another set, same title once normalised).
        /// </summary>
        private static FakePsnServer Standard()
        {
            var server = new FakePsnServer { Token = Token };
            server.Titles["me"] =
            [
                new FakeTitle(Quest, "Invented Quest", QuestTitleIcon),
                new FakeTitle(RacerA, "Invented Racer", RacerTitleIcon, "trophy2"),
                new FakeTitle(Border, "Invented Border", BorderTitleIcon),
                new FakeTitle(TwinA, "Invented Twin\u2122", null, "trophy2"),
                new FakeTitle(Solo, "Invented Solo"),
            ];
            server.Titles[Partner] =
            [
                new FakeTitle(Quest, "Invented Quest", QuestTitleIcon),
                new FakeTitle(RacerB, "Invented Racer", null),
                new FakeTitle(Border, "Invented Border", BorderTitleIcon),
                new FakeTitle(TwinB, "invented   TWIN"),
            ];

            foreach (var set in new[] { Quest, RacerA, RacerB, Border, TwinA, TwinB, Solo })
            {
                server.Sets[set] = [new(1), new(2), new(3, "gold"), new(4), new(5, "silver")];
            }

            server.AddEarned("me", Quest, 1, Utc(1, 10, 0));
            server.AddEarned("me", Quest, 2, Utc(1, 11, 0));
            server.AddEarned(Partner, Quest, 1, Utc(1, 10, 0, 30));
            server.AddEarned("me", RacerA, 5, Utc(2, 9, 0));
            server.AddEarned(Partner, RacerB, 5, Utc(2, 9, 5));
            server.AddEarned("me", Border, 3, Utc(4, 20, 0));
            server.AddEarned(Partner, Border, 3, Utc(4, 20, 0, 10));
            server.AddEarned(Partner, Border, 4, Utc(5, 20, 0));
            return server;
        }

        private static JsonObject Listed(string setA, string setB, string title, string? icon)
        {
            var game = new JsonObject
            {
                ["game"] = title,
                ["key"] = $"{setA}|{setB}",
                ["sameTrophySet"] = setA == setB,
                [AccountA] = new JsonObject { ["npCommunicationId"] = setA, ["npServiceName"] = setA == RacerA ? "trophy2" : "trophy", ["expectedEarnedCount"] = 1 },
                [AccountB] = new JsonObject { ["npCommunicationId"] = setB, ["npServiceName"] = "trophy", ["expectedEarnedCount"] = 1 },
            };

            if (icon is not null)
            {
                game["icon"] = icon;
            }

            return game;
        }

        private string WriteList(params JsonObject[] games)
        {
            var path = Path.Combine(_root, "zzlist-previous.json");
            File.WriteAllText(path, new JsonObject
            {
                ["description"] = "invented",
                ["timezone"] = "Africa/Johannesburg",
                ["games"] = new JsonArray(games),
            }.ToJsonString());
            return path;
        }

        private string WriteDefinitions(params string[] sets)
        {
            var array = new JsonArray();

            foreach (var set in sets)
            {
                var trophies = new JsonArray();

                foreach (var id in new[] { 1, 2, 3, 4, 5 })
                {
                    trophies.Add(new JsonObject
                    {
                        ["trophyId"] = id,
                        ["trophyType"] = id == 3 ? "gold" : id == 5 ? "silver" : "bronze",
                        ["trophyName"] = $"Invented Old {id}",
                        ["trophyDetail"] = "Invented.",
                        ["trophyIconUrl"] = $"https://image.api.playstation.com/zz/old-{id}.png",
                        ["trophyGroupId"] = "default",
                        ["trophyHidden"] = false,
                    });
                }

                array.Add(new JsonObject
                {
                    ["npCommunicationId"] = set,
                    ["npServiceName"] = "trophy",
                    ["trophySetVersion"] = "01.00",
                    ["trophies"] = trophies,
                });
            }

            var path = Path.Combine(_root, "zzdefinitions-previous.json");
            File.WriteAllText(path, new JsonObject
            {
                ["format"] = PsnTrophyDefinitionsReader.Format,
                ["fetchedUtc"] = "2026-10-27T12:00:00Z",
                ["sets"] = array,
            }.ToJsonString());
            return path;
        }

        private static string[] Decision(string title, string status, string setsA, string setsB) => [title, status, setsA, setsB];

        /// <summary>Writes the decisions CSV (header in the account names' lower case, as Angie's file has it).</summary>
        private string WriteDecisions(params string[][] rows) => WriteDecisionsText(
            "game_title,status,alpha_trophy_set_ids,beta_trophy_set_ids\r\n" +
            string.Concat(rows.Select(row => string.Join(',', row.Select(Csv)) + "\r\n")));

        private string WriteDecisionsText(string text, bool bom = false)
        {
            var path = Path.Combine(_root, "zzdecisions.csv");
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(bom));
            return path;
        }

        private static string Csv(string value) =>
            value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
                ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : value;

        /// <summary>The to-review CSV as rows of cells (the tests' values hold no comma or quote).</summary>
        private List<string[]> ReadReview(string name = PsnTrophyFetcher.ToReviewFile)
        {
            var bytes = File.ReadAllBytes(Path.Combine(Out, name));
            Assert.True(bytes.AsSpan().StartsWith(System.Text.Encoding.UTF8.Preamble), "written with a byte order mark");
            return System.Text.Encoding.UTF8.GetString(bytes[3..])
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(','))
                .ToList();
        }

        private PsnFetchOptions Options(int titlePage = 800, int trophyPage = 100) =>
            new(PreviousList, Definitions, Decisions, AccountA, AccountB, AccountA, Out) { TitlePageSize = titlePage, TrophyPageSize = trophyPage };

        private (PsnFetchResult Result, PsnApiClient Api) Fetch(FakePrompt prompt, PsnFetchOptions? options = null, Func<DateTimeOffset>? clock = null)
        {
            using var output = new StringWriter();
            var api = new PsnApiClient(Server, prompt, _sleeps.Add, output, clock);
            var result = PsnTrophyFetcher.Run(options ?? Options(), api, prompt, () => Now);
            AssertNoSecret(output.ToString());
            return (result, api);
        }

        private (int Code, string Output, string Error) RunCommand(FakePrompt prompt, params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = MemoryImportCommand.Run(
                args, output, error, () => Now, () => throw new InvalidOperationException("No images here."),
                () => new PsnFetchEnvironment(Server, prompt, _sleeps.Add));
            var (o, e) = (output.ToString(), error.ToString());

            foreach (var text in new[] { o, e })
            {
                AssertNoSecret(text);

                foreach (var other in new[] { "ZZSET", "Invented", AccountA, AccountB, "https", "zzlist", "zzdefinitions", _root })
                {
                    Assert.DoesNotContain(other, text, StringComparison.OrdinalIgnoreCase);
                }
            }

            return (code, o, e);
        }

        private string[] CommandArgs() =>
        [
            "psn-fetch", "--previous-list", PreviousList, "--definitions", Definitions, "--decisions", Decisions,
            "--account-a", AccountA, "--account-b", AccountB, "--local", AccountA, "--out", Out,
        ];

        private static void AssertNoSecret(string text)
        {
            foreach (var secret in new[] { Token, NewToken, Partner })
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }

        private void AssertNoSecretInOutFolder()
        {
            foreach (var file in Directory.GetFiles(Out))
            {
                AssertNoSecret(File.ReadAllText(file));
            }
        }

        private JsonObject ReadOut(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Out, name)))!.AsObject();

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        private List<string> RequestsTo(string fragment) =>
            Server.Requests.Select(r => r.Url).Where(url => url.Contains(fragment, StringComparison.Ordinal)).ToList();

        [Fact]
        public void Command_EndToEnd_AddsTogetherGame_ListsUndecidedForReview_AndPsnBuildAcceptsTheOutput()
        {
            var listHash = Hash(PreviousList);
            var definitionsHash = Hash(Definitions);
            var decisionsHash = Hash(Decisions);
            var prompt = new FakePrompt(Partner, Token);

            var (code, output, error) = RunCommand(prompt, CommandArgs());

            Assert.True(code == MemoryImportCommand.Success, error);
            Assert.Contains("on the previous list: 2", output, StringComparison.Ordinal);
            Assert.Contains("added from decisions (together): 1", output, StringComparison.Ordinal);
            Assert.Contains("not added, excluded: 0", output, StringComparison.Ordinal);
            Assert.Contains("not added, uncertain: 0", output, StringComparison.Ordinal);
            Assert.Contains("awaiting review (no decisions row; see psn-to-review.csv): 1", output, StringComparison.Ordinal);
            Assert.Contains("listed now: 3", output, StringComparison.Ordinal);
            Assert.Contains("trophies earned, account a: 4", output, StringComparison.Ordinal);
            Assert.Contains("trophies earned, account b: 4", output, StringComparison.Ordinal);
            Assert.Contains("sets added: 1", output, StringComparison.Ordinal);
            Assert.Contains("sets refreshed for new trophies (DLC): 0", output, StringComparison.Ordinal);
            Assert.Contains("trophies fetched: 5", output, StringComparison.Ordinal);
            Assert.Contains("ATTENTION: 1 candidate(s) await review", output, StringComparison.Ordinal);

            // The inputs are never written.
            Assert.Equal(listHash, Hash(PreviousList));
            Assert.Equal(definitionsHash, Hash(Definitions));

            // Game list: the two listed games as listed, then the added one.
            var games = ReadOut(PsnTrophyFetcher.GameListFile)["games"]!.AsArray();
            Assert.Equal([$"{Quest}|{Quest}", $"{RacerA}|{RacerB}", $"{Border}|{Border}"], games.Select(g => (string)g!["key"]!).ToArray());
            Assert.Equal(1, (int)games[0]![AccountA]!["expectedEarnedCount"]!);
            Assert.Equal("Invented Border", (string)games[2]!["game"]!);
            Assert.True((bool)games[2]!["sameTrophySet"]!);
            Assert.Equal("trophy", (string)games[2]![AccountB]!["npServiceName"]!);
            Assert.Equal("invented", (string)ReadOut(PsnTrophyFetcher.GameListFile)["description"]!);

            // To review: the same title with different sets and no decisions row, never added.
            var review = ReadReview();
            Assert.Equal(["game_title", "status", "alpha_trophy_set_ids", "beta_trophy_set_ids", "alpha_last_updated", "beta_last_updated"], review[0]);
            Assert.Equal(["Invented Twin\u2122", "", TwinA, TwinB, "", ""], Assert.Single(review.Skip(1)));
            Assert.Equal(decisionsHash, Hash(Decisions));

            // Timestamps: the old script's shape, every listed game.
            var timestamps = ReadOut(PsnTrophyFetcher.TimestampsFile);
            Assert.Equal(PsnSharedActivityBuilder.TimestampsFormat, (string)timestamps["format"]!);
            Assert.True((bool)timestamps["timestampsAreUTC"]!);
            Assert.Equal(3, timestamps["games"]!.AsArray().Count);
            var quest = timestamps["games"]![0]!;
            Assert.Equal(5, (int)quest[AccountA]!["totalTrophies"]!);
            Assert.Equal(2, (int)quest[AccountA]!["earnedCount"]!);
            Assert.Equal("2015-03-01T10:00:30Z", (string)quest[AccountB]!["trophies"]![0]!["earnedDateTimeUTC"]!);
            Assert.Equal("trophy2", (string)timestamps["games"]![1]![AccountA]!["npServiceName"]!);

            // Account a is "me"; account b is the other account's id. No call names any other user.
            Assert.All(RequestsTo("/npCommunicationIds/" + RacerA), url => Assert.StartsWith("users/me/", url, StringComparison.Ordinal));
            Assert.All(RequestsTo("/npCommunicationIds/" + RacerB), url => Assert.StartsWith($"users/{Partner}/", url, StringComparison.Ordinal));

            // psn-build accepts the output as the refresh passes it (the current file for icon carry-over,
            // icons required): see PsnBuild_CarriesListedGameIconsOverFromTheCurrentFile for the icons.
            var timestampsOut = File.ReadAllBytes(Path.Combine(Out, PsnTrophyFetcher.TimestampsFile));
            var listOut = File.ReadAllBytes(Path.Combine(Out, PsnTrophyFetcher.GameListFile));
            var built = PsnSharedActivityBuilder.Build([timestampsOut], listOut, AccountA, AccountB, CurrentFile(timestampsOut));
            Assert.Equal(3, built.Counts.GamesBuilt);
            Assert.Equal(3, built.Counts.Events);
            Assert.Equal(0, built.Counts.IconsNone);
            Assert.Equal(3, PsnSharedActivityReader.Parse(built.Json, AccountA, AccountB, AccountA).Records.Count);

            // The definitions file passes the enrich reader.
            var definitions = PsnTrophyDefinitionsReader.Read(Path.Combine(Out, PsnTrophyFetcher.DefinitionsFile));
            Assert.Equal(4, definitions.SetCount);
            Assert.Equal(20, definitions.Definitions.Count);

            AssertNoSecretInOutFolder();
            Assert.Equal(1, prompt.PartnerAsked);
            Assert.Equal(1, prompt.TokensAsked);
            Assert.Contains(Server.Requests, request => request.Authorization == "Bearer " + Token);
        }

        /// <summary>
        /// Last month's shared-activity file, built from the previous list with icons for both listed
        /// games (as the October file has them), without the game added this run.
        /// </summary>
        private byte[] CurrentFile(byte[] timestamps)
        {
            var list = JsonNode.Parse(File.ReadAllText(PreviousList))!;
            list["games"]![1]!["icon"] = RacerCurrentIcon;
            return PsnSharedActivityBuilder.Build(
                [timestamps], System.Text.Encoding.UTF8.GetBytes(list.ToJsonString()), AccountA, AccountB).Json;
        }

        [Fact]
        public void PsnBuild_CarriesListedGameIconsOverFromTheCurrentFile_AndCompareShowsNoIconChange()
        {
            var (result, _) = Fetch(new FakePrompt(Partner, Token));
            Assert.True(result.Completed);

            var timestamps = File.ReadAllBytes(Path.Combine(Out, PsnTrophyFetcher.TimestampsFile));
            var list = File.ReadAllBytes(Path.Combine(Out, PsnTrophyFetcher.GameListFile));
            var current = CurrentFile(timestamps);

            // Without the current file the listed game with events and no icon is refused, as in the plan.
            Assert.Throws<InvalidOperationException>(() => PsnSharedActivityBuilder.Build([timestamps], list, AccountA, AccountB));

            // With it (the refresh passes -PsnCurrent): the icon is carried over, nothing is missing.
            var built = PsnSharedActivityBuilder.Build([timestamps], list, AccountA, AccountB, current);
            var games = JsonNode.Parse(built.Json)!["games"]!.AsArray().ToDictionary(g => (string)g!["key"]!, g => (string?)g!["icon"]);
            Assert.Equal(RacerCurrentIcon, games[$"{RacerA}|{RacerB}"]);
            Assert.Equal(QuestListIcon, games[$"{Quest}|{Quest}"]);
            Assert.Equal(BorderTitleIcon, games[$"{Border}|{Border}"]);
            Assert.Equal(1, built.Counts.IconsFromExistingFile);
            Assert.Equal(2, built.Counts.IconsFromManifest);
            Assert.Equal(0, built.Counts.IconsNone);

            // psn-compare: the only game difference is the added one; no icon changes.
            var compare = PsnSharedActivityComparer.Compare(
                PsnSharedActivityReader.Parse(current, AccountA, AccountB, AccountA), current,
                PsnSharedActivityReader.Parse(built.Json, AccountA, AccountB, AccountA), built.Json);
            Assert.Equal(0, compare.GamesDifferentIcon);
            Assert.Equal(0, compare.GamesDifferentTitle);
            Assert.Equal(1, compare.GamesOnlyNew);
            Assert.Equal(0, compare.GamesOnlyOld);
        }

        [Fact]
        public void IconsAndDefinitions_AreAddedOnlyWhereMissing()
        {
            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            var games = ReadOut(PsnTrophyFetcher.GameListFile)["games"]!.AsArray();

            // Listed with an icon: kept, even though the title list has another.
            Assert.Equal(QuestListIcon, (string)games[0]!["icon"]!);

            // Listed without one: still none (psn-build carries it over from the current file), even
            // though the title list has one.
            Assert.False(games[1]!.AsObject().ContainsKey("icon"));
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(File.ReadAllText(PreviousList))!["games"]![1],
                games[1]));

            // Added: the title icon.
            Assert.Equal(BorderTitleIcon, (string)games[2]!["icon"]!);
            Assert.Equal(1, result.Counts.IconsAdded);
            Assert.Equal(0, result.Counts.AddedWithoutIcon);
            Assert.Equal(1, result.Counts.IconsNone);

            // Definitions fetched for the added game's set only; the given sets are copied unchanged.
            Assert.Equal([$"npCommunicationIds/{Border}/trophyGroups/all/trophies?npServiceName=trophy&limit=100&offset=0"],
                Server.Requests.Select(r => r.Url).Where(url => url.StartsWith("npCommunicationIds/", StringComparison.Ordinal)).ToList());
            Assert.Equal(1, result.Counts.DefinitionSetsFetched);

            var definitions = ReadOut(PsnTrophyFetcher.DefinitionsFile);
            var sets = definitions["sets"]!.AsArray();
            Assert.Equal([Quest, RacerA, RacerB, Border], sets.Select(s => (string)s!["npCommunicationId"]!).ToArray());
            Assert.Equal("Invented Old 1", (string)sets[0]!["trophies"]![0]!["trophyName"]!);
            Assert.Equal("Invented Trophy 1", (string)sets[3]!["trophies"]![0]!["trophyName"]!);
            Assert.Equal("01.02", (string)sets[3]!["trophySetVersion"]!);
            Assert.Null(sets[3]!["trophies"]![2]!["trophyName"]);
            Assert.True((bool)sets[3]!["trophies"]![2]!["trophyHidden"]!);
            Assert.Equal("2026-11-09T08:00:00Z", (string)definitions["fetchedUtc"]!);
        }

        [Fact]
        public void NothingNew_DefinitionsFileIsAnUnchangedCopy()
        {
            Server.Titles["me"].RemoveAll(t => t.SetId == Border);
            var before = File.ReadAllText(Definitions);

            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(0, result.Counts.Added);
            Assert.Equal(0, result.Counts.DefinitionSetsFetched);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), ReadOut(PsnTrophyFetcher.DefinitionsFile)));
        }

        [Fact]
        public void Paging_TitlesAndTrophies_AreFollowedToTheTotal()
        {
            var (result, _) = Fetch(new FakePrompt(Partner, Token), Options(titlePage: 2, trophyPage: 2));

            Assert.True(result.Completed);
            Assert.Equal(1, result.Counts.Added);
            Assert.Equal(3, RequestsTo("users/me/trophyTitles").Count);
            Assert.Equal(2, RequestsTo($"users/{Partner}/trophyTitles").Count);
            Assert.Contains("users/me/trophyTitles?limit=2&offset=4", RequestsTo("users/me/trophyTitles"));
            Assert.Equal(3, RequestsTo($"users/me/npCommunicationIds/{Quest}/").Count);
            Assert.Equal(3, Server.Requests.Count(r => r.Url.StartsWith($"npCommunicationIds/{Border}/", StringComparison.Ordinal)));

            var border = ReadOut(PsnTrophyFetcher.TimestampsFile)["games"]![2]!;
            Assert.Equal(2, (int)border[AccountB]!["earnedCount"]!);
            Assert.Equal(5, ReadOut(PsnTrophyFetcher.DefinitionsFile)["sets"]![3]!["trophies"]!.AsArray().Count);
        }

        [Fact]
        public void Paging_TitlesIncomplete_IsRefused()
        {
            Server.TotalTitlesBonus = 1;

            var exception = Assert.Throws<InvalidOperationException>(() => Fetch(new FakePrompt(Partner, Token), Options(titlePage: 2)));

            Assert.Contains("incomplete", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(Out, PsnTrophyFetcher.GameListFile)));
            Assert.Empty(RequestsTo("/npCommunicationIds/"));
        }

        [Fact]
        public void Paging_TrophiesIncomplete_IsRefused_AndNothingOfThatGameIsSaved()
        {
            Server.TotalTrophiesBonus = 1;

            var exception = Assert.Throws<InvalidOperationException>(() => Fetch(new FakePrompt(Partner, Token), Options(trophyPage: 2)));

            Assert.Contains("incomplete", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(Out, PsnTrophyFetcher.TimestampsFile)));
        }

        [Fact]
        public void Decisions_ExcludedAndUncertain_AreNotAdded_AndCountedApart()
        {
            Server.Titles["me"].Add(new FakeTitle("ZZSET00008_00", "Invented Maybe"));
            Server.Titles[Partner].Add(new FakeTitle("ZZSET00008_00", "Invented Maybe"));
            Server.Sets["ZZSET00008_00"] = [new(1)];
            Decisions = WriteDecisions(
                Decision("Invented Border", PsnDecisionsFile.Excluded, Border, Border),
                Decision("Invented Maybe", PsnDecisionsFile.Uncertain, "ZZSET00008_00", "ZZSET00008_00"));

            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(0, result.Counts.Added);
            Assert.Equal(1, result.Counts.NotAddedExcluded);
            Assert.Equal(1, result.Counts.NotAddedUncertain);
            Assert.Equal(1, result.Counts.AwaitingReview);
            Assert.Equal(2, result.Counts.ListedNow);
            Assert.DoesNotContain(ReadReview(), row => row[2] == Border || row[2] == "ZZSET00008_00");

            // Nothing is fetched for games that were not added.
            Assert.Empty(RequestsTo(Border));
        }

        [Fact]
        public void Decisions_NoRow_GoesToReview_NewestFirst_WithTheTitleListDates()
        {
            Server.Titles["me"].Add(new FakeTitle("ZZSET00009_00", "Invented Older", null, "trophy", "2020-01-02T03:04:05Z"));
            Server.Titles[Partner].Add(new FakeTitle("ZZSET00009_00", "Invented Older", null, "trophy", "2021-06-07T22:00:00Z"));
            Server.Titles["me"].Add(new FakeTitle("ZZSET00010_00", "Invented Newer", null, "trophy", "2026-10-30T23:30:00+00:00"));
            Server.Titles[Partner].Add(new FakeTitle("ZZSET00010_00", "Invented Newer"));
            Decisions = WriteDecisions();

            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(0, result.Counts.Added);
            Assert.Equal(4, result.Counts.AwaitingReview);
            var rows = ReadReview().Skip(1).ToList();
            Assert.Equal(["Invented Newer", "", "ZZSET00010_00", "ZZSET00010_00", "2026-10-30", ""], rows[0]);
            Assert.Equal(["Invented Older", "", "ZZSET00009_00", "ZZSET00009_00", "2020-01-02", "2021-06-07"], rows[1]);
            Assert.Equal(new[] { Border, TwinA }, rows.Skip(2).Select(row => row[2]).Order(StringComparer.Ordinal).ToArray());
            Assert.All(rows, row => Assert.Equal("", row[1]));
        }

        [Fact]
        public void Decisions_MultiIdCells_AndADifferentSetTogetherPair_AreAdded()
        {
            // Twin: a different-set pair marked together in a row whose cells list two versions each.
            Decisions = WriteDecisions(
                Decision("Invented Border", PsnDecisionsFile.Together, Border, Border),
                Decision("Invented Twin, both versions", PsnDecisionsFile.Together, $" ZZSET00099_00 ; {TwinA}", $"{TwinB};ZZSET00098_00 "));

            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(2, result.Counts.Added);
            Assert.Equal(0, result.Counts.AwaitingReview);
            Assert.Equal(0, result.Counts.TogetherNotReturned);
            var twin = ReadOut(PsnTrophyFetcher.GameListFile)["games"]!.AsArray().Single(g => (string)g!["key"]! == $"{TwinA}|{TwinB}")!;
            Assert.False((bool)twin["sameTrophySet"]!);
            Assert.Equal("trophy2", (string)twin[AccountA]!["npServiceName"]!);
            Assert.Equal(4, ReadOut(PsnTrophyFetcher.TimestampsFile)["games"]!.AsArray().Count);
            Assert.Single(ReadReview());
        }

        [Fact]
        public void Decisions_TogetherRowWithNoReturnedPair_IsListed_NotAStop()
        {
            Decisions = WriteDecisions(
                Decision("Invented Border", PsnDecisionsFile.Together, Border, Border),
                Decision("Invented Gone", PsnDecisionsFile.Together, "ZZSET00097_00", "ZZSET00097_00"));

            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(1, result.Counts.Added);
            Assert.Equal(1, result.Counts.TogetherNotReturned);
            var rows = ReadReview(PsnTrophyFetcher.TogetherNotReturnedFile);
            Assert.Equal(["Invented Gone", "together", "ZZSET00097_00", "ZZSET00097_00"], Assert.Single(rows.Skip(1)));
        }

        [Fact]
        public void Decisions_ConflictingRows_StopBeforeFetching()
        {
            Decisions = WriteDecisions(
                Decision("Invented Border", PsnDecisionsFile.Together, Border, Border),
                Decision("Invented Border again", PsnDecisionsFile.Excluded, $"{Border};ZZSET00096_00", Border));

            var (code, output, _) = RunCommand(new FakePrompt(Partner, Token), CommandArgs());

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("rows with different statuses: 1", output, StringComparison.Ordinal);
            Assert.Contains("marked excluded or uncertain: 0", output, StringComparison.Ordinal);
            Assert.DoesNotContain(Server.Requests, r => r.Url.Contains("npCommunicationIds/", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(Out, PsnTrophyFetcher.GameListFile)));
            var conflict = Assert.Single(ReadOut(PsnTrophyFetcher.ConflictsFile)["conflicts"]!.AsArray())!;
            Assert.Equal(PsnTrophyFetcher.ConflictRows, (string)conflict["kind"]!);
            Assert.Equal($"{Border}|{Border}", (string)conflict["key"]!);
            Assert.Equal([1, 2], conflict["decisionRows"]!.AsArray().Select(n => (int)n!).ToArray());
        }

        [Fact]
        public void Decisions_PreviousListGameMarkedExcluded_StopsBeforeFetching_AndAFixedFileRunsInANewFolder()
        {
            Decisions = WriteDecisions(
                Decision("Invented Border", PsnDecisionsFile.Together, Border, Border),
                Decision("Invented Racer", PsnDecisionsFile.Excluded, RacerA, RacerB));

            var (code, output, _) = RunCommand(new FakePrompt(Partner, Token), CommandArgs());

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("previous-list games marked excluded or uncertain: 1", output, StringComparison.Ordinal);
            Assert.DoesNotContain(Server.Requests, r => r.Url.Contains("npCommunicationIds/", StringComparison.Ordinal));
            var conflict = Assert.Single(ReadOut(PsnTrophyFetcher.ConflictsFile)["conflicts"]!.AsArray())!;
            Assert.Equal(PsnTrophyFetcher.ConflictListedGame, (string)conflict["kind"]!);

            // Nothing was fetched, so the same folder makes the list again from the corrected file.
            Decisions = WriteDecisions(Decision("Invented Border", PsnDecisionsFile.Together, Border, Border));
            var (again, _, error) = RunCommand(new FakePrompt(Partner, Token), CommandArgs());
            Assert.True(again == MemoryImportCommand.Success, error);
            Assert.False(File.Exists(Path.Combine(Out, PsnTrophyFetcher.ConflictsFile)));
        }

        [Fact]
        public void Decisions_ByteOrderMark_QuotedTitle_AnyHeaderCase_AndExtraColumns_AreRead()
        {
            Decisions = WriteDecisionsText(
                "Notes,GAME_TITLE,Status,Alpha_Trophy_Set_Ids,BETA_trophy_set_ids\r\n" +
                $"x,\"Invented Border, \"\"Deluxe\"\"\",  Together ,{Border},{Border}\r\n" +
                "\r\n",
                bom: true);

            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(1, result.Counts.Added);

            var decisions = PsnDecisionsFile.Parse(File.ReadAllBytes(Decisions), AccountA, AccountB);
            Assert.Equal("Invented Border, \"Deluxe\"", Assert.Single(decisions).Title);
        }

        [Theory]
        [InlineData("game_title,status,alpha_trophy_set_ids\r\n", "account b's trophy set id column")]
        [InlineData("game_title,alpha_trophy_set_ids,beta_trophy_set_ids\r\n", "the status column")]
        [InlineData("status,alpha_trophy_set_ids,beta_trophy_set_ids\r\n", "the game_title column")]
        [InlineData("game_title,status,alpha_trophy_set_ids,beta_trophy_set_ids\r\nInvented,maybe,ZZSET00001_00,ZZSET00001_00\r\n", "row 1: the status")]
        public void Decisions_MissingColumnOrUnknownStatus_IsRefused_BeforeAnyCall(string text, string expected)
        {
            Decisions = WriteDecisionsText(text);

            var (code, _, error) = RunCommand(new FakePrompt(Partner, Token), CommandArgs());

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains(expected, error, StringComparison.Ordinal);
            Assert.Empty(Server.Requests);
            Assert.False(Directory.Exists(Out));
        }

        [Fact]
        public void SharedSetAlsoInAListedPair_IsACandidate_ForReview_NotTheList()
        {
            // Account b also has account a's Racer set: one set on both accounts, Racer listed as a pair.
            Server.Titles[Partner].Add(new FakeTitle(RacerA, "Invented Racer", null, "trophy2"));

            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(1, result.Counts.Added);
            Assert.Equal(2, result.Counts.AwaitingReview);
            Assert.Contains(ReadReview(), row => row[2] == RacerA && row[3] == RacerA);
            Assert.DoesNotContain(ReadOut(PsnTrophyFetcher.GameListFile)["games"]!.AsArray(), g => (string)g!["key"]! == $"{RacerA}|{RacerA}");
        }

        [Fact]
        public void PreviousListGame_NoLongerReturned_StopsBeforeAnyTrophyIsFetched()
        {
            Server.Titles[Partner].RemoveAll(t => t.SetId == RacerB);
            var prompt = new FakePrompt(Partner, Token);

            var (code, output, _) = RunCommand(prompt, CommandArgs());

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("account a 0, account b 1", output, StringComparison.Ordinal);
            Assert.DoesNotContain(Server.Requests, r => r.Url.Contains("npCommunicationIds/", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(Out, PsnTrophyFetcher.TimestampsFile)));
            Assert.False(File.Exists(Path.Combine(Out, PsnTrophyFetcher.GameListFile)));

            var missing = Assert.Single(ReadOut(PsnTrophyFetcher.MissingFile)["games"]!.AsArray())!;
            Assert.Equal($"{RacerA}|{RacerB}", (string)missing["key"]!);
            Assert.False((bool)missing["missingForAccountA"]!);
            Assert.True((bool)missing["missingForAccountB"]!);
            AssertNoSecretInOutFolder();

            // Once PlayStation returns it again, the same folder continues and the missing file goes.
            Server = Standard();
            var (again, _, error) = RunCommand(new FakePrompt(Partner, Token), CommandArgs());
            Assert.True(again == MemoryImportCommand.Success, error);
            Assert.False(File.Exists(Path.Combine(Out, PsnTrophyFetcher.MissingFile)));
        }

        [Fact]
        public void Resume_SkipsFinishedGames()
        {
            // The first run stops on a hard error at account b of the second game.
            Server.Override = (request, _) =>
                request.RequestUri!.AbsolutePath.Contains($"/users/{Partner}/npCommunicationIds/{RacerB}/", StringComparison.Ordinal)
                    ? FakePsnServer.Reply(HttpStatusCode.NotFound, "{}")
                    : null;

            var exception = Assert.Throws<InvalidOperationException>(() => Fetch(new FakePrompt(Partner, Token)));
            Assert.Contains("HTTP 404", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Partner, exception.Message, StringComparison.Ordinal);
            Assert.Single(ReadOut(PsnTrophyFetcher.TimestampsFile)["games"]!.AsArray());

            Server.Override = null;
            Server.Requests.Clear();
            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(1, result.Counts.GamesAlreadySaved);
            Assert.Equal(2, result.Counts.GamesFetched);
            Assert.Equal(1, result.Counts.Added);
            Assert.Empty(RequestsTo($"/npCommunicationIds/{Quest}/"));
            Assert.Empty(RequestsTo("trophyTitles"));
            Assert.Equal(3, ReadOut(PsnTrophyFetcher.TimestampsFile)["games"]!.AsArray().Count);
            Assert.Equal(4, result.Counts.TrophiesA);
        }

        [Fact]
        public void Resume_WithOtherInputs_IsRefused()
        {
            Server.Override = (request, _) =>
                request.RequestUri!.AbsolutePath.Contains($"/npCommunicationIds/{RacerB}/", StringComparison.Ordinal)
                    ? FakePsnServer.Reply(HttpStatusCode.NotFound, "{}")
                    : null;
            Assert.Throws<InvalidOperationException>(() => Fetch(new FakePrompt(Partner, Token)));
            Server.Override = null;

            File.Delete(PreviousList);
            PreviousList = WriteList(Listed(Quest, Quest, "Invented Quest", QuestListIcon));

            var exception = Assert.Throws<InvalidOperationException>(() => Fetch(new FakePrompt(Partner, Token)));
            Assert.Contains("other inputs", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void OutFolder_ExistingNotOurs_OrFinished_IsRefused()
        {
            Directory.CreateDirectory(Out);
            var exception = Assert.Throws<InvalidOperationException>(() => Fetch(new FakePrompt(Partner, Token)));
            Assert.Contains("not an unfinished psn-fetch run", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Server.Requests);
            Directory.Delete(Out);

            Assert.True(Fetch(new FakePrompt(Partner, Token)).Result.Completed);
            exception = Assert.Throws<InvalidOperationException>(() => Fetch(new FakePrompt(Partner, Token)));
            Assert.Contains("finished psn-fetch run", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Token_RejectedWith401_AsksForANewOne_AndContinues()
        {
            // The token expires after the title lists.
            Server.Override = (_, count) =>
            {
                if (count == 3)
                {
                    Server.Token = NewToken;
                }

                return null;
            };
            var prompt = new FakePrompt(Partner, Token, NewToken);

            var (code, output, error) = RunCommand(prompt, CommandArgs());

            Assert.True(code == MemoryImportCommand.Success, error);
            Assert.Equal(2, prompt.TokensAsked);
            Assert.Contains("new tokens: 1", output, StringComparison.Ordinal);
            Assert.Contains("Request 1 then Request 2", output, StringComparison.Ordinal);
            Assert.Contains(Server.Requests, r => r.Authorization == "Bearer " + NewToken);
            AssertNoSecretInOutFolder();
        }

        [Fact]
        public void Retry_On429_BacksOff_AndContinues()
        {
            var failures = 0;
            Server.Override = (request, _) =>
                request.RequestUri!.AbsolutePath.Contains("/trophyGroups/", StringComparison.Ordinal) && failures++ < 2
                    ? FakePsnServer.Reply(HttpStatusCode.TooManyRequests, "{}")
                    : null;

            var (result, api) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(2, api.Retries);
            Assert.Contains(TimeSpan.FromSeconds(5), _sleeps);
            Assert.Contains(TimeSpan.FromSeconds(10), _sleeps);
            Assert.Contains(PsnApiClient.Pause, _sleeps);
        }

        [Fact]
        public void Forbidden403_StopsWithAPrivacyMessage_WithoutAskingForANewToken_AndARerunContinues()
        {
            Server.Override = (request, _) =>
                request.RequestUri!.AbsolutePath.Contains($"/users/{Partner}/npCommunicationIds/{RacerB}/", StringComparison.Ordinal)
                    ? FakePsnServer.Reply(HttpStatusCode.Forbidden, "{}")
                    : null;
            var prompt = new FakePrompt(Partner, Token, NewToken);

            var exception = Assert.Throws<InvalidOperationException>(() => Fetch(prompt));

            Assert.Contains("HTTP 403", exception.Message, StringComparison.Ordinal);
            Assert.Contains("privacy", exception.Message, StringComparison.Ordinal);
            Assert.Contains("run the same command again", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Partner, exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, prompt.TokensAsked);
            Assert.Single(RequestsTo($"/npCommunicationIds/{RacerB}/"));
            Assert.Single(ReadOut(PsnTrophyFetcher.TimestampsFile)["games"]!.AsArray());

            Server.Override = null;
            var (result, _) = Fetch(new FakePrompt(Partner, Token));
            Assert.True(result.Completed);
            Assert.Equal(1, result.Counts.GamesAlreadySaved);
        }

        [Fact]
        public void Token_NotAscii_IsRefusedBeforeAnyCall()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => Fetch(new FakePrompt(Partner, "zzToken\u00E9Value")));

            Assert.Contains("ASCII", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Server.Requests);
        }

        [Theory]
        [InlineData(HttpStatusCode.TooManyRequests, 42, 42)]
        [InlineData(HttpStatusCode.ServiceUnavailable, 42, 42)]
        [InlineData(HttpStatusCode.TooManyRequests, 3600, 300)]
        public void RetryAfter_InSeconds_IsHonoured_AndCapped(HttpStatusCode status, int retryAfter, int expected)
        {
            var failed = false;
            Server.Override = (request, _) =>
            {
                if (failed || !request.RequestUri!.AbsolutePath.Contains("/trophyGroups/", StringComparison.Ordinal))
                {
                    return null;
                }

                failed = true;
                var reply = FakePsnServer.Reply(status, "{}");
                reply.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfter));
                return reply;
            };

            var (result, api) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(1, api.Retries);
            Assert.Contains(TimeSpan.FromSeconds(expected), _sleeps);
            Assert.DoesNotContain(TimeSpan.FromSeconds(5), _sleeps);
        }

        [Fact]
        public void RetryAfter_AsAnHttpDate_IsHonouredAgainstTheClock()
        {
            var clock = new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);
            var failed = false;
            Server.Override = (request, _) =>
            {
                if (failed || !request.RequestUri!.AbsolutePath.Contains("/trophyGroups/", StringComparison.Ordinal))
                {
                    return null;
                }

                failed = true;
                var reply = FakePsnServer.Reply(HttpStatusCode.TooManyRequests, "{}");
                reply.Headers.RetryAfter = new RetryConditionHeaderValue(clock.AddSeconds(120));
                return reply;
            };

            var (result, _) = Fetch(new FakePrompt(Partner, Token), clock: () => clock);

            Assert.True(result.Completed);
            Assert.Contains(TimeSpan.FromSeconds(120), _sleeps);
        }

        [Fact]
        public void Dlc_ListedSetWithFewerDefinitionsThanTrophies_IsRefreshed_AndEnrichNamesTheNewTrophy()
        {
            // A DLC trophy group added to a previous-list game: id 6 in group 001, earned by both.
            Server.Sets[Quest].Add(new FakeTrophy(6, "gold", "001"));
            Server.AddEarned("me", Quest, 6, Utc(1, 12, 0));
            Server.AddEarned(Partner, Quest, 6, Utc(1, 12, 0, 20));

            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(1, result.Counts.DefinitionSetsRefreshed);
            Assert.Equal(1, result.Counts.DefinitionSetsFetched);
            Assert.Equal(0, result.Counts.DefinitionSetsKeptLarger);
            Assert.Equal(0, result.Counts.DefinitionSetsStillShort);

            // The earned DLC trophy is in the timestamps (trophyGroups/all).
            var questTimes = ReadOut(PsnTrophyFetcher.TimestampsFile)["games"]![0]!;
            Assert.Equal(6, (int)questTimes[AccountA]!["totalTrophies"]!);
            Assert.Contains(questTimes[AccountA]!["trophies"]!.AsArray(), t => (long)t!["trophyId"]! == 6);

            // The set's entry is replaced in place, with every group.
            var sets = ReadOut(PsnTrophyFetcher.DefinitionsFile)["sets"]!.AsArray();
            Assert.Equal([Quest, RacerA, RacerB, Border], sets.Select(s => (string)s!["npCommunicationId"]!).ToArray());
            var quest = sets[0]!["trophies"]!.AsArray();
            Assert.Equal(6, quest.Count);
            Assert.Equal("001", (string)quest[5]!["trophyGroupId"]!);
            Assert.Equal("Invented Trophy 6", (string)quest[5]!["trophyName"]!);

            // End to end: psn-build (as the plan), import, enrich: the DLC trophy gets its name.
            var timestamps = File.ReadAllBytes(Path.Combine(Out, PsnTrophyFetcher.TimestampsFile));
            var built = PsnSharedActivityBuilder.Build(
                [timestamps], File.ReadAllBytes(Path.Combine(Out, PsnTrophyFetcher.GameListFile)), AccountA, AccountB, CurrentFile(timestamps));
            var memories = Path.Combine(_root, "memories.db");
            Chatcheology.Data.Memories.MemoriesDatabase.Create(memories, Now);
            new PsnImporter().Import(memories, PsnSharedActivityReader.Parse(built.Json, AccountA, AccountB, AccountA));
            var enriched = PsnTrophyEnricher.Enrich(memories, PsnTrophyDefinitionsReader.Read(Path.Combine(Out, PsnTrophyFetcher.DefinitionsFile)));

            Assert.Equal(0, enriched.ItemsWithoutDefinition);
            Assert.Equal(0, enriched.ItemsTypeMismatch);

            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={memories};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Name, GroupId FROM ItemDefinition WHERE SetId = $set AND ItemId = '6';";
                command.Parameters.AddWithValue("$set", Quest);
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("Invented Trophy 6", reader.GetString(0));
                Assert.Equal("001", reader.GetString(1));
            }

            // The DLC set was fetched once, in full.
            Assert.Single(Server.Requests, r => r.Url.StartsWith($"npCommunicationIds/{Quest}/", StringComparison.Ordinal));
        }

        [Fact]
        public void Dlc_FewerDefinitionsReturnedThanHeld_KeepsTheHeldSet_AndCountsIt()
        {
            Server.Sets[Quest].Add(new FakeTrophy(6, "gold", "001"));
            Server.DefinitionSets[Quest] = [new(1), new(2), new(3, "gold"), new(4)];
            var before = JsonNode.Parse(File.ReadAllText(Definitions))!["sets"]![0]!.DeepClone();

            var (result, _) = Fetch(new FakePrompt(Partner, Token));

            Assert.True(result.Completed);
            Assert.Equal(0, result.Counts.DefinitionSetsRefreshed);
            Assert.Equal(1, result.Counts.DefinitionSetsKeptLarger);
            Assert.Equal(1, result.Counts.DefinitionSetsStillShort);
            Assert.True(JsonNode.DeepEquals(before, ReadOut(PsnTrophyFetcher.DefinitionsFile)["sets"]![0]));
        }

        [Fact]
        public void Dlc_StillShort_IsFlaggedOnScreen()
        {
            Server.Sets[Quest].Add(new FakeTrophy(6, "gold", "001"));
            Server.DefinitionSets[Quest] = [new(1), new(2), new(3, "gold"), new(4), new(5, "silver")];

            var (code, output, error) = RunCommand(new FakePrompt(Partner, Token), CommandArgs());

            Assert.True(code == MemoryImportCommand.Success, error);
            Assert.Contains("sets still short of their trophy total: 1", output, StringComparison.Ordinal);
            Assert.Contains("ATTENTION: 1 set(s) still have fewer definitions", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Retry_SixTimes429_Fails_WithProgressSaved()
        {
            Server.Override = (request, _) =>
                request.RequestUri!.AbsolutePath.Contains($"/npCommunicationIds/{RacerA}/", StringComparison.Ordinal)
                    ? FakePsnServer.Reply(HttpStatusCode.TooManyRequests, "{}")
                    : null;

            var exception = Assert.Throws<InvalidOperationException>(() => Fetch(new FakePrompt(Partner, Token)));

            Assert.Contains("after 6 attempts", exception.Message, StringComparison.Ordinal);
            Assert.Equal(PsnApiClient.MaxAttempts, RequestsTo($"/npCommunicationIds/{RacerA}/").Count);
            Assert.Single(ReadOut(PsnTrophyFetcher.TimestampsFile)["games"]!.AsArray());
        }

        [Fact]
        public void PartnerId_NotDigits_IsRefused_BeforeAnyCall()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => Fetch(new FakePrompt("not-a-number", Token)));

            Assert.Contains("digits", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Server.Requests);
        }

        [Fact]
        public void LocalAccountB_UsesMeForAccountB()
        {
            Server.Titles[Partner] = Server.Titles["me"];
            Server.Titles["me"] = Standard().Titles[Partner];
            var earned = Server.Earned.ToList();
            Server.Earned.Clear();

            foreach (var ((user, set, id), time) in earned)
            {
                Server.Earned[(user == "me" ? Partner : "me", set, id)] = time;
            }

            var (result, _) = Fetch(new FakePrompt(Partner, Token), Options() with { Local = AccountB });

            Assert.True(result.Completed);
            Assert.All(RequestsTo($"/npCommunicationIds/{RacerB}/"), url => Assert.StartsWith("users/me/", url, StringComparison.Ordinal));
            Assert.Equal(4, result.Counts.TrophiesA);
        }

        [Fact]
        public void NormaliseTitle_IsExactAfterCaseSpaceAndTrademark_NotFuzzy()
        {
            Assert.Equal("INVENTED TWIN", PsnTrophyFetcher.NormaliseTitle(" Invented  Twin\u2122 "));
            Assert.Equal(PsnTrophyFetcher.NormaliseTitle("invented twin"), PsnTrophyFetcher.NormaliseTitle("INVENTED\tTWIN"));
            Assert.NotEqual(PsnTrophyFetcher.NormaliseTitle("Invented Twin"), PsnTrophyFetcher.NormaliseTitle("Invented Twins"));
            Assert.NotEqual(PsnTrophyFetcher.NormaliseTitle("Invented Twin"), PsnTrophyFetcher.NormaliseTitle("Invented Twin 2"));
        }

        [Fact]
        public void Command_MissingOptions_IsAUsageError()
        {
            var (code, _, error) = RunCommand(new FakePrompt(Partner, Token), "psn-fetch", "--previous-list", PreviousList);

            Assert.Equal(MemoryImportCommand.UsageError, code);
            Assert.Contains("psn-fetch needs", error, StringComparison.Ordinal);
            Assert.Empty(Server.Requests);
        }

        [Fact]
        public void Command_DefinitionsOptionOutsidePsnFetch_IsAUsageError()
        {
            var (code, _, _) = RunCommand(new FakePrompt(Partner, Token), "psn-build", "--definitions", Definitions);

            Assert.Equal(MemoryImportCommand.UsageError, code);
        }
    }
}
