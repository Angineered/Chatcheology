using System.Globalization;
using System.Text.Json;
using Chatcheology.Data.Memories.Psn;

namespace Chatcheology.Tools.MemoryImport
{
    /// <summary>Counts from one build. Counts only: no titles, set IDs or account names.</summary>
    public sealed class PsnBuildCounts
    {
        /// <summary>Games in the manifest.</summary>
        public int ManifestGames { get; internal set; }

        /// <summary>Games in the timestamp file(s).</summary>
        public int TimestampGames { get; internal set; }

        /// <summary>Games written (in the manifest and with timestamps).</summary>
        public int GamesBuilt { get; internal set; }

        /// <summary>Games written whose two accounts use different trophy sets (never matched).</summary>
        public int GamesWithDistinctSets { get; internal set; }

        /// <summary>Events written.</summary>
        public int Events { get; internal set; }

        /// <summary>Events per level.</summary>
        public int Strong { get; internal set; }

        /// <summary>Events per level.</summary>
        public int Supporting { get; internal set; }

        /// <summary>Events per level.</summary>
        public int SameDay { get; internal set; }

        /// <summary>Matching trophy pairs written.</summary>
        public int MatchingPairs { get; internal set; }

        /// <summary>Trophies written in events (both accounts).</summary>
        public int TrophiesInEvents { get; internal set; }

        /// <summary>Skipped: manifest games with no timestamp entry.</summary>
        public int ManifestGamesWithoutTimestamps { get; internal set; }

        /// <summary>Skipped: timestamp games not in the manifest.</summary>
        public int TimestampGamesNotInManifest { get; internal set; }

        /// <summary>Games written with no event: neither account earned a trophy.</summary>
        public int GamesWithNoTrophies { get; internal set; }

        /// <summary>Games written with no event: only one account earned trophies.</summary>
        public int GamesOneAccountOnly { get; internal set; }

        /// <summary>Games written with no event: both earned trophies, never on the same date.</summary>
        public int GamesNoCommonDate { get; internal set; }

        /// <summary>Game dates on which only one account earned trophies (no event).</summary>
        public int OneAccountDates { get; internal set; }

        /// <summary>Game icons taken from the manifest.</summary>
        public int IconsFromManifest { get; internal set; }

        /// <summary>Game icons carried over from the existing shared-activity file.</summary>
        public int IconsFromExistingFile { get; internal set; }

        /// <summary>Games written with no icon.</summary>
        public int IconsNone { get; internal set; }
    }

    /// <summary>The built file and its counts.</summary>
    public sealed record PsnBuildResult(byte[] Json, PsnBuildCounts Counts);

    /// <summary>
    /// Builds a <see cref="PsnSharedActivityReader.Schema"/> file from the two accounts' earned-trophy
    /// timestamps (the fetch script's <see cref="TimestampsFormat"/> output) and the hand-kept game
    /// manifest. Rules (CH-71 D8, the same rules the earlier file was made with):
    /// <list type="bullet">
    /// <item>One event per (game set pair, <see cref="TimeZoneId"/> calendar date) on which both
    /// accounts earned at least one trophy in that game.</item>
    /// <item>Trophy IDs are compared only where both accounts use the same trophy set
    /// (npCommunicationId); different sets never match.</item>
    /// <item>strong: at least one identical trophy ID earned by both on that date at most
    /// <see cref="PsnSharedActivityReader.StrongPairSeconds"/> seconds apart; supporting: identical
    /// IDs that date, none that close; same-day: no identical ID that date.</item>
    /// <item>Every identical trophy ID earned by both on that date is a matching pair.</item>
    /// <item>Event id <c>&lt;setA&gt;|&lt;setB&gt;|yyyy-MM-dd</c>, side a being account a.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Strict: an input that is not the expected shape stops the build with the game and trophy index;
    /// nothing is guessed. Messages never quote a value from the inputs.
    /// </remarks>
    public static class PsnSharedActivityBuilder
    {
        /// <summary>The fetch script's output format, the only one accepted.</summary>
        public const string TimestampsFormat = "chatcheology-psn-trophy-batch-v1";

        /// <summary>The time zone that defines the calendar date.</summary>
        public const string TimeZoneId = "Africa/Johannesburg";

        private const string UtcFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'";
        private const string DateFormat = "yyyy'-'MM'-'dd";
        private const string TimeFormat = "HH':'mm':'ss";

        private sealed record Trophy(long Id, string Type, DateTime Utc, DateTime Local);

        private sealed record AccountSet(string SetId, List<Trophy> Trophies);

        private sealed record ManifestGame(string Key, string Title, string SetA, string SetB, string? Icon);

        private sealed record TimestampGame(AccountSet A, AccountSet B);

        /// <summary>Builds the file.</summary>
        /// <param name="timestampFiles">One or more fetch-script outputs; a game key may appear in only one.</param>
        /// <param name="manifest">The hand-kept game manifest.</param>
        /// <param name="accountA">The account field name of side a (its set is first in every key); also its prefix in the output.</param>
        /// <param name="accountB">The other account's field name and prefix.</param>
        /// <param name="existingFile">Optional existing shared-activity file to carry game icons over from (by key) where the manifest has none.</param>
        /// <exception cref="ArgumentException">An account name is not plain letters or digits, or both are the same.</exception>
        /// <exception cref="FormatException">An input is not the accepted shape.</exception>
        public static PsnBuildResult Build(
            IReadOnlyList<ReadOnlyMemory<byte>> timestampFiles,
            ReadOnlyMemory<byte> manifest,
            string accountA,
            string accountB,
            ReadOnlyMemory<byte>? existingFile = null)
        {
            ArgumentNullException.ThrowIfNull(timestampFiles);
            CheckAccount(accountA, "a");
            CheckAccount(accountB, "b");

            if (string.Equals(accountA, accountB, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The two account names are the same; they must name two different accounts.");
            }

            if (timestampFiles.Count == 0)
            {
                throw new ArgumentException("At least one timestamp file is needed.");
            }

            var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
            var counts = new PsnBuildCounts();

            var manifestGames = ReadManifest(manifest, accountA, accountB);
            counts.ManifestGames = manifestGames.Count;

            var timestamps = new Dictionary<string, TimestampGame>(StringComparer.Ordinal);

            for (var file = 0; file < timestampFiles.Count; file++)
            {
                ReadTimestamps(timestampFiles[file], file, accountA, accountB, zone, timestamps);
            }

            counts.TimestampGames = timestamps.Count;
            var existingIcons = existingFile is null ? null : ReadExistingIcons(existingFile.Value);

            var manifestKeys = new HashSet<string>(manifestGames.Select(game => game.Key), StringComparer.Ordinal);
            counts.TimestampGamesNotInManifest = timestamps.Keys.Count(key => !manifestKeys.Contains(key));

            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("schema", PsnSharedActivityReader.Schema);
                writer.WriteString("timezone", TimeZoneId);
                writer.WriteString("generator", "Chatcheology MemoryImport psn-build");
                writer.WriteString(
                    "rules",
                    "One event per (trophy set pair, Africa/Johannesburg date) on which both accounts earned trophies. " +
                    "strong: an identical trophy id (same set only) earned by both that date at most 60 s apart; " +
                    "supporting: identical ids that date, none within 60 s; same-day: no identical id that date.");

                var events = new List<(string Key, DateOnly Date, Action<Utf8JsonWriter> Write)>();

                writer.WriteStartArray("games");
                var gameIndex = 0;

                foreach (var game in manifestGames)
                {
                    if (!timestamps.TryGetValue(game.Key, out var stamps))
                    {
                        counts.ManifestGamesWithoutTimestamps++;
                        gameIndex++;
                        continue;
                    }

                    if (!string.Equals(stamps.A.SetId, game.SetA, StringComparison.Ordinal)
                        || !string.Equals(stamps.B.SetId, game.SetB, StringComparison.Ordinal))
                    {
                        throw new FormatException(
                            $"Manifest game at index {gameIndex}: the timestamp file's trophy sets for this key are not the manifest's.");
                    }

                    var icon = game.Icon;

                    if (icon is not null)
                    {
                        counts.IconsFromManifest++;
                    }
                    else if (existingIcons is not null && existingIcons.TryGetValue(game.Key, out var carried) && carried is not null)
                    {
                        icon = carried;
                        counts.IconsFromExistingFile++;
                    }
                    else
                    {
                        counts.IconsNone++;
                    }

                    var sameSet = string.Equals(game.SetA, game.SetB, StringComparison.Ordinal);

                    writer.WriteStartObject();
                    writer.WriteString("key", game.Key);
                    writer.WriteString("game", game.Title);

                    if (icon is null)
                    {
                        writer.WriteNull("icon");
                    }
                    else
                    {
                        writer.WriteString("icon", icon);
                    }

                    writer.WriteBoolean("sameTrophySet", sameSet);
                    writer.WriteString(accountA + "TrophySet", game.SetA);
                    writer.WriteString(accountB + "TrophySet", game.SetB);
                    writer.WriteEndObject();

                    counts.GamesBuilt++;

                    if (!sameSet)
                    {
                        counts.GamesWithDistinctSets++;
                    }

                    AddEvents(game, stamps, sameSet, accountA, accountB, counts, events);
                    gameIndex++;
                }

                writer.WriteEndArray();

                writer.WriteStartArray("events");

                foreach (var e in events.OrderBy(e => e.Date).ThenBy(e => e.Key, StringComparer.Ordinal))
                {
                    e.Write(writer);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return new PsnBuildResult(stream.ToArray(), counts);
        }

        private static void AddEvents(
            ManifestGame game,
            TimestampGame stamps,
            bool sameSet,
            string accountA,
            string accountB,
            PsnBuildCounts counts,
            List<(string Key, DateOnly Date, Action<Utf8JsonWriter> Write)> events)
        {
            if (stamps.A.Trophies.Count == 0 && stamps.B.Trophies.Count == 0)
            {
                counts.GamesWithNoTrophies++;
                return;
            }

            if (stamps.A.Trophies.Count == 0 || stamps.B.Trophies.Count == 0)
            {
                counts.GamesOneAccountOnly++;
                counts.OneAccountDates += stamps.A.Trophies.Concat(stamps.B.Trophies)
                    .Select(trophy => DateOnly.FromDateTime(trophy.Local)).Distinct().Count();
                return;
            }

            var byDateA = stamps.A.Trophies.ToLookup(trophy => DateOnly.FromDateTime(trophy.Local));
            var byDateB = stamps.B.Trophies.ToLookup(trophy => DateOnly.FromDateTime(trophy.Local));
            var dates = byDateA.Select(group => group.Key).Union(byDateB.Select(group => group.Key)).ToList();
            var gameEvents = 0;

            foreach (var date in dates)
            {
                var a = byDateA[date].OrderBy(t => t.Utc).ThenBy(t => t.Id).ToList();
                var b = byDateB[date].OrderBy(t => t.Utc).ThenBy(t => t.Id).ToList();

                if (a.Count == 0 || b.Count == 0)
                {
                    counts.OneAccountDates++;
                    continue;
                }

                var pairs = new List<(Trophy A, Trophy B)>();

                if (sameSet)
                {
                    var bById = b.ToDictionary(t => t.Id);

                    foreach (var trophy in a)
                    {
                        if (bById.TryGetValue(trophy.Id, out var match))
                        {
                            pairs.Add((trophy, match));
                        }
                    }
                }

                string level;

                if (pairs.Count == 0)
                {
                    level = "same-day";
                    counts.SameDay++;
                }
                else if (pairs.Any(pair => Math.Abs((pair.A.Utc - pair.B.Utc).TotalSeconds) <= PsnSharedActivityReader.StrongPairSeconds))
                {
                    level = "strong";
                    counts.Strong++;
                }
                else
                {
                    level = "supporting";
                    counts.Supporting++;
                }

                counts.Events++;
                counts.MatchingPairs += pairs.Count;
                counts.TrophiesInEvents += a.Count + b.Count;
                gameEvents++;

                var dateText = date.ToString(DateFormat, CultureInfo.InvariantCulture);
                var key = game.Key;

                events.Add((key, date, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", $"{key}|{dateText}");
                    writer.WriteString("gameKey", key);
                    writer.WriteString("date", dateText);
                    writer.WriteString("quality", level);
                    WriteTrophies(writer, accountA + "Trophies", a);
                    WriteTrophies(writer, accountB + "Trophies", b);
                    writer.WriteStartArray("matchingTrophies");

                    foreach (var (pa, pb) in pairs)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("id", pa.Id);
                        writer.WriteString("type", pa.Type);
                        writer.WriteString("aTime", pa.Local.ToString(TimeFormat, CultureInfo.InvariantCulture));
                        writer.WriteString("bTime", pb.Local.ToString(TimeFormat, CultureInfo.InvariantCulture));
                        writer.WriteNumber("seconds", (long)(pb.Utc - pa.Utc).TotalSeconds);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }));
            }

            if (gameEvents == 0)
            {
                counts.GamesNoCommonDate++;
            }
        }

        private static void WriteTrophies(Utf8JsonWriter writer, string name, List<Trophy> trophies)
        {
            writer.WriteStartArray(name);

            foreach (var trophy in trophies)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", trophy.Id);
                writer.WriteString("type", trophy.Type);
                writer.WriteString("time", trophy.Local.ToString(TimeFormat, CultureInfo.InvariantCulture));
                writer.WriteString("utc", trophy.Utc.ToString(UtcFormat, CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static void CheckAccount(string account, string which)
        {
            if (string.IsNullOrWhiteSpace(account) || !account.All(char.IsAsciiLetterOrDigit))
            {
                throw new ArgumentException($"Account {which} must be plain letters or digits.");
            }
        }

        private static JsonDocument ParseJson(ReadOnlyMemory<byte> json, string what)
        {
            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                throw new FormatException($"The {what} is not valid JSON.");
            }
        }

        private static List<ManifestGame> ReadManifest(ReadOnlyMemory<byte> json, string accountA, string accountB)
        {
            using var document = ParseJson(json, "manifest");
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !TryGet(root, "games", out var gamesElement) || gamesElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The manifest has no games array.");
            }

            var games = new List<ManifestGame>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;

            foreach (var game in gamesElement.EnumerateArray())
            {
                var where = $"Manifest game at index {index}";

                if (game.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"{where} is not an object.");
                }

                var key = RequireString(game, "key", $"{where} has no key.");
                var title = RequireString(game, "game", $"{where} has no game title.");
                var setA = RequireSet(game, accountA, $"{where} has no trophy set (npCommunicationId) for account a.");
                var setB = RequireSet(game, accountB, $"{where} has no trophy set (npCommunicationId) for account b.");
                string? icon = null;

                if (TryGet(game, "icon", out var iconElement) && iconElement.ValueKind != JsonValueKind.Null)
                {
                    if (iconElement.ValueKind != JsonValueKind.String)
                    {
                        throw new FormatException($"{where} has an icon that is not text or null.");
                    }

                    icon = string.IsNullOrWhiteSpace(iconElement.GetString()) ? null : iconElement.GetString();
                }

                if (!string.Equals(key, $"{setA}|{setB}", StringComparison.Ordinal))
                {
                    throw new FormatException(
                        $"{where}: its key is not account a's trophy set then account b's, joined by '|'. Check --account-a.");
                }

                if (TryGet(game, "sameTrophySet", out var sameElement))
                {
                    if (sameElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new FormatException($"{where} has a sameTrophySet that is not true or false.");
                    }

                    if (sameElement.GetBoolean() != string.Equals(setA, setB, StringComparison.Ordinal))
                    {
                        throw new FormatException($"{where}: sameTrophySet disagrees with its two trophy sets.");
                    }
                }

                if (!keys.Add(key))
                {
                    throw new FormatException($"{where} repeats an earlier game's key.");
                }

                games.Add(new ManifestGame(key, title, setA, setB, icon));
                index++;
            }

            return games;
        }

        private static string RequireSet(JsonElement game, string account, string message)
        {
            if (!TryGet(game, account, out var set) || set.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException(message);
            }

            return RequireString(set, "npCommunicationId", message);
        }

        private static void ReadTimestamps(
            ReadOnlyMemory<byte> json,
            int fileIndex,
            string accountA,
            string accountB,
            TimeZoneInfo zone,
            Dictionary<string, TimestampGame> games)
        {
            var file = $"Timestamp file {fileIndex + 1}";
            using var document = ParseJson(json, $"timestamp file {fileIndex + 1}");
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !TryGet(root, "format", out var format)
                || format.ValueKind != JsonValueKind.String
                || !string.Equals(format.GetString(), TimestampsFormat, StringComparison.Ordinal))
            {
                throw new FormatException($"{file} is not format {TimestampsFormat}; no other format is accepted.");
            }

            if (TryGet(root, "timestampsAreUTC", out var utcFlag) && utcFlag.ValueKind != JsonValueKind.True)
            {
                throw new FormatException($"{file} does not say its timestamps are UTC.");
            }

            if (!TryGet(root, "games", out var gamesElement) || gamesElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"{file} has no games array.");
            }

            var index = 0;

            foreach (var game in gamesElement.EnumerateArray())
            {
                var where = $"{file}, game at index {index}";

                if (game.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"{where} is not an object.");
                }

                var key = RequireString(game, "key", $"{where} has no key.");
                var a = ReadAccountSet(game, accountA, "a", zone, where);
                var b = ReadAccountSet(game, accountB, "b", zone, where);

                if (!string.Equals(key, $"{a.SetId}|{b.SetId}", StringComparison.Ordinal))
                {
                    throw new FormatException(
                        $"{where}: its key is not account a's trophy set then account b's, joined by '|'. Check --account-a.");
                }

                if (!games.TryAdd(key, new TimestampGame(a, b)))
                {
                    throw new FormatException($"{where} repeats a game key already read.");
                }

                index++;
            }
        }

        private static AccountSet ReadAccountSet(JsonElement game, string account, string which, TimeZoneInfo zone, string where)
        {
            if (!TryGet(game, account, out var set) || set.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"{where} has no object for account {which}.");
            }

            var setId = RequireString(set, "npCommunicationId", $"{where} has no npCommunicationId for account {which}.");

            if (!TryGet(set, "trophies", out var trophiesElement) || trophiesElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"{where} has no trophies array for account {which}.");
            }

            var trophies = new List<Trophy>();
            var ids = new HashSet<long>();
            var index = 0;

            foreach (var trophy in trophiesElement.EnumerateArray())
            {
                var at = $"{where}, account {which} trophy at index {index}";

                if (trophy.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"{at} is not an object.");
                }

                if (!TryGet(trophy, "trophyId", out var idElement)
                    || idElement.ValueKind != JsonValueKind.Number
                    || !idElement.TryGetInt64(out var id)
                    || id < 0)
                {
                    throw new FormatException($"{at} has no whole-number trophyId.");
                }

                var type = RequireString(trophy, "trophyType", $"{at} has no trophyType.");
                var utcText = RequireString(trophy, "earnedDateTimeUTC", $"{at} has no earnedDateTimeUTC.");

                if (!DateTime.TryParseExact(utcText, UtcFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                {
                    throw new FormatException($"{at}: earnedDateTimeUTC is not yyyy-MM-ddTHH:mm:ssZ.");
                }

                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

                if (!ids.Add(id))
                {
                    throw new FormatException($"{at} repeats an earlier trophyId of the same account and set.");
                }

                var local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, zone), DateTimeKind.Unspecified);
                trophies.Add(new Trophy(id, type, utc, local));
                index++;
            }

            if (TryGet(set, "earnedCount", out var earned)
                && (earned.ValueKind != JsonValueKind.Number || !earned.TryGetInt32(out var count) || count != trophies.Count))
            {
                throw new FormatException($"{where}: account {which}'s earnedCount does not equal its trophies.");
            }

            return new AccountSet(setId, trophies);
        }

        private static Dictionary<string, string?> ReadExistingIcons(ReadOnlyMemory<byte> json)
        {
            using var document = ParseJson(json, "existing shared-activity file");
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !TryGet(root, "schema", out var schema)
                || schema.ValueKind != JsonValueKind.String
                || !string.Equals(schema.GetString(), PsnSharedActivityReader.Schema, StringComparison.Ordinal))
            {
                throw new FormatException($"The existing file is not schema {PsnSharedActivityReader.Schema}.");
            }

            if (!TryGet(root, "games", out var gamesElement) || gamesElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The existing file has no games array.");
            }

            var icons = new Dictionary<string, string?>(StringComparer.Ordinal);
            var index = 0;

            foreach (var game in gamesElement.EnumerateArray())
            {
                var where = $"Existing file game at index {index}";

                if (game.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"{where} is not an object.");
                }

                var key = RequireString(game, "key", $"{where} has no key.");
                string? icon = null;

                if (TryGet(game, "icon", out var iconElement) && iconElement.ValueKind != JsonValueKind.Null)
                {
                    if (iconElement.ValueKind != JsonValueKind.String)
                    {
                        throw new FormatException($"{where} has an icon that is not text or null.");
                    }

                    icon = string.IsNullOrWhiteSpace(iconElement.GetString()) ? null : iconElement.GetString();
                }

                if (!icons.TryAdd(key, icon))
                {
                    throw new FormatException($"{where} repeats an earlier game's key.");
                }

                index++;
            }

            return icons;
        }

        private static string RequireString(JsonElement element, string name, string message)
        {
            if (TryGet(element, name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }

            throw new FormatException(message);
        }

        /// <summary>Case-insensitive property lookup; two properties differing only by case are refused.</summary>
        private static bool TryGet(JsonElement element, string name, out JsonElement value)
        {
            var found = false;
            value = default;

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (found)
                    {
                        throw new FormatException("An input has two fields whose names differ only by case.");
                    }

                    value = property.Value;
                    found = true;
                }
            }

            return found;
        }
    }
}
