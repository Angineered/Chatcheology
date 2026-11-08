using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;
using Microsoft.Data.Sqlite;
using static Chatcheology.Tools.MemoryImport.Tests.SyntheticTrophyBatch;

namespace Chatcheology.Tools.MemoryImport.Tests
{
    /// <summary>psn-build and psn-compare on synthetic accounts and games (CH-71 D8).</summary>
    public sealed class PsnBuildCommandTests : IDisposable
    {
        private const string SameSet = "ZZSET00001_00";
        private const string SetOfA = "ZZSET00002_00";
        private const string SetOfB = "ZZSET00003_00";
        private const string OtherSame = "ZZSET00004_00";

        private static readonly DateTime Now = new(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);

        private readonly string _root;

        public PsnBuildCommandTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "Chatcheology.MemoryImport.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        private string[] Secrets => ["ZZSET", "Invented", "alpha", "beta", "zztimestamps", "zzmanifest", "zzbuilt", "https", "example", "Chatcheology.MemoryImport.Tests", _root];

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder must not fail a test.
            }
        }

        private (int Code, string Output, string Error) Run(params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = MemoryImportCommand.Run(args, output, error, () => Now, () => throw new InvalidOperationException("No images here."));
            var (o, e) = (output.ToString(), error.ToString());

            foreach (var secret in Secrets)
            {
                Assert.DoesNotContain(secret, o, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret, e, StringComparison.OrdinalIgnoreCase);
            }

            return (code, o, e);
        }

        private static PsnBuildResult Build(SyntheticTrophyBatch batch) =>
            PsnSharedActivityBuilder.Build(
                [Encoding.UTF8.GetBytes(batch.TimestampsJson())], Encoding.UTF8.GetBytes(batch.ManifestJson()), AccountA, AccountB);

        private static PsnActivity Read(byte[] json) => PsnSharedActivityReader.Parse(json, AccountA, AccountB, AccountA);

        private static Dictionary<string, string> Levels(byte[] json) =>
            Read(json).Records.ToDictionary(record => record.SourceKey, record => record.DayLevel, StringComparer.Ordinal);

        private static JsonNode Event(byte[] json, string id) =>
            JsonNode.Parse(json)!["events"]!.AsArray().Single(e => (string)e!["id"]! == id)!;

        private static SyntheticTrophyBatch OnePair(int secondsApart)
        {
            var batch = new SyntheticTrophyBatch();
            batch.Games.Add(new BatchGame
            {
                SetA = SameSet,
                SetB = SameSet,
                A = [new BatchTrophy(1, Utc(2015, 3, 1, 10, 0, 0))],
                B = [new BatchTrophy(1, Utc(2015, 3, 1, 10, 0, 0).AddSeconds(secondsApart))],
            });
            return batch;
        }

        /// <summary>A three-game batch used for the command line, round trip and compare tests.</summary>
        private static SyntheticTrophyBatch Standard()
        {
            var batch = new SyntheticTrophyBatch();
            batch.Games.Add(new BatchGame
            {
                SetA = SameSet,
                SetB = SameSet,
                Title = "Invented Quest",
                Icon = "https://image.api.playstation.com/zz/example.png",
                A = [new BatchTrophy(1, Utc(2015, 3, 1, 18, 0, 0)), new BatchTrophy(2, Utc(2015, 3, 2, 18, 0, 0), "silver"), new BatchTrophy(3, Utc(2015, 3, 3, 9, 0, 0))],
                B = [new BatchTrophy(1, Utc(2015, 3, 1, 18, 0, 30)), new BatchTrophy(2, Utc(2015, 3, 2, 19, 0, 0), "silver"), new BatchTrophy(4, Utc(2015, 3, 3, 12, 0, 0))],
            });
            batch.Games.Add(new BatchGame
            {
                SetA = SetOfA,
                SetB = SetOfB,
                Title = "Invented Racer",
                A = [new BatchTrophy(5, Utc(2016, 7, 4, 10, 0, 0))],
                B = [new BatchTrophy(5, Utc(2016, 7, 4, 10, 0, 0))],
            });
            batch.Games.Add(new BatchGame
            {
                SetA = OtherSame,
                SetB = OtherSame,
                Title = "Invented Solo",
                A = [new BatchTrophy(1, Utc(2017, 1, 1, 10, 0, 0))],
            });
            return batch;
        }

        [Fact]
        public void Pair_ExactlySixtySecondsApart_IsStrong()
        {
            var built = Build(OnePair(60));

            Assert.Equal("strong", Assert.Single(Levels(built.Json)).Value);
            Assert.Equal(1, built.Counts.Strong);
            Assert.Equal(1, built.Counts.MatchingPairs);

            var pair = Assert.Single(Event(built.Json, $"{SameSet}|{SameSet}|2015-03-01")["matchingTrophies"]!.AsArray())!;
            Assert.Equal(1, (int)pair["id"]!);
            Assert.Equal("12:00:00", (string)pair["aTime"]!);
            Assert.Equal("12:01:00", (string)pair["bTime"]!);
        }

        [Fact]
        public void Pair_SixtySecondsBeforeTheOther_IsStrongToo()
        {
            Assert.Equal("strong", Assert.Single(Levels(Build(OnePair(-60)).Json)).Value);
        }

        [Fact]
        public void Pair_SixtyOneSecondsApart_IsSupporting()
        {
            var built = Build(OnePair(61));

            Assert.Equal("supporting", Assert.Single(Levels(built.Json)).Value);
            Assert.Equal(1, built.Counts.Supporting);
            Assert.Equal(0, built.Counts.Strong);
        }

        [Fact]
        public void OneCloseAndOneFarPair_IsStrong_WithBothPairsListed()
        {
            var batch = OnePair(3600);
            batch.Games[0].A.Add(new BatchTrophy(2, Utc(2015, 3, 1, 14, 0, 0)));
            batch.Games[0].B.Add(new BatchTrophy(2, Utc(2015, 3, 1, 14, 0, 5)));

            var built = Build(batch);

            Assert.Equal("strong", Assert.Single(Levels(built.Json)).Value);
            Assert.Equal(2, built.Counts.MatchingPairs);
        }

        [Fact]
        public void BothEarnedButNoIdenticalId_IsSameDay()
        {
            var batch = OnePair(0);
            batch.Games[0].B[0] = new BatchTrophy(9, Utc(2015, 3, 1, 10, 0, 0));

            var built = Build(batch);

            Assert.Equal("same-day", Assert.Single(Levels(built.Json)).Value);
            Assert.Empty(Event(built.Json, $"{SameSet}|{SameSet}|2015-03-01")["matchingTrophies"]!.AsArray());
        }

        [Fact]
        public void Dates_AreJohannesburgDates_NotUtcDates()
        {
            var batch = new SyntheticTrophyBatch();
            batch.Games.Add(new BatchGame
            {
                SetA = SameSet,
                SetB = SameSet,
                A =
                [
                    new BatchTrophy(1, Utc(2015, 3, 1, 21, 59, 59)), // 23:59:59 on 1 March in Johannesburg
                    new BatchTrophy(3, Utc(2015, 3, 1, 22, 30, 0)),  // 00:30:00 on 2 March in Johannesburg
                ],
                B =
                [
                    new BatchTrophy(1, Utc(2015, 3, 1, 22, 0, 0)),   // 00:00:00 on 2 March: one second later, another date
                    new BatchTrophy(2, Utc(2015, 3, 1, 21, 0, 0)),   // 23:00:00 on 1 March
                    new BatchTrophy(3, Utc(2015, 3, 1, 22, 30, 10)), // 00:30:10 on 2 March
                ],
            });

            var built = Build(batch);
            var levels = Levels(built.Json);

            // 1 March: a's trophy 1 and b's trophy 2; b's trophy 1 is on 2 March, so no pair.
            Assert.Equal("same-day", levels[$"{SameSet}|{SameSet}|2015-03-01"]);

            // 2 March: trophy 3 by both, 10 s apart. Trophy 1 is b's only on this date.
            Assert.Equal("strong", levels[$"{SameSet}|{SameSet}|2015-03-02"]);
            Assert.Equal(2, levels.Count);

            var first = Event(built.Json, $"{SameSet}|{SameSet}|2015-03-01");
            Assert.Equal("23:59:59", (string)first[AccountA + "Trophies"]![0]!["time"]!);
            Assert.Equal("2015-03-01T21:59:59Z", (string)first[AccountA + "Trophies"]![0]!["utc"]!);

            var second = Event(built.Json, $"{SameSet}|{SameSet}|2015-03-02");
            Assert.Single(second["matchingTrophies"]!.AsArray());
            Assert.Equal(2, second[AccountB + "Trophies"]!.AsArray().Count);
        }

        [Fact]
        public void DifferentTrophySets_AreNeverMatched()
        {
            var batch = new SyntheticTrophyBatch();
            batch.Games.Add(new BatchGame
            {
                SetA = SetOfA,
                SetB = SetOfB,
                A = [new BatchTrophy(1, Utc(2015, 3, 1, 10, 0, 0))],
                B = [new BatchTrophy(1, Utc(2015, 3, 1, 10, 0, 0))],
            });

            var built = Build(batch);

            Assert.Equal("same-day", Assert.Single(Levels(built.Json)).Value);
            Assert.Equal(0, built.Counts.MatchingPairs);
            Assert.Equal(1, built.Counts.GamesWithDistinctSets);
            Assert.Empty(Event(built.Json, $"{SetOfA}|{SetOfB}|2015-03-01")["matchingTrophies"]!.AsArray());
        }

        [Fact]
        public void GameWithTrophiesForOneAccountOnly_HasNoEvent()
        {
            var batch = new SyntheticTrophyBatch();
            batch.Games.Add(new BatchGame
            {
                SetA = SameSet,
                SetB = SameSet,
                A = [new BatchTrophy(1, Utc(2015, 3, 1, 10, 0, 0)), new BatchTrophy(2, Utc(2015, 3, 2, 10, 0, 0))],
            });
            batch.Games.Add(new BatchGame { SetA = OtherSame, SetB = OtherSame });

            var built = Build(batch);

            Assert.Empty(Read(built.Json).Records);
            Assert.Equal(2, Read(built.Json).GameCount);
            Assert.Equal(1, built.Counts.GamesOneAccountOnly);
            Assert.Equal(1, built.Counts.GamesWithNoTrophies);
            Assert.Equal(2, built.Counts.OneAccountDates);
        }

        [Fact]
        public void Build_DryRun_PrintsCountsOnly_AndWritesNothing()
        {
            var batch = Standard();
            batch.ManifestOnly.Add(new BatchGame { SetA = "ZZSET00009_00", SetB = "ZZSET00009_00" });
            batch.TimestampsOnly.Add(new BatchGame { SetA = "ZZSET00008_00", SetB = "ZZSET00008_00" });
            var (timestamps, manifest) = batch.WriteTo(_root, "a");

            var (code, output, error) = Run(
                "psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--dry-run");

            Assert.Equal(0, code);
            Assert.Equal(string.Empty, error);
            Assert.Contains("Dry run: nothing is written.", output, StringComparison.Ordinal);
            Assert.Contains("Manifest games: 4", output, StringComparison.Ordinal);
            Assert.Contains("Timestamp games: 4", output, StringComparison.Ordinal);
            Assert.Contains("Games written: 3", output, StringComparison.Ordinal);
            Assert.Contains("  with different trophy sets (never matched): 1", output, StringComparison.Ordinal);
            Assert.Contains("Events: 4", output, StringComparison.Ordinal);
            Assert.Contains("  strong: 1", output, StringComparison.Ordinal);
            Assert.Contains("  supporting: 1", output, StringComparison.Ordinal);
            Assert.Contains("  same-day: 2", output, StringComparison.Ordinal);
            Assert.Contains("Matching pairs: 2", output, StringComparison.Ordinal);
            Assert.Contains("Trophies in events: 8", output, StringComparison.Ordinal);
            Assert.Contains("  manifest games with no timestamps: 1", output, StringComparison.Ordinal);
            Assert.Contains("  timestamp games not in the manifest: 1", output, StringComparison.Ordinal);
            Assert.Contains("  games with trophies for one account only: 1", output, StringComparison.Ordinal);
            Assert.Contains("  from the manifest: 1", output, StringComparison.Ordinal);
            Assert.Contains("  none: 2", output, StringComparison.Ordinal);
            Assert.Equal(2, Directory.GetFiles(_root).Length);
        }

        [Fact]
        public void Build_RoundTrip_ReaderParses_AndPsnImportAccepts()
        {
            var (timestamps, manifest) = Standard().WriteTo(_root, "a");
            var built = Path.Combine(_root, "zzbuilt.json");

            var (code, output, _) = Run(
                "psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--out", built);

            Assert.Equal(0, code);
            Assert.Contains("Built.", output, StringComparison.Ordinal);

            var bytes = File.ReadAllBytes(built);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "No byte order mark.");

            var activity = PsnSharedActivityReader.Read(built, AccountA, AccountB, AccountA);
            Assert.Equal(3, activity.GameCount);
            Assert.Equal(4, activity.Records.Count);
            Assert.All(activity.Records, record => Assert.Equal(0, record.PairsNotLocated));

            var quest = activity.Records.Single(record => record.SourceKey == $"{SameSet}|{SameSet}|2015-03-01");
            Assert.Equal("Invented Quest", quest.Title);
            Assert.Equal("https://image.api.playstation.com/zz/example.png", quest.ImageSourceUrl);

            // The other account as side a of the reader's roles still reads (side a is fixed by the key).
            Assert.Equal(4, PsnSharedActivityReader.Read(built, AccountB, AccountA, AccountA).Records.Count);

            var workspace = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(workspace);

            (code, output, _) = Run("psn-import", built, "--workspace-folder", workspace, "--local", AccountA, "--other", AccountB, "--pair-a", AccountA, "--create");

            Assert.Equal(0, code);
            Assert.Contains("  Records added: 4", output, StringComparison.Ordinal);
            Assert.Contains("  strong: 1", output, StringComparison.Ordinal);
            Assert.Contains("  not located: 0", output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(workspace, MemoriesDatabase.FileName)));

            // A rebuild of the same inputs is byte for byte the same, and re-importing it changes nothing.
            var again = Path.Combine(_root, "zzbuilt2.json");
            Run("psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--out", again);
            Assert.Equal(bytes, File.ReadAllBytes(again));

            (code, output, _) = Run("psn-import", again, "--workspace-folder", workspace, "--local", AccountA, "--other", AccountB, "--pair-a", AccountA);

            Assert.Equal(0, code);
            Assert.Contains("  Changes: 0", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Build_ExistingOutput_IsRefusedWithoutOverwrite_AndReplacedWithIt()
        {
            var (timestamps, manifest) = Standard().WriteTo(_root, "a");
            var built = Path.Combine(_root, "zzbuilt.json");
            File.WriteAllText(built, "keep");

            var (code, _, error) = Run(
                "psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--out", built);

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("--overwrite", error, StringComparison.Ordinal);
            Assert.Equal("keep", File.ReadAllText(built));

            (code, _, _) = Run(
                "psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--out", built, "--overwrite");

            Assert.Equal(0, code);
            Assert.Equal(4, PsnSharedActivityReader.Read(built, AccountA, AccountB, AccountA).Records.Count);
            Assert.Equal(3, Directory.GetFiles(_root).Length);
        }

        [Fact]
        public void Build_OutputNamingAnInput_IsRefused()
        {
            var (timestamps, manifest) = Standard().WriteTo(_root, "a");
            var before = File.ReadAllBytes(manifest);

            var (code, _, error) = Run(
                "psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--out", manifest, "--overwrite");

            Assert.Equal(MemoryImportCommand.UsageError, code);
            Assert.Contains("input", error, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllBytes(manifest));
        }

        [Fact]
        public void Build_IconsFrom_CarriesOverIconsTheManifestLacks()
        {
            var (timestamps, manifest) = Standard().WriteTo(_root, "a");
            var old = Path.Combine(_root, "zzbuilt-old.json");
            var oldRoot = new JsonObject
            {
                ["schema"] = PsnSharedActivityReader.Schema,
                ["games"] = new JsonArray(
                    new JsonObject { ["key"] = $"{SetOfA}|{SetOfB}", ["icon"] = "https://psnobj.prod.dl.playstation.net/zz/example2.png" },
                    new JsonObject { ["key"] = $"{SameSet}|{SameSet}", ["icon"] = "https://image.api.playstation.com/zz/other.png" }),
            };
            File.WriteAllText(old, oldRoot.ToJsonString());
            var built = Path.Combine(_root, "zzbuilt.json");

            var (code, output, _) = Run(
                "psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB,
                "--out", built, "--icons-from", old);

            Assert.Equal(0, code);
            Assert.Contains("  from the manifest: 1", output, StringComparison.Ordinal);
            Assert.Contains("  from the existing file: 1", output, StringComparison.Ordinal);
            Assert.Contains("  none: 1", output, StringComparison.Ordinal);

            var records = PsnSharedActivityReader.Read(built, AccountA, AccountB, AccountA).Records;
            Assert.Equal("https://psnobj.prod.dl.playstation.net/zz/example2.png", records.Single(r => r.SourceKey.StartsWith(SetOfA, StringComparison.Ordinal)).ImageSourceUrl);
            Assert.Equal("https://image.api.playstation.com/zz/example.png", records.First(r => r.SourceKey.StartsWith(SameSet, StringComparison.Ordinal)).ImageSourceUrl);
        }

        [Fact]
        public void Build_SeveralTimestampFiles_AreCombined_AndARepeatedKeyIsRefused()
        {
            var first = new SyntheticTrophyBatch();
            first.Games.Add(Standard().Games[0]);
            var second = new SyntheticTrophyBatch();
            second.Games.Add(Standard().Games[1]);
            var (t1, _) = first.WriteTo(_root, "a");
            var (t2, _) = second.WriteTo(_root, "b");
            var manifest = Path.Combine(_root, "c-zzmanifest.json");
            var both = new SyntheticTrophyBatch();
            both.Games.AddRange(Standard().Games.Take(2));
            File.WriteAllText(manifest, both.ManifestJson());

            var (code, output, _) = Run(
                "psn-build", "--timestamps", t1, "--timestamps", t2, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--dry-run");

            Assert.Equal(0, code);
            Assert.Contains("Games written: 2", output, StringComparison.Ordinal);
            Assert.Contains("Events: 4", output, StringComparison.Ordinal);

            string error;
            (code, _, error) = Run(
                "psn-build", "--timestamps", t1, "--timestamps", t1, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--dry-run");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("Timestamp file 2, game at index 0 repeats", error, StringComparison.Ordinal);
        }

        public static TheoryData<string, string> BadInputs => new()
        {
            { "format", "is not format" },
            { "utcflag", "does not say its timestamps are UTC" },
            { "nogames", "has no games array" },
            { "notrophies", "game at index 0 has no trophies array for account b" },
            { "badtime", "account a trophy at index 0: earnedDateTimeUTC is not" },
            { "offsettime", "account a trophy at index 0: earnedDateTimeUTC is not" },
            { "stringid", "account a trophy at index 0 has no whole-number trophyId" },
            { "notype", "account b trophy at index 0 has no trophyType" },
            { "repeatid", "account a trophy at index 1 repeats an earlier trophyId" },
            { "earnedcount", "account a's earnedCount does not equal its trophies" },
            { "swapped", "Check --account-a" },
            { "manifestrepeat", "Manifest game at index 1 repeats" },
            { "manifestsame", "sameTrophySet disagrees" },
            { "manifestnoset", "Manifest game at index 0 has no trophy set (npCommunicationId) for account b" },
            { "manifesticon", "Manifest game at index 0 has an icon that is not text or null" },
            { "manifestnotitle", "Manifest game at index 0 has no game title" },
            { "manifestnotjson", "The manifest is not valid JSON" },
            { "casetwice", "two fields whose names differ only by case" },
        };

        [Theory]
        [MemberData(nameof(BadInputs))]
        public void Build_BadInput_StopsWithIndexesOnly_AndWritesNothing(string kind, string expected)
        {
            var batch = OnePair(0);
            batch.Games[0].A.Add(new BatchTrophy(2, Utc(2015, 3, 1, 11, 0, 0)));
            JsonObject FirstA(JsonObject root) => root["games"]![0]![AccountA]!.AsObject();
            JsonObject FirstTrophy(JsonObject root, string account) => root["games"]![0]![account]!["trophies"]![0]!.AsObject();
            string? manifestText = null;

            switch (kind)
            {
                case "format": batch.TamperTimestamps = root => root["format"] = "something-else"; break;
                case "utcflag": batch.TamperTimestamps = root => root["timestampsAreUTC"] = false; break;
                case "nogames": batch.TamperTimestamps = root => root.Remove("games"); break;
                case "notrophies": batch.TamperTimestamps = root => root["games"]![0]![AccountB]!.AsObject().Remove("trophies"); break;
                case "badtime": batch.TamperTimestamps = root => FirstTrophy(root, AccountA)["earnedDateTimeUTC"] = "2015-03-01 10:00:00"; break;
                case "offsettime": batch.TamperTimestamps = root => FirstTrophy(root, AccountA)["earnedDateTimeUTC"] = "2015-03-01T12:00:00+02:00"; break;
                case "stringid": batch.TamperTimestamps = root => FirstTrophy(root, AccountA)["trophyId"] = "1"; break;
                case "notype": batch.TamperTimestamps = root => FirstTrophy(root, AccountB).Remove("trophyType"); break;
                case "repeatid": batch.TamperTimestamps = root => FirstA(root)["trophies"]![1]!["trophyId"] = 1; break;
                case "earnedcount": batch.TamperTimestamps = root => FirstA(root)["earnedCount"] = 7; break;
                case "swapped":
                    batch.Games[0] = new BatchGame { SetA = SetOfA, SetB = SetOfB, A = batch.Games[0].A, B = batch.Games[0].B };
                    batch.TamperManifest = root => root["games"]![0]!["key"] = $"{SetOfB}|{SetOfA}";
                    break;
                case "manifestrepeat": batch.TamperManifest = root => root["games"]!.AsArray().Add(root["games"]![0]!.DeepClone()); break;
                case "manifestsame": batch.TamperManifest = root => root["games"]![0]!["sameTrophySet"] = false; break;
                case "manifestnoset": batch.TamperManifest = root => root["games"]![0]![AccountB]!.AsObject().Remove("npCommunicationId"); break;
                case "manifesticon": batch.TamperManifest = root => root["games"]![0]!["icon"] = 5; break;
                case "manifestnotitle": batch.TamperManifest = root => root["games"]![0]!.AsObject().Remove("game"); break;
                case "manifestnotjson": manifestText = "{ not json"; break;
                case "casetwice": batch.TamperManifest = root => root["games"]![0]!["KEY"] = "x"; break;
            }

            var (timestamps, manifest) = batch.WriteTo(_root, "a");

            if (manifestText is not null)
            {
                File.WriteAllText(manifest, manifestText);
            }

            var built = Path.Combine(_root, "zzbuilt.json");

            var (code, output, error) = Run(
                "psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--out", built);

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Equal(string.Empty, output);
            Assert.Contains(expected, error, StringComparison.Ordinal);
            Assert.False(File.Exists(built));
        }

        [Theory]
        [InlineData("psn-build", "--manifest", "m", "--account-a", "x", "--account-b", "y", "--dry-run")]
        [InlineData("psn-build", "--timestamps", "t", "--account-a", "x", "--account-b", "y", "--dry-run")]
        [InlineData("psn-build", "--timestamps", "t", "--manifest", "m", "--account-b", "y", "--dry-run")]
        [InlineData("psn-build", "--timestamps", "t", "--manifest", "m", "--account-a", "x", "--account-b", "y")]
        [InlineData("psn-build", "--timestamps", "t", "--manifest", "m", "--account-a", "x", "--account-b", "y", "--dry-run", "--out", "o")]
        [InlineData("psn-build", "--timestamps", "t", "--manifest", "m", "--account-a", "x", "--account-b", "y", "--dry-run", "--overwrite")]
        [InlineData("psn-build", "--timestamps", "t", "--manifest", "m", "--account-a", "x", "--account-b", "y", "--dry-run", "--local", "x")]
        [InlineData("psn-build", "positional", "--timestamps", "t", "--manifest", "m", "--account-a", "x", "--account-b", "y", "--dry-run")]
        [InlineData("psn-dry-run", "f", "--local", "x", "--other", "y", "--pair-a", "x", "--manifest", "m")]
        [InlineData("psn-import", "f", "--local", "x", "--other", "y", "--pair-a", "x", "--dry-run")]
        [InlineData("psn-compare", "f", "--local", "x", "--other", "y", "--pair-a", "x")]
        [InlineData("psn-compare", "f", "g", "h", "--local", "x", "--other", "y", "--pair-a", "x")]
        [InlineData("psn-compare", "f", "g", "--local", "x", "--other", "y", "--pair-a", "x", "--workspace-folder", "w")]
        public void UsageErrors_AreRefused(params string[] args)
        {
            var (code, output, error) = Run(args);

            Assert.Equal(MemoryImportCommand.UsageError, code);
            Assert.Equal(string.Empty, output);
            Assert.StartsWith("Usage error:", error, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("alpha", "ALPHA")]
        [InlineData("al-pha", "beta")]
        public void Build_BadAccountNames_AreRefused(string a, string b)
        {
            var (timestamps, manifest) = Standard().WriteTo(_root, "a");

            var (code, _, error) = Run("psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", a, "--account-b", b, "--dry-run");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("account", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Build_MissingInput_PrintsTypeOnly()
        {
            var (_, manifest) = Standard().WriteTo(_root, "a");

            var (code, _, error) = Run(
                "psn-build", "--timestamps", Path.Combine(_root, "missing-zztimestamps.json"), "--manifest", manifest,
                "--account-a", AccountA, "--account-b", AccountB, "--dry-run");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("FileNotFoundException", error, StringComparison.Ordinal);
        }

        [Fact]
        public void Compare_SameFile_IsSame_ExitZero()
        {
            var (timestamps, manifest) = Standard().WriteTo(_root, "a");
            var built = Path.Combine(_root, "zzbuilt.json");
            Run("psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--out", built);

            var (code, output, error) = Run("psn-compare", built, built, "--local", AccountA, "--other", AccountB, "--pair-a", AccountA);

            Assert.Equal(0, code);
            Assert.Equal(string.Empty, error);
            Assert.Contains("Old file: games 3, events 4", output, StringComparison.Ordinal);
            Assert.Contains("In both, same level: 4", output, StringComparison.Ordinal);
            Assert.Contains("In both, different level: 0", output, StringComparison.Ordinal);
            Assert.Contains("Only in old: 0", output, StringComparison.Ordinal);
            Assert.Contains("Only in new: 0", output, StringComparison.Ordinal);
            Assert.Contains("Result: same.", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Compare_Differences_AreCountedPerLevel_ExitThree()
        {
            var (t1, m1) = Standard().WriteTo(_root, "a");
            var oldFile = Path.Combine(_root, "zzbuilt-old.json");
            Run("psn-build", "--timestamps", t1, "--manifest", m1, "--account-a", AccountA, "--account-b", AccountB, "--out", oldFile);

            // New: 1 March's pair now 61 s apart (strong -> supporting), 3 March gone (b's trophy
            // removed), and a new same-day date for the racer. 2 March (supporting) is unchanged.
            var changed = Standard();
            changed.Games[0].B[0] = new BatchTrophy(1, Utc(2015, 3, 1, 18, 1, 1));
            changed.Games[0].B.RemoveAt(2);
            changed.Games[1].A.Add(new BatchTrophy(6, Utc(2016, 7, 5, 10, 0, 0)));
            changed.Games[1].B.Add(new BatchTrophy(7, Utc(2016, 7, 5, 11, 0, 0)));
            var (t2, m2) = changed.WriteTo(_root, "b");
            var newFile = Path.Combine(_root, "zzbuilt-new.json");
            Run("psn-build", "--timestamps", t2, "--manifest", m2, "--account-a", AccountA, "--account-b", AccountB, "--out", newFile);

            var (code, output, error) = Run("psn-compare", oldFile, newFile, "--local", AccountA, "--other", AccountB, "--pair-a", AccountA);

            Assert.Equal(MemoryImportCommand.Different, code);
            Assert.Equal(string.Empty, error);
            Assert.Contains("Old file: games 3, events 4", output, StringComparison.Ordinal);
            Assert.Contains("New file: games 3, events 4", output, StringComparison.Ordinal);
            Assert.Contains("In both, same level: 2\n  strong: 0\n  supporting: 1\n  same-day: 1", Normalise(output), StringComparison.Ordinal);
            Assert.Contains("In both, different level: 1", output, StringComparison.Ordinal);
            Assert.Contains("  strong -> supporting: 1", output, StringComparison.Ordinal);
            Assert.Contains("  supporting -> strong: 0", output, StringComparison.Ordinal);
            Assert.Contains("Only in old: 1\n  strong: 0\n  supporting: 0\n  same-day: 1", Normalise(output), StringComparison.Ordinal);
            Assert.Contains("Only in new: 1\n  strong: 0\n  supporting: 0\n  same-day: 1", Normalise(output), StringComparison.Ordinal);
            Assert.Contains("In both, same level, different trophies or pairs: 0", output, StringComparison.Ordinal);
            Assert.Contains("Result: different.", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Compare_SameLevelButOtherTrophiesOrIcon_IsDifferent()
        {
            var (t1, m1) = Standard().WriteTo(_root, "a");
            var oldFile = Path.Combine(_root, "zzbuilt-old.json");
            Run("psn-build", "--timestamps", t1, "--manifest", m1, "--account-a", AccountA, "--account-b", AccountB, "--out", oldFile);

            // 2 March: b's trophy one second later, still supporting; the quest's icon is gone.
            var changed = Standard();
            changed.Games[0].B[1] = new BatchTrophy(2, Utc(2015, 3, 2, 19, 0, 1), "silver");
            changed.Games[0] = new BatchGame { SetA = SameSet, SetB = SameSet, Title = "Invented Quest", A = changed.Games[0].A, B = changed.Games[0].B };
            var (t2, m2) = changed.WriteTo(_root, "b");
            var newFile = Path.Combine(_root, "zzbuilt-new.json");
            Run("psn-build", "--timestamps", t2, "--manifest", m2, "--account-a", AccountA, "--account-b", AccountB, "--out", newFile);

            var (code, output, _) = Run("psn-compare", oldFile, newFile, "--local", AccountA, "--other", AccountB, "--pair-a", AccountA);

            Assert.Equal(MemoryImportCommand.Different, code);
            Assert.Contains("In both, same level: 4", output, StringComparison.Ordinal);
            Assert.Contains("In both, same level, different trophies or pairs: 1", output, StringComparison.Ordinal);
            Assert.Contains("In both, different game icon: 3", output, StringComparison.Ordinal);
            Assert.Contains("In both, different game title: 0", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Compare_BadFile_StopsWithIndexOnly()
        {
            var (timestamps, manifest) = Standard().WriteTo(_root, "a");
            var built = Path.Combine(_root, "zzbuilt.json");
            Run("psn-build", "--timestamps", timestamps, "--manifest", manifest, "--account-a", AccountA, "--account-b", AccountB, "--out", built);
            var broken = Path.Combine(_root, "zzbuilt-broken.json");
            var root = JsonNode.Parse(File.ReadAllText(built))!;
            root["events"]![1]!["quality"] = "certain";
            File.WriteAllText(broken, root.ToJsonString());

            var (code, _, error) = Run("psn-compare", built, broken, "--local", AccountA, "--other", AccountB, "--pair-a", AccountA);

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("Event at index 1", error, StringComparison.Ordinal);
        }

        private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
