using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chatcheology.Tools.MemoryImport
{
    /// <summary>One row of Angie's decisions file.</summary>
    /// <param name="Row">The spreadsheet line the row starts on (the header is line 1).</param>
    /// <param name="Title">The game title as written (for the out-folder files only).</param>
    /// <param name="Status"><see cref="PsnDecisionsFile.Together"/>, <see cref="PsnDecisionsFile.Uncertain"/> or <see cref="PsnDecisionsFile.Excluded"/>.</param>
    /// <param name="SetsA">Account a's trophy set ids in the row.</param>
    /// <param name="SetsB">Account b's trophy set ids in the row.</param>
    public sealed record PsnDecision(int Row, string Title, string Status, IReadOnlySet<string> SetsA, IReadOnlySet<string> SetsB)
    {
        /// <summary>True when the row covers the pair (set a in its a cell and set b in its b cell).</summary>
        public bool Matches(string setA, string setB) => SetsA.Contains(setA) && SetsB.Contains(setB);
    }

    /// <summary>
    /// Angie's hand-kept decisions file for psn-fetch: which shared titles were played together.
    /// CSV, UTF-8 (a byte order mark is allowed), a header row, then one row per game. Columns (names
    /// match case-insensitively, other columns are ignored): <c>game_title</c>, <c>status</c>
    /// (together, uncertain or excluded), <c>&lt;account a lower case&gt;_trophy_set_ids</c> and
    /// <c>&lt;account b lower case&gt;_trophy_set_ids</c>. A set-id cell may hold several ids separated by
    /// <c>;</c> (spaces around them allowed), for example the PS3 and PS4 versions. Quoted fields
    /// follow RFC 4180 (commas, quotes doubled, line breaks). Blank lines are skipped.
    /// </summary>
    /// <remarks>Messages never quote a value from the file or an account name: row numbers and column roles only.</remarks>
    public static partial class PsnDecisionsFile
    {
        /// <summary>Played together: the game is added.</summary>
        public const string Together = "together";

        /// <summary>Not sure: not added.</summary>
        public const string Uncertain = "uncertain";

        /// <summary>Not played together: not added.</summary>
        public const string Excluded = "excluded";

        /// <summary>The title column.</summary>
        public const string TitleColumn = "game_title";

        /// <summary>The status column.</summary>
        public const string StatusColumn = "status";

        /// <summary>The set-id column name for an account.</summary>
        public static string SetColumn(string account) => account.ToLowerInvariant() + "_trophy_set_ids";

        /// <summary>The last-updated date column name for an account (written in the to-review file only).</summary>
        public static string LastUpdatedColumn(string account) => account.ToLowerInvariant() + "_last_updated";

        /// <summary>Reads the file.</summary>
        /// <exception cref="FormatException">The file is not the accepted shape; nothing is guessed.</exception>
        public static List<PsnDecision> Parse(ReadOnlySpan<byte> bytes, string accountA, string accountB)
        {
            if (bytes.StartsWith(Encoding.UTF8.Preamble))
            {
                bytes = bytes[Encoding.UTF8.Preamble.Length..];
            }

            string text;

            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new FormatException("The decisions file is not UTF-8 text.");
            }

            var records = ReadRecords(text);

            if (records.Count == 0)
            {
                throw new FormatException("The decisions file has no header row.");
            }

            var header = records[0].Fields.Select(name => name.Trim()).ToList();
            var title = Column(header, TitleColumn, "the game_title column");
            var status = Column(header, StatusColumn, "the status column");
            var setsA = Column(header, SetColumn(accountA), "account a's trophy set id column (<account a in lower case>_trophy_set_ids)");
            var setsB = Column(header, SetColumn(accountB), "account b's trophy set id column (<account b in lower case>_trophy_set_ids)");
            var decisions = new List<PsnDecision>();

            for (var index = 1; index < records.Count; index++)
            {
                var (record, line) = records[index];

                if (record.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                var where = $"Decisions file line {line.ToString(CultureInfo.InvariantCulture)}";
                string Cell(int column) => column < record.Count ? record[column] : string.Empty;
                var rowStatus = Cell(status).Trim().ToLowerInvariant();

                if (rowStatus is not (Together or Uncertain or Excluded))
                {
                    throw new FormatException($"{where}: the status is not together, uncertain or excluded.");
                }

                decisions.Add(new PsnDecision(line, Cell(title).Trim(), rowStatus, Ids(Cell(setsA), where, "a"), Ids(Cell(setsB), where, "b")));
            }

            return decisions;
        }

        /// <summary>Writes rows as CSV text (UTF-8 with a byte order mark, so a spreadsheet reads it as UTF-8).</summary>
        public static byte[] Write(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
        {
            var builder = new StringBuilder();
            builder.Append(string.Join(',', header.Select(Quote))).Append("\r\n");

            foreach (var row in rows)
            {
                builder.Append(string.Join(',', row.Select(Quote))).Append("\r\n");
            }

            return [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(builder.ToString())];
        }

        private static string Quote(string value) =>
            value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || value != value.Trim()
                ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : value;

        /// <summary>The ids in a cell: split on ';', trimmed, upper case; each must be NPWRnnnnn_nn.</summary>
        private static HashSet<string> Ids(string cell, string where, string side)
        {
            var ids = cell.Split(';').Select(id => id.Trim().ToUpperInvariant()).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);

            if (ids.Any(id => !SetIdPattern().IsMatch(id)))
            {
                throw new FormatException(
                    $"{where}: account {side}'s trophy set ids are not all NPWRnnnnn_nn (separate several with ';'). Nothing has been fetched.");
            }

            return ids;
        }

        [GeneratedRegex("^NPWR[0-9]{5}_[0-9]{2}$")]
        private static partial Regex SetIdPattern();

        private static int Column(List<string> header, string name, string what)
        {
            var found = header.Select((column, index) => (column, index))
                .Where(pair => string.Equals(pair.column, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return found.Count switch
            {
                1 => found[0].index,
                0 => throw new FormatException($"The decisions file has no {what}. Nothing has been fetched."),
                _ => throw new FormatException($"The decisions file has {what} twice."),
            };
        }

        /// <summary>RFC 4180 records: comma separated, double-quoted fields with "" for a quote, CRLF or LF.</summary>
        private static List<(List<string> Fields, int Line)> ReadRecords(string text)
        {
            var records = new List<(List<string> Fields, int Line)>();
            var record = new List<string>();
            var line = 1;
            var recordLine = 1;
            var field = new StringBuilder();
            var quoted = false;
            var fieldStarted = false;
            var afterQuote = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            quoted = false;
                            afterQuote = true;
                        }
                    }
                    else
                    {
                        if (ch == '\n' || (ch == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
                        {
                            line++;
                        }

                        field.Append(ch);
                    }

                    continue;
                }

                switch (ch)
                {
                    case '"' when field.Length == 0 && !fieldStarted:
                        quoted = true;
                        fieldStarted = true;
                        break;

                    case '"':
                        throw new FormatException(
                            $"Decisions file line {line.ToString(CultureInfo.InvariantCulture)}: a quote inside an unquoted field.");

                    case ',':
                        record.Add(field.ToString());
                        field.Clear();
                        fieldStarted = false;
                        afterQuote = false;
                        break;

                    case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                        break;

                    case '\r' or '\n':
                        record.Add(field.ToString());
                        records.Add((record, recordLine));
                        line++;
                        recordLine = line;
                        record = [];
                        field.Clear();
                        fieldStarted = false;
                        afterQuote = false;
                        break;

                    default:
                        if (afterQuote)
                        {
                            throw new FormatException(
                                $"Decisions file line {line.ToString(CultureInfo.InvariantCulture)}: text after a closing quote.");
                        }

                        field.Append(ch);
                        fieldStarted = true;
                        break;
                }
            }

            if (quoted)
            {
                throw new FormatException("The decisions file ends inside a quoted field.");
            }

            if (field.Length > 0 || fieldStarted || record.Count > 0)
            {
                record.Add(field.ToString());
                records.Add((record, recordLine));
            }

            return records;
        }
    }
}
