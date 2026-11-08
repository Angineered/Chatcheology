using System.Globalization;
using System.Text.Json.Nodes;

namespace Chatcheology.Tools.MemoryImport.Tests
{
    /// <summary>One synthetic earned trophy in the fetch script's shape.</summary>
    internal sealed record BatchTrophy(long Id, DateTime Utc, string Type = "bronze");

    /// <summary>A synthetic game: invented title and trophy set IDs, and each account's earned trophies.</summary>
    internal sealed class BatchGame
    {
        internal required string SetA { get; init; }

        internal required string SetB { get; init; }

        internal string Title { get; init; } = "Invented Game";

        internal string? Icon { get; init; }

        internal List<BatchTrophy> A { get; init; } = [];

        internal List<BatchTrophy> B { get; init; } = [];

        internal string Key => $"{SetA}|{SetB}";
    }

    /// <summary>
    /// Builds a synthetic fetch-script output (<c>chatcheology-psn-trophy-batch-v1</c>) and the matching
    /// hand-kept manifest, in the shapes the fetch script writes and reads. Every value is invented.
    /// Account a is <see cref="AccountA"/> (its set first in every key).
    /// </summary>
    internal sealed class SyntheticTrophyBatch
    {
        internal const string AccountA = "alpha";
        internal const string AccountB = "beta";

        internal List<BatchGame> Games { get; } = [];

        /// <summary>Games only in the manifest (no timestamps).</summary>
        internal List<BatchGame> ManifestOnly { get; } = [];

        /// <summary>Games only in the timestamps (not in the manifest).</summary>
        internal List<BatchGame> TimestampsOnly { get; } = [];

        internal Action<JsonObject>? TamperTimestamps { get; set; }

        internal Action<JsonObject>? TamperManifest { get; set; }

        internal static DateTime Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
            new(year, month, day, hour, minute, second, DateTimeKind.Utc);

        internal string TimestampsJson()
        {
            var games = new JsonArray();

            foreach (var game in Games.Concat(TimestampsOnly))
            {
                games.Add(new JsonObject
                {
                    ["key"] = game.Key,
                    ["game"] = game.Title,
                    ["sameTrophySet"] = game.SetA == game.SetB,
                    [AccountA] = Set(game.SetA, game.A),
                    [AccountB] = Set(game.SetB, game.B),
                });
            }

            var root = new JsonObject
            {
                ["format"] = "chatcheology-psn-trophy-batch-v1",
                ["sourceAccounts"] = new JsonArray("invented1", "invented2"),
                ["timestampsAreUTC"] = true,
                ["note"] = "invented",
                ["games"] = games,
            };

            TamperTimestamps?.Invoke(root);
            return root.ToJsonString();
        }

        internal string ManifestJson()
        {
            var games = new JsonArray();

            foreach (var game in Games.Concat(ManifestOnly))
            {
                var entry = new JsonObject
                {
                    ["key"] = game.Key,
                    ["game"] = game.Title,
                    ["sameTrophySet"] = game.SetA == game.SetB,
                    [AccountA] = new JsonObject { ["npCommunicationId"] = game.SetA, ["npServiceName"] = "trophy" },
                    [AccountB] = new JsonObject { ["npCommunicationId"] = game.SetB, ["npServiceName"] = "trophy" },
                };

                if (game.Icon is not null)
                {
                    entry["icon"] = game.Icon;
                }

                games.Add(entry);
            }

            var root = new JsonObject { ["note"] = "invented", ["games"] = games };
            TamperManifest?.Invoke(root);
            return root.ToJsonString();
        }

        internal (string Timestamps, string Manifest) WriteTo(string folder, string stem)
        {
            var timestamps = Path.Combine(folder, stem + "-zztimestamps.json");
            var manifest = Path.Combine(folder, stem + "-zzmanifest.json");
            File.WriteAllText(timestamps, TimestampsJson());
            File.WriteAllText(manifest, ManifestJson());
            return (timestamps, manifest);
        }

        private static JsonObject Set(string setId, List<BatchTrophy> trophies)
        {
            var array = new JsonArray();

            foreach (var trophy in trophies)
            {
                array.Add(new JsonObject
                {
                    ["trophyId"] = trophy.Id,
                    ["trophyType"] = trophy.Type,
                    ["earnedDateTimeUTC"] = trophy.Utc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture),
                });
            }

            return new JsonObject
            {
                ["npCommunicationId"] = setId,
                ["npServiceName"] = "trophy",
                ["totalTrophies"] = 50,
                ["earnedCount"] = trophies.Count,
                ["trophies"] = array,
            };
        }
    }
}
