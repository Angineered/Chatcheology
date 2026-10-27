using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Chatcheology.Tests.Memories
{
    /// <summary>One synthetic trophy. Utc is the earned time; Time overrides the local HH:mm:ss text.</summary>
    internal sealed record SyntheticTrophy(string Id, string Type, DateTime Utc, string? Time = null)
    {
        /// <summary>Synthetic files use Africa/Johannesburg: +02:00, no daylight saving.</summary>
        internal static readonly TimeSpan Offset = TimeSpan.FromHours(2);

        internal string LocalText => Time ?? (Utc + Offset).ToString("HH':'mm':'ss", CultureInfo.InvariantCulture);

        internal int IdNumber => int.Parse(Id, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>A synthetic matching pair (local trophy, other trophy). The pair id is the local trophy's.</summary>
    internal sealed record SyntheticPair(SyntheticTrophy Local, SyntheticTrophy Other);

    /// <summary>A synthetic game: invented title and set IDs.</summary>
    internal sealed record SyntheticGame(string LocalSet, string OtherSet, string Title, string? Icon);

    /// <summary>A synthetic event (game-day record).</summary>
    internal sealed class SyntheticEvent
    {
        internal required SyntheticGame Game { get; init; }

        internal string Date { get; init; } = "2015-03-01";

        internal string Quality { get; init; } = "strong";

        internal List<SyntheticTrophy> Local { get; init; } = [];

        internal List<SyntheticTrophy> Other { get; init; } = [];

        internal List<SyntheticPair> Pairs { get; init; } = [];
    }

    /// <summary>
    /// Builds a synthetic PSN shared-activity file in the exact source shape. Every value is invented.
    /// By default the local account (<see cref="LocalPrefix"/>) is side a: its set is first in the key.
    /// </summary>
    internal sealed class SyntheticPsnFile
    {
        internal const string LocalPrefix = "alpha";
        internal const string OtherPrefix = "beta";

        internal static readonly SyntheticGame Quest =
            new("NPWR99999_00", "NPWR99999_00", "Example Quest", "https://image.api.playstation.com/zz/examplequest.png");

        internal static readonly SyntheticGame Racer =
            new("NPWR88888_00", "NPWR77777_00", "Sample Racer", "https://psnobj.prod.dl.playstation.net/zz/sampleracer.png");

        internal string Schema { get; set; } = "chatcheology-psn-shared-activity-v1";

        internal string TimeZone { get; set; } = "Africa/Johannesburg";

        internal List<SyntheticGame> Games { get; } = [];

        internal List<SyntheticEvent> Events { get; } = [];

        /// <summary>False makes the other account (beta) side a: first in keys, aTime from its trophy.</summary>
        internal bool LocalIsSideA { get; set; } = true;

        /// <summary>The prefix to pass as --pair-a for this file.</summary>
        internal string PairAPrefix => LocalIsSideA ? LocalPrefix : OtherPrefix;

        /// <summary>Called on the root just before serialising, to break the file on purpose.</summary>
        internal Action<JsonObject>? Tamper { get; set; }

        internal static DateTime Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
            new(year, month, day, hour, minute, second, DateTimeKind.Utc);

        internal static SyntheticTrophy Trophy(string id, DateTime utc, string type = "bronze") => new(id, type, utc);

        internal string KeyOf(SyntheticGame game) =>
            LocalIsSideA ? $"{game.LocalSet}|{game.OtherSet}" : $"{game.OtherSet}|{game.LocalSet}";

        internal string IdOf(SyntheticEvent e) => $"{KeyOf(e.Game)}|{e.Date}";

        private static string Capitalised(string prefix) => char.ToUpperInvariant(prefix[0]) + prefix[1..];

        internal string ToJson()
        {
            var games = new JsonArray();

            foreach (var game in Games)
            {
                games.Add(new JsonObject
                {
                    ["key"] = KeyOf(game),
                    ["game"] = game.Title,
                    ["icon"] = game.Icon,
                    ["sameTrophySet"] = game.LocalSet == game.OtherSet,
                    ["platform" + Capitalised(LocalPrefix)] = "PS4",
                    ["platform" + Capitalised(OtherPrefix)] = "PS4",
                    [LocalPrefix + "TrophySet"] = game.LocalSet,
                    [OtherPrefix + "TrophySet"] = game.OtherSet,
                    [LocalPrefix + "Earned"] = 10,
                    [OtherPrefix + "Earned"] = 10,
                    ["sharedDates"] = 1,
                    ["strongDates"] = 1,
                    ["supportingDates"] = 0,
                    ["matchedTrophyIds"] = 1,
                    ["matchedWithin60s"] = 1,
                    ["matchedWithin10s"] = 0,
                    ["matchingExactSeconds"] = 0,
                    ["firstSharedDate"] = "2015-03-01",
                    ["lastSharedDate"] = "2015-03-01",
                    ["note"] = "invented",
                    ["reviewStatus"] = "together",
                });
            }

            var events = new JsonArray();

            foreach (var e in Events)
            {
                var pairs = new JsonArray();

                foreach (var pair in e.Pairs)
                {
                    var (a, b) = LocalIsSideA ? (pair.Local, pair.Other) : (pair.Other, pair.Local);

                    pairs.Add(new JsonObject
                    {
                        ["id"] = pair.Local.IdNumber,
                        ["type"] = pair.Local.Type,
                        ["aTime"] = a.LocalText,
                        ["bTime"] = b.LocalText,
                        ["seconds"] = (int)(a.Utc - b.Utc).TotalSeconds,
                    });
                }

                var date = DateOnly.ParseExact(e.Date, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);
                var ev = new JsonObject
                {
                    ["id"] = IdOf(e),
                    ["gameKey"] = KeyOf(e.Game),
                    ["title"] = e.Game.Title,
                    ["date"] = e.Date,
                    ["year"] = date.Year,
                    ["quality"] = e.Quality,
                    [LocalPrefix + "Count"] = e.Local.Count,
                    [OtherPrefix + "Count"] = e.Other.Count,
                    ["sameTrophyCount"] = e.Pairs.Count,
                    ["close60Count"] = 0,
                    ["close10Count"] = 0,
                    ["exactCount"] = 0,
                    ["closestSeconds"] = 0,
                };

                AddFirstLast(ev, LocalPrefix, e.Local);
                AddFirstLast(ev, OtherPrefix, e.Other);
                ev[LocalPrefix + "Trophies"] = TrophyArray(e.Local);
                ev[OtherPrefix + "Trophies"] = TrophyArray(e.Other);
                ev["matchingTrophies"] = pairs;
                events.Add(ev);
            }

            var root = new JsonObject
            {
                ["schema"] = Schema,
                ["timezone"] = TimeZone,
                ["sources"] = new JsonObject { ["note"] = "invented" },
                ["rules"] = new JsonObject { ["note"] = "invented" },
                ["summary"] = new JsonObject { ["games"] = Games.Count },
                ["yearlyGameDateCounts"] = new JsonObject(),
                ["games"] = games,
                ["events"] = events,
            };

            Tamper?.Invoke(root);
            return root.ToJsonString();
        }

        private static void AddFirstLast(JsonObject ev, string prefix, List<SyntheticTrophy> trophies)
        {
            ev["first" + Capitalised(prefix)] = trophies.Count == 0 ? null : trophies.Min(t => t.LocalText);
            ev["last" + Capitalised(prefix)] = trophies.Count == 0 ? null : trophies.Max(t => t.LocalText);
        }

        internal byte[] ToBytes() => Encoding.UTF8.GetBytes(ToJson());

        internal string WriteTo(string folder, string fileName = "shared-activity.json")
        {
            var path = Path.Combine(folder, fileName);
            File.WriteAllText(path, ToJson());
            return path;
        }

        private static JsonArray TrophyArray(List<SyntheticTrophy> trophies)
        {
            var array = new JsonArray();

            foreach (var trophy in trophies)
            {
                array.Add(new JsonObject
                {
                    ["id"] = trophy.IdNumber,
                    ["type"] = trophy.Type,
                    ["time"] = trophy.LocalText,
                    ["utc"] = trophy.Utc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture),
                });
            }

            return array;
        }

        /// <summary>
        /// The standard two-game file: a strong-day Quest event split into a strong period and a
        /// later period with no close pair, plus a different-set Racer event with no pairs.
        /// </summary>
        internal static SyntheticPsnFile Standard()
        {
            var file = new SyntheticPsnFile();
            file.Games.Add(Quest);
            file.Games.Add(Racer);

            var a1 = Trophy("1", Utc(2015, 3, 1, 18, 0, 0));
            var b1 = Trophy("1", Utc(2015, 3, 1, 18, 0, 30));
            var a2 = Trophy("2", Utc(2015, 3, 1, 21, 0, 0), "silver");
            var b2 = Trophy("2", Utc(2015, 3, 1, 21, 10, 0), "silver");

            file.Events.Add(new SyntheticEvent
            {
                Game = Quest,
                Date = "2015-03-01",
                Quality = "strong",
                Local = [a1, a2],
                Other = [b1, b2],
                Pairs = [new SyntheticPair(a1, b1), new SyntheticPair(a2, b2)],
            });

            file.Events.Add(new SyntheticEvent
            {
                Game = Racer,
                Date = "2016-07-04",
                Quality = "same-day",
                Local = [Trophy("5", Utc(2016, 7, 4, 10, 0, 0))],
                Other = [Trophy("6", Utc(2016, 7, 4, 15, 0, 0))],
            });

            return file;
        }
    }
}
