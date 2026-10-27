using System.Globalization;
using System.Text.Json;

namespace Chatcheology.Data.Memories.Psn
{
    /// <summary>
    /// Reads a PSN shared-activity file (schema <see cref="Schema"/> only), maps its two accounts to
    /// the Local and Other roles, converts and checks times, and splits each event into periods.
    /// </summary>
    /// <remarks>
    /// Only <c>schema</c>, <c>timezone</c>, <c>games</c> and <c>events</c> are used.
    /// Account-specific fields are named after the accounts (<c>&lt;prefix&gt;Trophies</c> on an
    /// event, <c>&lt;prefix&gt;TrophySet</c> on a game); the caller supplies both prefixes, matched
    /// case-insensitively, and both must be present in every game and every event.
    /// <para>
    /// Privacy: no message from here quotes a value from the file or a prefix. Stops name the
    /// 0-based index of the game or event and the kind of problem only.
    /// </para>
    /// </remarks>
    public static class PsnSharedActivityReader
    {
        /// <summary>The only schema accepted.</summary>
        public const string Schema = "chatcheology-psn-shared-activity-v1";

        /// <summary>The default period gap in minutes (design: at most 60 minutes chains).</summary>
        public const int DefaultPeriodGapMinutes = 60;

        /// <summary>A matching pair at most this many seconds apart makes a period strong.</summary>
        public const int StrongPairSeconds = 60;

        private static readonly string[] LocalTimeFormats =
        [
            "yyyy'-'MM'-'dd' 'HH':'mm':'ss",
            "yyyy'-'MM'-'dd'T'HH':'mm':'ss",
        ];

        private static readonly string[] LocalTimeWithOffsetFormats =
        [
            "yyyy'-'MM'-'dd' 'HH':'mm':'sszzz",
            "yyyy'-'MM'-'dd'T'HH':'mm':'sszzz",
        ];

        /// <summary>Reads the file at <paramref name="path"/>.</summary>
        public static PsnActivity Read(string path, string localPrefix, string otherPrefix, int periodGapMinutes = DefaultPeriodGapMinutes)
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
                throw new InvalidOperationException(
                    $"The PSN file could not be read ({exception.GetType().Name}).");
            }

            return Parse(bytes, localPrefix, otherPrefix, periodGapMinutes);
        }

        /// <summary>Reads a file already in memory.</summary>
        /// <exception cref="ArgumentException">A prefix is empty, not plain letters/digits, or both are the same.</exception>
        /// <exception cref="FormatException">The file is not what this reader accepts. Nothing is guessed.</exception>
        public static PsnActivity Parse(ReadOnlyMemory<byte> json, string localPrefix, string otherPrefix, int periodGapMinutes = DefaultPeriodGapMinutes)
        {
            CheckPrefix(localPrefix, "local");
            CheckPrefix(otherPrefix, "other");

            if (string.Equals(localPrefix, otherPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The local and other prefixes are the same; they must name two different accounts.");
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
                var games = ReadGames(root, localPrefix, otherPrefix);
                var records = ReadEvents(root, games, zone, localPrefix, otherPrefix, periodGapMinutes);

                return new PsnActivity(games.Count, periodGapMinutes, records);
            }
        }

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

            if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _) && !IsIanaOnSystem(id))
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

        private static bool IsIanaOnSystem(string id) =>
            !OperatingSystem.IsWindows() && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) && zone.HasIanaId;

        private sealed record Game(string Title, string LocalSet, string OtherSet, string? Icon);

        private static Dictionary<(string, string), Game> ReadGames(JsonElement root, string localPrefix, string otherPrefix)
        {
            if (!TryGet(root, "games", out var gamesElement) || gamesElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The file has no games array.");
            }

            var games = new Dictionary<(string, string), Game>();
            var index = 0;

            foreach (var game in gamesElement.EnumerateArray())
            {
                if (game.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"Game at index {index} is not an object.");
                }

                var localSet = RequireString(game, localPrefix + "TrophySet",
                    $"Game at index {index} has no trophy set field for the local prefix.");
                var otherSet = RequireString(game, otherPrefix + "TrophySet",
                    $"Game at index {index} has no trophy set field for the other prefix.");
                var title = RequireString(game, "title", $"Game at index {index} has no title.");
                string? icon = null;

                if (TryGet(game, "icon", out var iconElement) && iconElement.ValueKind == JsonValueKind.String)
                {
                    icon = string.IsNullOrWhiteSpace(iconElement.GetString()) ? null : iconElement.GetString();
                }

                if (!games.TryAdd((localSet, otherSet), new Game(title, localSet, otherSet, icon)))
                {
                    throw new FormatException($"Game at index {index} repeats an earlier game's trophy set pair.");
                }

                index++;
            }

            return games;
        }

        private static List<PsnRecord> ReadEvents(
            JsonElement root,
            Dictionary<(string, string), Game> games,
            TimeZoneInfo zone,
            string localPrefix,
            string otherPrefix,
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
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"Event at index {index} is not an object.");
                }

                var id = RequireString(element, "id", $"Event at index {index} has no id.");

                if (!keys.Add(id))
                {
                    throw new FormatException($"Event at index {index} repeats an earlier event id.");
                }

                var parts = id.Split('|');

                if (parts.Length != 3)
                {
                    throw new FormatException($"Event at index {index} has an id that is not <set>|<set>|<date>.");
                }

                if (!games.TryGetValue((parts[0], parts[1]), out var game)
                    && !games.TryGetValue((parts[1], parts[0]), out game))
                {
                    throw new FormatException($"Event at index {index} names a trophy set pair with no game.");
                }

                var level = RequireString(element, "quality", $"Event at index {index} has no quality.");

                var items = new List<(string Role, string ItemId, string Type, DateTime Utc, DateTime Local)>();
                ReadTrophies(element, localPrefix, MemoryRoles.Local, zone, index, items);
                ReadTrophies(element, otherPrefix, MemoryRoles.Other, zone, index, items);

                if (items.Count == 0)
                {
                    throw new FormatException($"Event at index {index} has no trophies.");
                }

                var pairs = ReadPairs(element, localPrefix, otherPrefix, index);

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
            TimeZoneInfo zone,
            int eventIndex,
            List<(string Role, string ItemId, string Type, DateTime Utc, DateTime Local)> items)
        {
            var which = role == MemoryRoles.Local ? "local" : "other";

            if (!TryGet(element, prefix + "Trophies", out var trophies) || trophies.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"Event at index {eventIndex} has no trophy list for the {which} prefix.");
            }

            foreach (var trophy in trophies.EnumerateArray())
            {
                if (trophy.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"Event at index {eventIndex}: a {which} trophy is not an object.");
                }

                var itemId = ReadId(trophy)
                    ?? throw new FormatException($"Event at index {eventIndex}: a {which} trophy has no id.");
                var type = ReadFirstString(trophy, "type", "trophyType")
                    ?? throw new FormatException($"Event at index {eventIndex}: a {which} trophy has no type.");
                var utcText = ReadFirstString(trophy, "utc")
                    ?? throw new FormatException($"Event at index {eventIndex}: a {which} trophy has no utc time.");
                var timeText = ReadFirstString(trophy, "time")
                    ?? throw new FormatException($"Event at index {eventIndex}: a {which} trophy has no time.");

                var utc = ParseUtc(utcText)
                    ?? throw new FormatException(
                        $"Event at index {eventIndex}: a {which} trophy's utc time is not a whole-second UTC time.");
                var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
                local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

                if (!SourceLocalTimeMatches(timeText, local, utc, zone))
                {
                    throw new FormatException(
                        $"Event at index {eventIndex}: a {which} trophy's utc time converted to the file's " +
                        "timezone does not equal its time field. Stopped.");
                }

                items.Add((role, itemId, type, utc, local));
            }
        }

        private static List<PsnPeriodBuilder.Pair> ReadPairs(JsonElement element, string localPrefix, string otherPrefix, int eventIndex)
        {
            if (!TryGet(element, "matchingTrophies", out var pairsElement))
            {
                throw new FormatException($"Event at index {eventIndex} has no matchingTrophies field.");
            }

            var pairs = new List<PsnPeriodBuilder.Pair>();

            if (pairsElement.ValueKind == JsonValueKind.Null)
            {
                return pairs;
            }

            if (pairsElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"Event at index {eventIndex} has a matchingTrophies field that is not a list.");
            }

            foreach (var pair in pairsElement.EnumerateArray())
            {
                pairs.Add(new PsnPeriodBuilder.Pair(ReadPairSide(pair, localPrefix), ReadPairSide(pair, otherPrefix)));
            }

            return pairs;
        }

        /// <summary>
        /// One side of a pair: either a nested object named after the prefix holding <c>id</c> (or
        /// <c>trophyId</c>) and <c>time</c>, or flat <c>&lt;prefix&gt;Id</c> / <c>&lt;prefix&gt;TrophyId</c> and
        /// <c>&lt;prefix&gt;Time</c>. Anything missing or unreadable gives null: the pair is then
        /// counted as not located, never guessed.
        /// </summary>
        private static (string ItemId, DateTime Local)? ReadPairSide(JsonElement pair, string prefix)
        {
            if (pair.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? id;
            string? time;

            if (TryGet(pair, prefix, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                id = ReadId(nested);
                time = ReadFirstString(nested, "time");
            }
            else
            {
                id = ReadIdField(pair, prefix + "Id") ?? ReadIdField(pair, prefix + "TrophyId");
                time = ReadFirstString(pair, prefix + "Time");
            }

            if (id is null || time is null || ParseLocal(time) is not { } local)
            {
                return null;
            }

            return (id, local);
        }

        private static string? ReadId(JsonElement element) => ReadIdField(element, "id") ?? ReadIdField(element, "trophyId");

        private static string? ReadIdField(JsonElement element, string name)
        {
            if (!TryGet(element, name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString(),
                _ => null,
            };
        }

        private static string? ReadFirstString(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryGet(element, name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }

            return null;
        }

        private static string RequireString(JsonElement element, string name, string message) =>
            ReadFirstString(element, name) ?? throw new FormatException(message);

        /// <summary>A UTC time with an explicit Z or zero offset, whole seconds (a zero fraction is allowed).</summary>
        internal static DateTime? ParseUtc(string text)
        {
            if (!DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var value)
                || value.Offset != TimeSpan.Zero
                || !(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))
                || value.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                return null;
            }

            return DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Utc);
        }

        /// <summary>A local wall-clock reading without zone, whole seconds.</summary>
        internal static DateTime? ParseLocal(string text)
        {
            if (DateTime.TryParseExact(text, LocalTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                return value;
            }

            if (DateTimeOffset.TryParseExact(text, LocalTimeWithOffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
            {
                return withOffset.DateTime;
            }

            return null;
        }

        private static bool SourceLocalTimeMatches(string text, DateTime local, DateTime utc, TimeZoneInfo zone)
        {
            if (DateTime.TryParseExact(text, LocalTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var plain))
            {
                return plain == local;
            }

            if (DateTimeOffset.TryParseExact(text, LocalTimeWithOffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
            {
                return withOffset.DateTime == local && withOffset.Offset == zone.GetUtcOffset(utc);
            }

            return false;
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
