using System.Text;

namespace ArchiveRefresh;

/// <summary>
/// What an apply has done so far (<see cref="FileName"/> in the plan folder): each backup, each step begun and each step
/// done, with the state of the refresh's files at that point. A rerun after a stop reads it to skip finished steps and to
/// check that nothing else changed the files since.
/// </summary>
/// <remarks>
/// Appended one line at a time and flushed to disk. Holds step names, the backup folder's own name (a timestamp) and
/// file sizes and SHA-256 digests; never a path or any content.
/// </remarks>
internal sealed class RefreshJournal
{
    internal const string FileName = "apply-progress.tsv";
    private const string Magic = "chatcheology-archive-refresh-progress";
    private const string Version = "1";

    private readonly string _path;

    private RefreshJournal(string path, string planSha)
    {
        _path = path;
        PlanSha256 = planSha;
    }

    internal string PlanSha256 { get; }

    internal List<(string Kind, string Name, string State)> Entries { get; } = [];

    /// <summary>Steps done, in order.</summary>
    internal IEnumerable<string> Done => Entries.Where(e => e.Kind == "done").Select(e => e.Name);

    /// <summary>The step begun last and not done, when the last apply stopped inside it.</summary>
    internal string? Interrupted =>
        Entries.LastOrDefault(e => e.Kind is "begin" or "done") is ("begin", var name, _) ? name : null;

    /// <summary>The state recorded last (after a backup, a begin or a done), or null when nothing is recorded.</summary>
    internal string? LastState => Entries.Count == 0 ? null : Entries[^1].State;

    /// <exception cref="FormatException">The file is not this plan's progress file.</exception>
    internal static RefreshJournal OpenOrCreate(string planFolder, string planSha)
    {
        var path = Path.Combine(planFolder, FileName);
        var journal = new RefreshJournal(path, planSha);

        if (!File.Exists(path))
        {
            return journal;
        }

        var lines = File.ReadAllText(path, Encoding.UTF8).Split('\n');

        if (lines.Length < 2 || lines[0] != $"{Magic}\t{Version}\t{planSha}")
        {
            throw new FormatException("The apply progress file in the plan folder is not this plan's.");
        }

        foreach (var line in lines[1..])
        {
            if (line.Length == 0)
            {
                continue;
            }

            // A line cut short by a stop mid-write ends without its state; it is ignored, as never written.
            if (line.Split('\t') is [var kind and ("backup" or "begin" or "done"), var name, var state]
                && state.Length > 0 && state.EndsWith(';') is false)
            {
                try
                {
                    RefreshFiles.ParseState(state);
                    journal.Entries.Add((kind, name, state));
                }
                catch (FormatException)
                {
                    // As above: an incomplete last line.
                }
            }
        }

        return journal;
    }

    internal void Record(string kind, string name, string state)
    {
        var header = File.Exists(_path) ? "" : $"{Magic}\t{Version}\t{PlanSha256}\n";

        using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(header + $"{kind}\t{name}\t{state}\n");
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        Entries.Add((kind, name, state));
    }
}
