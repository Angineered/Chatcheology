using System.Globalization;
using System.Text;

namespace ArchiveRefresh.PhonePull;

/// <summary>A refusal before or during the pull: the run stops with a counts-only reason (exit 2).</summary>
internal sealed class PullRefusedException(string message) : Exception(message);

/// <summary>How a file reached the new copy.</summary>
internal enum Obtained
{
    Linked,
    Copied,
    Pulled,
}

/// <summary>One manifest line: a file of the new copy as it was written.</summary>
internal sealed record ManifestEntry(string RelativePath, long Size, long MtimeSeconds, Obtained How);

/// <summary>
/// The two files the pull keeps in the destination, beside <c>WhatsApp</c> and <c>Databases</c>:
/// <c>pull-state.tsv</c> while the pull is unfinished (so a rerun continues) and <c>pull-manifest.tsv</c> once it is
/// complete (every file: how obtained, size, whole-second mtime, relative path).
/// </summary>
internal static class PullFiles
{
    internal const string StateFileName = "pull-state.tsv";
    internal const string ManifestFileName = "pull-manifest.tsv";
    internal const string RefusedNamesFileName = "refused-names.tsv";
    internal const string MediaFolderName = "WhatsApp";
    internal const string DatabasesFolderName = "Databases";
    internal const string MsgstoreName = "msgstore.db.crypt15";

    /// <summary>The ending of a local copy still being written (made whole by a rename).</summary>
    internal const string PartialSuffix = ".pull-partial";

    private const string StateHeader = "# Chatcheology phone pull (CH-76): unfinished. Run the same pull again to continue.";
    private const string ManifestHeader = "# Chatcheology phone pull manifest v1: how, size, mtime (unix seconds), path relative to WhatsApp";

    /// <summary>Writes the state file: the previous copy this pull compares with (empty for a full pull).</summary>
    internal static void WriteState(string destination, string? previous)
    {
        var text = new StringBuilder()
            .Append(StateHeader).Append('\n')
            .Append("version\t1\n")
            .Append("previous\t").Append(previous ?? string.Empty).Append('\n')
            .ToString();

        WriteWhole(Path.Combine(destination, StateFileName), text);
    }

    /// <summary>The previous copy recorded in the state file ("" for a full pull).</summary>
    /// <exception cref="PullRefusedException">The state file is not one this tool wrote.</exception>
    internal static string ReadStatePrevious(string destination)
    {
        var lines = File.ReadAllText(Path.Combine(destination, StateFileName), Encoding.UTF8).Split('\n');

        if (lines.Length < 3 || lines[0] != StateHeader || lines[1] != "version\t1" || !lines[2].StartsWith("previous\t", StringComparison.Ordinal))
        {
            throw new PullRefusedException("The destination's pull-state.tsv is damaged. Delete the destination folder and start again.");
        }

        return lines[2]["previous\t".Length..];
    }

    /// <summary>Writes the refused phone names (relative path, reason). Control characters in a name are shown as \n, \t, \xNN.</summary>
    internal static void WriteRefusedNames(string destination, IEnumerable<(string Path, string Reason)> refused)
    {
        Directory.CreateDirectory(destination);
        var text = new StringBuilder("# Phone names the pull refused (CH-76): path relative to WhatsApp, reason\n");

        foreach (var (path, reason) in refused)
        {
            foreach (var c in path)
            {
                text.Append(c switch
                {
                    '\n' => "\\n",
                    '\t' => "\\t",
                    '\r' => "\\r",
                    < ' ' => "\\x" + ((int)c).ToString("x2", CultureInfo.InvariantCulture),
                    _ => c.ToString(),
                });
            }

            text.Append('\t').Append(reason).Append('\n');
        }

        WriteWhole(Path.Combine(destination, RefusedNamesFileName), text.ToString());
    }

    internal static void DeleteState(string destination) => File.Delete(Path.Combine(destination, StateFileName));

    internal static void WriteManifest(string destination, IEnumerable<ManifestEntry> entries)
    {
        var text = new StringBuilder().Append(ManifestHeader).Append('\n');

        foreach (var e in entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            text.Append(e.How switch { Obtained.Linked => "linked", Obtained.Copied => "copied", _ => "pulled" })
                .Append('\t').Append(e.Size.ToString(CultureInfo.InvariantCulture))
                .Append('\t').Append(e.MtimeSeconds.ToString(CultureInfo.InvariantCulture))
                .Append('\t').Append(e.RelativePath).Append('\n');
        }

        WriteWhole(Path.Combine(destination, ManifestFileName), text.ToString());
    }

    /// <summary>Reads a manifest (tests and checks). The path is the last field, so a tab inside it is kept.</summary>
    internal static List<ManifestEntry> ReadManifest(string destination)
    {
        var lines = File.ReadAllText(Path.Combine(destination, ManifestFileName), Encoding.UTF8).Split('\n');

        if (lines.Length == 0 || lines[0] != ManifestHeader)
        {
            throw new FormatException("Not a pull manifest.");
        }

        var entries = new List<ManifestEntry>();

        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var parts = line.Split('\t', 4);
            entries.Add(new ManifestEntry(
                parts[3],
                long.Parse(parts[1], CultureInfo.InvariantCulture),
                long.Parse(parts[2], CultureInfo.InvariantCulture),
                parts[0] switch
                {
                    "linked" => Obtained.Linked,
                    "copied" => Obtained.Copied,
                    "pulled" => Obtained.Pulled,
                    _ => throw new FormatException("Not a pull manifest."),
                }));
        }

        return entries;
    }

    /// <summary>The file's mtime in whole unix seconds (what <c>adb pull -a</c> sets: the phone's seconds).</summary>
    internal static long MtimeSeconds(FileInfo file) => new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds();

    private static void WriteWhole(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
