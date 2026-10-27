using System.Globalization;
using System.Text.Json;

namespace Chatcheology.Data.Memories.Psn
{
    /// <summary>
    /// Reads a PSN shared-activity file (schema <see cref="Schema"/> only), maps its two accounts to
    /// the Local and Other roles, converts and checks times, and splits each event into periods.
    /// </summary>
    /// <remarks>
    /// Strict: only the one known shape is accepted, and anything else stops the read with the
    /// index of the game or event. Only <c>schema</c>, <c>timezone</c>, <c>games</c> and
    /// <c>events</c> are used; other root fields are ignored.
    /// <list type="bullet">
    /// <item>Game: <c>key</c> (<c>&lt;setA&gt;|&lt;setB&gt;</c>), <c>game</c> (the title), <c>icon</c>
    /// (URL or null), <c>&lt;prefix&gt;TrophySet</c> for both accounts.</item>
    /// <item>Event: <c>id</c> (<c>&lt;gameKey&gt;|yyyy-MM-dd</c>), <c>gameKey</c>, <c>date</c>
    /// (local date), <c>quality</c>, <c>&lt;prefix&gt;Trophies</c> for both accounts,
    /// <c>matchingTrophies</c>.</item>
    /// <item>Trophy: <c>id</c> (integer), <c>type</c>, <c>time</c> (<c>HH:mm:ss</c> local, on the
    /// event's date), <c>utc</c> (<c>yyyy-MM-ddTHH:mm:ssZ</c>).</item>
    /// <item>Pair: <c>id</c> (integer), <c>aTime</c>, <c>bTime</c> (<c>HH:mm:ss</c>). Side a is the
    /// account whose set is first in the game key; the caller names its prefix.</item>
    /// </list>
    /// Field names match case-insensitively. Privacy: no message from here quotes a value from the
    /// file or a prefix.
    /// </remarks>
    public static class PsnSharedActivityReader
    {
        /// <summary>The only schema accepted.</summary>
        public const string Schema = "chatcheology-psn-shared-activity-v1";

        /// <summary>The default period gap in minutes (design: at most 60 minutes chains).</summary>
        public const int DefaultPeriodGapMinutes = 60;

        /// <summary>A matching pair at most this many seconds apart makes a period strong.</summary>
        public const int StrongPairSeconds = 60;

        private const string DateFormat = "yyyy'-'MM'-'dd";
        private const string TimeFormat = "HH':'mm':'ss";
        private const string UtcFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'";

        /// <summary>Reads the file at <paramref name="path"/>.</summary>
        public static PsnActivity Read(
            string path, string localPrefix, string otherPrefix, string pairAPrefix, int periodGapMinutes = DefaultPeriodGapMinutes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            byte[] bytes;

            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The framework message may quote the path.
                throw new InvalidOperationException($"The PSN file could not be read ({exception.GetType().Name}).");
            }

            return Parse(bytes, localPrefix, otherPrefix, pairAPrefix, periodGapMinutes);
        }

        /// <summary>Reads a file already in memory.</summary>
        /// <param name="json">The file.</param>
        /// <param name="localPrefix">The account prefix mapped to the Local role.</param>
        /// <param name="otherPrefix">The account prefix mapped to the Other role.</param>
        /// <param name="pairAPrefix">Which of the two prefixes is side a of the matching pairs.</param>
        /// <param name="periodGapMinutes">The period gap.</param>
        /// <exception cref="ArgumentException">A prefix is empty or not plain, the two are the same, or side a names neither.</exception>
        /// <exception cref="FormatException">The file is not the accepted shape. Nothing is guessed.</exception>
        public static PsnActivity Parse(
            ReadOnlyMemory<byte> json,
            string localPrefix,
            string otherPrefix,
            string pairAPrefix,
            int periodGapMinutes = DefaultPeriodGapMinutes)
        {
            CheckPrefix(localPrefix, "local");
            CheckPrefix(otherPrefix, "other");

            if (string.Equals(localPrefix, otherPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The local and other prefixes are the same; they must name two different accounts.");
            }

            string pairARole;

            if (string.Equals(pairAPrefix, localPrefix, StringComparison.OrdinalIgnoreCase))
            {
                pairARole = MemoryRoles.Local;
            }
            else if (string.Equals(pairAPrefix, otherPrefix, StringComparison.OrdinalIgnoreCase))
            {
                pairARole = MemoryRoles.Other;
            }
            else
            {
                throw new ArgumentException("The pair side a prefix must be either the local or the other prefix.");
            }

            if (periodGapMinutes < 1)
            {
                throw new ArgumentException("The period gap must be at least one minute.", nameof(periodGapMinutes));
            }

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                throw new FormatException("The PSN file is not valid JSON.");
            }

            using (document)
            {
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object
                    || !TryGet(root, "schema", out var schema)
                    || schema.ValueKind != JsonValueKind.String
                    || !string.Equals(schema.GetString(), Schema, StringComparison.Ordinal))
                {
                    throw new FormatException($"The file is not schema {Schema}; no other schema is accepted.");
                }

                var zone = ReadZone(root);
                var accounts = new Accounts(localPrefix, otherPrefix, pairARole);
                var games = ReadGames(root, accounts);
                var records = ReadEvents(root, games, zone, accounts, periodGapMinutes);

                return new PsnActivity(games.Count, periodGapMinutes, records);
            }
        }

        private sealed record Accounts(string LocalPrefix, string OtherPrefix, string PairARole);

        private sealed record Game(string Title, string LocalSet, string OtherSet, string? Icon);

        private static void CheckPrefix(string prefix, string which)
        {
            if (string.IsNullOrWhiteSpace(prefix) || !prefix.All(char.IsAsciiLetterOrDigit))
            {
                throw new ArgumentException($"The {which} prefix must be plain letters or digits.");
            }
        }

        private static TimeZoneInfo ReadZone(JsonElement root)
        {
            if (!TryGet(root, "timezone", out var zoneElement) || zoneElement.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("The file has no timezone.");
            }

            var id = zoneElement.GetString()!;

            if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
            {
                throw new FormatException("The file's timezone is not a known IANA time zone.");
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new FormatException("The file's timezone is not a known IANA time zone.");
            }
        }

        private static Dictionary<string, Game> ReadGames(JsonElement root, Accounts accounts)
        {
            if (!TryGet(root, "games", out var gamesElement) || gamesElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The file has no games array.");
            }

            var games = new Dictionary<string, Game>(StringComparer.Ordinal);
            var index = 0;

            foreach (var game in gamesElement.EnumerateArray())
            {
                var where = $"Game at index {index}";

                if (game.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"{where} is not an object.");
                }

                var key = RequireString(game, "key", $"{where} has no key.");
                var title = RequireString(game, "game", $"{where} has no game title.");
                var localSet = RequireString(game, accounts.LocalPrefix + "TrophySet", $"{where} has no trophy set field for the local prefix.");
                var otherSet = RequireString(game, accounts.OtherPrefix + "TrophySet", $"{where} has no trophy set field for the other prefix.");
                var icon = OptionalString(game, "icon", $"{where} has an icon that is not text.");

                var parts = key.Split('|');

                if (parts.Length != 2)
                {
                    throw new FormatException($"{where} has a key that is not <set>|<set>.");
                }

                var (setA, setB) = accounts.PairARole == MemoryRoles.Local ? (localSet, otherSet) : (otherSet, localSet);

                // Where the sets differ, this proves which account is side a; where they are the
                // same set, the key cannot tell the accounts apart and only the sets are checked.
                if (!string.Equals(parts[0], setA, StringComparison.Ordinal) || !string.Equals(parts[1], setB, StringComparison.Ordinal))
                {
                    throw new FormatException(
                        $"{where}: the side a prefix's trophy set is not first in the key, or the key does not name " +
                        "the two trophy sets. Check --pair-a.");
                }

                if (!games.TryAdd(key, new Game(title, localSet, otherSet, icon)))
                {
                    throw new FormatException($"{where} repeats an earlier game's key.");
                }

                index++;
            }

            return games;
        }

        private static List<PsnRecord> ReadEvents(
            JsonElement root,
            Dictionary<string, Game> games,
            TimeZoneInfo zone,
            Accounts accounts,
            int periodGapMinutes)
        {
            if (!TryGet(root, "events", out var eventsElement) || eventsElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The file has no events array.");
            }

            var records = new List<PsnRecord>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;

            foreach (var element in eventsElement.EnumerateArray())
            {
                var where = $"Event at index {index}";

                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"{where} is not an object.");
                }

                var id = RequireString(element, "id", $"{where} has no id.");
                var gameKey = RequireString(element, "gameKey", $"{where} has no gameKey.");
                var dateText = RequireString(element, "date", $"{where} has no date.");
                var level = RequireString(element, "quality", $"{where} has no quality.");

                if (!MemoriesDatabase.DayLevels.Contains(level, StringComparer.Ordinal))
                {
                    throw new FormatException($"{where} has a quality other than strong, supporting or same-day.");
                }

                if (!keys.Add(id))
                {
                    throw new FormatException($"{where} repeats an earlier event id.");
                }

                if (!DateOnly.TryParseExact(dateText, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    throw new FormatException($"{where} has a date that is not yyyy-MM-dd.");
                }

                if (!string.Equals(id, $"{gameKey}|{dateText}", StringComparison.Ordinal))
                {
                    throw new FormatException($"{where}: its id is not its gameKey and date joined by '|'.");
                }

                if (!games.TryGetValue(gameKey, out var game))
                {
                    throw new FormatException($"{where} names a gameKey with no game.");
                }

                var items = new List<(string Role, string ItemId, string Type, DateTime Utc, DateTime Local)>();
                ReadTrophies(element, accounts.LocalPrefix, MemoryRoles.Local, date, zone, where, items);
                ReadTrophies(element, accounts.OtherPrefix, MemoryRoles.Other, date, zone, where, items);

                if (items.Count == 0)
                {
                    throw new FormatException($"{where} has no trophies.");
                }

                var pairs = ReadPairs(element, accounts.PairARole, date, where);

                records.Add(PsnPeriodBuilder.Build(
                    id, game.Title, level, game.Icon, game.LocalSet, game.OtherSet, items, pairs, periodGapMinutes));

                index++;
            }

            return records;
        }

        private static void ReadTrophies(
            JsonElement element,
            string prefix,
            string role,
            DateOnly date,
            TimeZoneInfo zone,
            string where,
            List<(string Role, string ItemId, string Type, DateTime Utc, DateTime Local)> items)
        {
            var which = role == MemoryRoles.Local ? "local" : "other";

            if (!TryGet(element, prefix + "Trophies", out var trophies) || trophies.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"{where} has no trophy list for the {which} prefix.");
            }

            foreach (var trophy in trophies.EnumerateArray())
            {
                if (trophy.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"{where}: a {which} trophy is not an object.");
                }

                var itemId = RequireInteger(trophy, "id", $"{where}: a {which} trophy has no integer id.");
                var type = RequireString(trophy, "type", $"{where}: a {which} trophy has no type.");
                var utcText = RequireString(trophy, "utc", $"{where}: a {which} trophy has no utc time.");
                var timeText = RequireString(trophy, "time", $"{where}: a {which} trophy has no time.");

                if (!DateTime.TryParseExact(utcText, UtcFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                {
                    throw new FormatException($"{where}: a {which} trophy's utc time is not yyyy-MM-ddTHH:mm:ssZ.");
                }

                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

                if (!TimeOnly.TryParseExact(timeText, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                {
                    throw new FormatException($"{where}: a {which} trophy's time is not HH:mm:ss.");
                }

                var local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, zone), DateTimeKind.Unspecified);

                if (local != date.ToDateTime(time))
                {
                    throw new FormatException(
                        $"{where}: a {which} trophy's utc time converted to the file's timezone does not equal " +
                        "the event date and the trophy's time. Stopped.");
                }

                items.Add((role, itemId, type, utc, local));
            }
        }

        private static List<PsnPeriodBuilder.Pair> ReadPairs(JsonElement element, string pairARole, DateOnly date, string where)
        {
            if (!TryGet(element, "matchingTrophies", out var pairsElement) || pairsElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"{where} has no matchingTrophies list.");
            }

            var pairs = new List<PsnPeriodBuilder.Pair>();

            foreach (var pair in pairsElement.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"{where}: a matching pair is not an object.");
                }

                var id = RequireInteger(pair, "id", $"{where}: a matching pair has no integer id.");
                var aTime = RequireTime(pair, "aTime", $"{where}: a matching pair's aTime is not HH:mm:ss.");
                var bTime = RequireTime(pair, "bTime", $"{where}: a matching pair's bTime is not HH:mm:ss.");

                var a = (id, date.ToDateTime(aTime));
                var b = (id, date.ToDateTime(bTime));

                pairs.Add(pairARole == MemoryRoles.Local ? new PsnPeriodBuilder.Pair(a, b) : new PsnPeriodBuilder.Pair(b, a));
            }

            return pairs;
        }

        private static TimeOnly RequireTime(JsonElement element, string name, string message)
        {
            var text = RequireString(element, name, message);

            return TimeOnly.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time
                : throw new FormatException(message);
        }

        private static string RequireInteger(JsonElement element, string name, string message)
        {
            if (TryGet(element, name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }

            throw new FormatException(message);
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

        private static string? OptionalString(JsonElement element, string name, string message)
        {
            if (!TryGet(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw new FormatException(message);
            }

            return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString();
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
                        throw new FormatException("The file has two fields whose names differ only by case.");
                    }

                    value = property.Value;
                    found = true;
                }
            }

            return found;
        }
    }
}
