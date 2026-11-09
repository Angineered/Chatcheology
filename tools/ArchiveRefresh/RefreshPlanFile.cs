using System.Globalization;
using System.Text;

namespace ArchiveRefresh;

/// <summary>
/// The plan file (<see cref="FileName"/> in the plan folder): every parameter, every input's fingerprint and every
/// sub-manifest's SHA-256. Its own SHA-256 is the one plan SHA-256 Angie approves.
/// </summary>
/// <remarks>
/// Private, like the sub-manifests: it holds ids and digests, never a path, a name or any text (paths and account
/// names only as SHA-256 digests). UTF-8, LF, tab-separated, one fact per line.
/// </remarks>
internal sealed class RefreshPlanFile
{
    internal const string FileName = "refresh-plan.tsv";
    internal const string Magic = "chatcheology-archive-refresh-plan";
    internal const string Version = "1";

    internal const string NewerManifest = "newer-manifest.tsv";
    internal const string CaptionsManifest = "captions-manifest.tsv";
    internal const string PsnBuilt = "psn-shared-activity.json";

    internal const string StepNewer = "newer";
    internal const string StepCaptions = "captions";
    internal const string StepPsn = "psn";

    internal required long ConversationID { get; init; }
    internal required long ChatID { get; init; }
    internal required long LocalParticipantID { get; init; }
    internal required int ExpectRestored { get; init; }
    internal required int ExpectMissing { get; init; }
    internal required bool Psn { get; init; }

    /// <summary>Path digests, by label (workspace, msgstore, media-root, and the PSN files when included).</summary>
    internal required SortedDictionary<string, string> Paths { get; init; }

    /// <summary>Digests of the PSN account names and collection, when included.</summary>
    internal required SortedDictionary<string, string> Values { get; init; }

    /// <summary>Every input's fingerprint at plan time, by label (the writable files and the read-only inputs).</summary>
    internal required SortedDictionary<string, FileFingerprint> Inputs { get; init; }

    /// <summary>Each sub-manifest's file name and SHA-256, by step.</summary>
    internal required SortedDictionary<string, (string File, string Sha256)> Manifests { get; init; }

    internal byte[] ToBytes()
    {
        var text = new StringBuilder();

        void Line(params string[] fields) => text.Append(string.Join('\t', fields)).Append('\n');
        string N(long value) => value.ToString(CultureInfo.InvariantCulture);

        Line(Magic, Version);
        Line("# Archive refresh plan (CH-71 step 5). Private: ids and digests only. Its SHA-256 approves exactly this refresh.");
        Line("conversation", N(ConversationID));
        Line("chat", N(ChatID));
        Line("local-participant", N(LocalParticipantID));
        Line("expect-restored", N(ExpectRestored));
        Line("expect-missing", N(ExpectMissing));
        Line("psn", Psn ? "yes" : "no");

        foreach (var (label, digest) in Paths)
        {
            Line("path", label, digest);
        }

        foreach (var (label, digest) in Values)
        {
            Line("value", label, digest);
        }

        foreach (var (label, fingerprint) in Inputs)
        {
            Line("input", label, fingerprint.Text);
        }

        foreach (var (step, (file, sha)) in Manifests)
        {
            Line("manifest", step, file, sha);
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <exception cref="FormatException">The bytes are not a plan file of this version.</exception>
    internal static RefreshPlanFile Parse(byte[] bytes)
    {
        var lines = Encoding.UTF8.GetString(bytes).Split('\n');

        if (lines.Length < 3 || lines[0] != $"{Magic}\t{Version}" || lines[^1] != "")
        {
            throw new FormatException("The plan file is not an archive refresh plan of this version.");
        }

        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var inputs = new SortedDictionary<string, FileFingerprint>(StringComparer.Ordinal);
        var manifests = new SortedDictionary<string, (string, string)>(StringComparer.Ordinal);

        foreach (var line in lines[1..^1])
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var f = line.Split('\t');

            switch (f)
            {
                case ["path", var label, var digest]: paths.Add(label, digest); break;
                case ["value", var label, var digest]: values.Add(label, digest); break;
                case ["input", var label, var fingerprint]: inputs.Add(label, FileFingerprint.Parse(fingerprint)); break;
                case ["manifest", var step, var file, var sha]: manifests.Add(step, (file, sha)); break;
                case [var key, var value]: scalars.Add(key, value); break;
                default: throw new FormatException("The plan file has a malformed line.");
            }
        }

        long Long(string key) =>
            scalars.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new FormatException("The plan file lacks a parameter.");

        return new RefreshPlanFile
        {
            ConversationID = Long("conversation"),
            ChatID = Long("chat"),
            LocalParticipantID = Long("local-participant"),
            ExpectRestored = checked((int)Long("expect-restored")),
            ExpectMissing = checked((int)Long("expect-missing")),
            Psn = scalars.TryGetValue("psn", out var psn) && psn == "yes",
            Paths = paths,
            Values = values,
            Inputs = inputs,
            Manifests = manifests,
        };
    }
}
