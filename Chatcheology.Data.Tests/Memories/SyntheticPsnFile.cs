using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Chatcheology.Tests.Memories
{
    /// <summary>One synthetic trophy. Utc is the earned time; Time overrides the local text.</summary>
    internal sealed record SyntheticTrophy(string Id, string Type, DateTime Utc, string? Time = null)
    {
        /// <summary>Synthetic files use a +02:00 zone without daylight saving.</summary>
        internal static readonly TimeSpan Offset = TimeSpan.FromHours(2);

        internal string LocalText => Time ?? (Utc + Offset).ToString("yyyy'-'MM'-'dd' 'HH':'mm':'ss", CultureInfo.InvariantCulture);
    }

    /// <summary>A synthetic matching pair (local trophy, other trophy).</summary>
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

        internal string Id => $"{Game.LocalSet}|{Game.OtherSet}|{Date}";
    }

    /// <summary>Builds a synthetic PSN shared-activity file. Every value is invented.</summary>
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

        /// <summary>Pairs written as nested objects named after the prefix instead of flat fields.</summary>
        internal bool NestedPairs { get; set; }

        /// <summary>Called on the root just before serialising, to break the file on purpose.</summary>
        internal Action<JsonObject>? Tamper { get; set; }

        internal static DateTime Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
            new(year, month, day, hour, minute, second, DateTimeKind.Utc);

        internal static SyntheticTrophy Trophy(string id, DateTime utc, string type = "bronze") => new(id, type, utc);

        internal string ToJson()
        {
            var games = new JsonArray();

            foreach (var game in Games)
            {
                games.Add(new JsonObject
                {
                    ["key"] = $"{game.LocalSet}|{game.OtherSet}",
                    ["title"] = game.Title,
                    [LocalPrefix + "TrophySet"] = game.LocalSet,
                    [OtherPrefix + "TrophySet"] = game.OtherSet,
                    ["platformAlpha"] = "PS4",
                    ["platformBeta"] = "PS4",
                    ["reviewStatus"] = "together",
                    ["icon"] = game.Icon,
                });
            }

            var events = new JsonArray();

            foreach (var e in Events)
            {
                var pairs = new JsonArray();

                foreach (var pair in e.Pairs)
                {
                    var seconds = Math.Abs((pair.Local.Utc - pair.Other.Utc).TotalSeconds);

                    if (NestedPairs)
                    {
                        pairs.Add(new JsonObject
                        {
                            [LocalPrefix] = new JsonObject { ["id"] = pair.Local.Id, ["time"] = pair.Local.LocalText },
                            [OtherPrefix] = new JsonObject { ["id"] = pair.Other.Id, ["time"] = pair.Other.LocalText },
                            ["secondsApart"] = seconds,
                        });
                    }
                    else
                    {
                        pairs.Add(new JsonObject
                        {
                            [LocalPrefix + "TrophyId"] = pair.Local.Id,
                            [LocalPrefix + "Time"] = pair.Local.LocalText,
                            [OtherPrefix + "TrophyId"] = pair.Other.Id,
                            [OtherPrefix + "Time"] = pair.Other.LocalText,
                            ["secondsApart"] = seconds,
                        });
                    }
                }

                events.Add(new JsonObject
                {
                    ["id"] = e.Id,
                    ["date"] = e.Date,
                    ["title"] = e.Game.Title,
                    ["quality"] = e.Quality,
                    ["firstAlpha"] = e.Local.Count == 0 ? null : e.Local.Min(t => t.LocalText),
                    ["firstBeta"] = e.Other.Count == 0 ? null : e.Other.Min(t => t.LocalText),
                    [LocalPrefix + "Trophies"] = TrophyArray(e.Local),
                    [OtherPrefix + "Trophies"] = TrophyArray(e.Other),
                    ["matchingTrophies"] = pairs,
                });
            }

            var root = new JsonObject
            {
                ["schema"] = Schema,
                ["timezone"] = TimeZone,
                ["games"] = games,
                ["events"] = events,
            };

            Tamper?.Invoke(root);
            return root.ToJsonString();
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
                    ["id"] = int.TryParse(trophy.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                        ? JsonValue.Create(number)
                        : JsonValue.Create(trophy.Id),
                    ["type"] = trophy.Type,
                    ["utc"] = trophy.Utc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture),
                    ["time"] = trophy.LocalText,
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
            var b2 = Trophy("3", Utc(2015, 3, 1, 21, 10, 0));

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
