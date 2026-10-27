using System.Globalization;
using System.Text.Json;

namespace Chatcheology.Data.Memories.Psn
{
    /// <summary>One trophy definition, values exactly as the source file holds them.</summary>
    /// <param name="SetId">The trophy set (<c>npCommunicationId</c>).</param>
    /// <param name="ItemId">The trophy ID, as integer text (the spelling <c>EntryItem.ItemId</c> uses).</param>
    /// <param name="ItemType">The trophy type.</param>
    /// <param name="Name">The name, or null when the file has null.</param>
    /// <param name="Detail">The description, or null.</param>
    /// <param name="Hidden">Whether the trophy is hidden, or null.</param>
    /// <param name="GroupId">The trophy group, or null.</param>
    /// <param name="IconUrl">The icon URL, or null.</param>
    public sealed record PsnTrophyDefinition(
        string SetId,
        string ItemId,
        string ItemType,
        string? Name,
        string? Detail,
        bool? Hidden,
        string? GroupId,
        string? IconUrl);

    /// <summary>A read definitions file.</summary>
    /// <param name="SetCount">Trophy sets in the file.</param>
    /// <param name="Definitions">Every definition, in file order.</param>
    public sealed record PsnTrophyDefinitions(int SetCount, IReadOnlyList<PsnTrophyDefinition> Definitions);

    /// <summary>
    /// Reads a PSN trophy definitions file (format <see cref="Format"/> only). Strict like
    /// <see cref="PsnSharedActivityReader"/>: anything outside the one known shape stops the read
    /// with the set and trophy index.
    /// </summary>
    /// <remarks>
    /// Root: <c>format</c>, <c>fetchedUtc</c> (text, not used), <c>sets</c>. Set:
    /// <c>npCommunicationId</c> (non-empty text), <c>npServiceName</c> and <c>trophySetVersion</c>
    /// (text or null, not used), <c>trophies</c>. Trophy: <c>trophyId</c> (integer, 0 or more),
    /// <c>trophyType</c> (non-empty text), <c>trophyName</c>, <c>trophyDetail</c>,
    /// <c>trophyIconUrl</c>, <c>trophyGroupId</c> (text or null), <c>trophyHidden</c> (true, false
    /// or null). Every listed field must be present; null is allowed only where stated. Text is kept
    /// as written (no trimming). A repeated set or (set, trophy) stops the read. Other fields are
    /// ignored. Field names match case-insensitively. No message quotes a value from the file.
    /// </remarks>
    public static class PsnTrophyDefinitionsReader
    {
        /// <summary>The only format accepted.</summary>
        public const string Format = "chatcheology-psn-trophy-definitions-v1";

        /// <summary>Reads the file at <paramref name="path"/>.</summary>
        public static PsnTrophyDefinitions Read(string path)
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
                throw new InvalidOperationException($"The definitions file could not be read ({exception.GetType().Name}).");
            }

            return Parse(bytes);
        }

        /// <summary>Reads a file already in memory.</summary>
        /// <exception cref="FormatException">The file is not the accepted shape. Nothing is guessed.</exception>
        public static PsnTrophyDefinitions Parse(ReadOnlyMemory<byte> json)
        {
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                throw new FormatException("The definitions file is not valid JSON.");
            }

            using (document)
            {
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object
                    || !TryGet(root, "format", out var format)
                    || format.ValueKind != JsonValueKind.String
                    || !string.Equals(format.GetString(), Format, StringComparison.Ordinal))
                {
                    throw new FormatException($"The file is not format {Format}; no other format is accepted.");
                }

                if (!TryGet(root, "fetchedUtc", out var fetched) || fetched.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("The file has no fetchedUtc text.");
                }

                if (!TryGet(root, "sets", out var sets) || sets.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException("The file has no sets array.");
                }

                var definitions = new List<PsnTrophyDefinition>();
                var setIds = new HashSet<string>(StringComparer.Ordinal);
                var setIndex = 0;

                foreach (var set in sets.EnumerateArray())
                {
                    var where = $"Set at index {setIndex.ToString(CultureInfo.InvariantCulture)}";

                    if (set.ValueKind != JsonValueKind.Object)
                    {
                        throw new FormatException($"{where} is not an object.");
                    }

                    var setId = RequireText(set, "npCommunicationId", where);
                    OptionalText(set, "npServiceName", where);
                    OptionalText(set, "trophySetVersion", where);

                    if (!setIds.Add(setId))
                    {
                        throw new FormatException($"{where} repeats an earlier set.");
                    }

                    if (!TryGet(set, "trophies", out var trophies) || trophies.ValueKind != JsonValueKind.Array)
                    {
                        throw new FormatException($"{where} has no trophies array.");
                    }

                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    var trophyIndex = 0;

                    foreach (var trophy in trophies.EnumerateArray())
                    {
                        var at = $"{where}, trophy at index {trophyIndex.ToString(CultureInfo.InvariantCulture)}";

                        if (trophy.ValueKind != JsonValueKind.Object)
                        {
                            throw new FormatException($"{at} is not an object.");
                        }

                        var itemId = RequireId(trophy, at);

                        if (!ids.Add(itemId))
                        {
                            throw new FormatException($"{at} repeats an earlier trophy id in the set.");
                        }

                        definitions.Add(new PsnTrophyDefinition(
                            setId,
                            itemId,
                            RequireText(trophy, "trophyType", at),
                            OptionalText(trophy, "trophyName", at),
                            OptionalText(trophy, "trophyDetail", at),
                            OptionalBool(trophy, "trophyHidden", at),
                            OptionalText(trophy, "trophyGroupId", at),
                            OptionalText(trophy, "trophyIconUrl", at)));

                        trophyIndex++;
                    }

                    setIndex++;
                }

                return new PsnTrophyDefinitions(setIndex, definitions);
            }
        }

        private static string RequireId(JsonElement element, string at)
        {
            if (TryGet(element, "trophyId", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var number)
                && number >= 0)
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }

            throw new FormatException($"{at} has no trophyId that is a whole number, 0 or more.");
        }

        private static string RequireText(JsonElement element, string name, string at)
        {
            if (TryGet(element, name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }

            throw new FormatException($"{at} has no {name} text.");
        }

        private static string? OptionalText(JsonElement element, string name, string at)
        {
            if (!TryGet(element, name, out var value))
            {
                throw new FormatException($"{at} has no {name} field (null is allowed, a missing field is not).");
            }

            return value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                _ => throw new FormatException($"{at} has a {name} that is neither text nor null."),
            };
        }

        private static bool? OptionalBool(JsonElement element, string name, string at)
        {
            if (!TryGet(element, name, out var value))
            {
                throw new FormatException($"{at} has no {name} field (null is allowed, a missing field is not).");
            }

            return value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new FormatException($"{at} has a {name} that is neither true, false nor null."),
            };
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
