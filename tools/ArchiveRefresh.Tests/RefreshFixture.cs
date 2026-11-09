using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;
using Chatcheology.Tests.Memories;
using Chatcheology.Tools.MemoryImport;
using Chatcheology.Tools.MemoryImport.Tests;
using Microsoft.Data.Sqlite;
using MsgstoreImport.Tests;

namespace ArchiveRefresh.Tests;

/// <summary>
/// One synthetic month for the refresh: a workspace and msgstore that line up up to the workspace's last day, newer
/// messages after it (text, a restored picture with a caption, a poll), a workspace picture whose caption is only in the
/// msgstore, a copy of the phone's WhatsApp folder, and PSN inputs (last month's shared-activity file already imported,
/// this month's fetch output with one new game day, a game list and trophy definitions). Everything is invented.
/// </summary>
internal sealed class RefreshFixture : IDisposable
{
    internal const string WorkspaceCaption = "SYNTHSECRET caption of a workspace picture";
    internal const string NewerCaption = "SYNTHSECRET caption of a newer picture";
    internal const string NewerText = "SYNTHSECRET newer text";
    internal const string PollText = "SYNTHSECRET poll question";

    private const string Set = "ZZSET00042_00";
    private const string Icon = "https://image.api.playstation.com/zz/invented-game.png";

    private static readonly DateOnly Earlier = SyntheticData.Day.AddDays(-1);
    private static readonly DateOnly Next = SyntheticData.Day.AddDays(1);

    private int _clock;

    internal RefreshFixture(bool psn = true)
    {
        D = new SyntheticData();
        MediaRoot = Path.Combine(D.DirectoryPath, "phone-copy", "WhatsApp");
        Private = Path.Combine(D.DirectoryPath, "private");
        BackupRoot = Path.Combine(D.DirectoryPath, "backups");
        Directory.CreateDirectory(MediaRoot);
        Directory.CreateDirectory(Private);
        Directory.CreateDirectory(BackupRoot);

        // The shared past: an earlier day and the workspace's last day, lined up exactly.
        D.AddWorkspaceMessage("10:00:00", outgoing: true, Earlier, content: "SYNTHSECRET earlier one");
        D.AddWorkspaceMessage("10:01:00", outgoing: false, Earlier, content: "SYNTHSECRET earlier two");
        D.AddNonMediaMessage("10:00:05", true, 0, "SYNTHSECRET earlier one", Earlier);
        D.AddNonMediaMessage("10:01:05", false, 0, "SYNTHSECRET earlier two", Earlier);

        D.AddWorkspaceMessage("09:00:00", outgoing: true, content: "SYNTHSECRET hello");
        WorkspacePicture = D.AddAttachment("09:05:00", outgoing: false);
        D.AddWorkspaceMessage("09:10:00", outgoing: false, content: "SYNTHSECRET bye");

        D.AddNonMediaMessage("09:00:10", true, 0, "SYNTHSECRET hello");
        var shared = D.AddRecord("09:05:00", false, "IMG-SYNTH-shared.jpg", 10, D.UnknownHash(), "SYNTHKEYSHARED");
        D.Msgstore($"UPDATE message SET text_data = '{WorkspaceCaption}' WHERE _id = {shared.ToString(CultureInfo.InvariantCulture)};");
        D.AddNonMediaMessage("09:10:00", false, 0, "SYNTHSECRET bye");

        // Newer messages: a text, a restored picture with a caption, a poll.
        D.AddNonMediaMessage("08:00:00", false, 0, NewerText, Next);
        var picture = Encoding.UTF8.GetBytes("invented newer picture bytes");
        var newer = D.AddRecord("08:01:00", true, "IMG-SYNTH-newer.jpg", picture.Length,
            Convert.ToBase64String(SHA256.HashData(picture)), "SYNTHKEYNEWER", date: Next);
        D.Msgstore($"UPDATE message SET text_data = '{NewerCaption}' WHERE _id = {newer.ToString(CultureInfo.InvariantCulture)};");
        var file = Path.Combine(MediaRoot, "Media", "WhatsApp Images", "IMG-SYNTH-newer.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, picture);
        D.AddNonMediaMessage("08:02:00", false, 66, PollText, Next);

        D.CloseAll();

        if (psn)
        {
            BuildPsn();
        }
    }

    internal SyntheticData D { get; }

    internal long WorkspacePicture { get; }

    internal string MediaRoot { get; }

    /// <summary>A private folder for plan folders and archives, outside the workspace folder's own files.</summary>
    internal string Private { get; }

    internal string BackupRoot { get; }

    internal bool Psn => Timestamps is not null;

    internal string? Timestamps { get; private set; }

    internal string? GameList { get; private set; }

    internal string? Current { get; private set; }

    internal string? Definitions { get; private set; }

    internal string MemoriesPath => Path.Combine(D.DirectoryPath, MemoriesDatabase.FileName);

    /// <summary>Every value that must never reach the screen.</summary>
    internal IEnumerable<string> Secrets =>
    [
        "SYNTHSECRET", "SYNTHKEY", "SYNTHTEXT", "IMG-SYNTH", "Alex", SyntheticTrophyBatch.AccountA,
        SyntheticTrophyBatch.AccountB, "ZZSET", "Invented Game", "https", D.DirectoryPath, Path.GetFileName(D.DirectoryPath),
    ];

    /// <summary>Last month's file already imported into memories.db; this month's fetch adds one more game day.</summary>
    private void BuildPsn()
    {
        var psnFolder = Path.Combine(D.DirectoryPath, "psn-inputs");
        Directory.CreateDirectory(psnFolder);

        BatchGame Game(params DateTime[] days) => new()
        {
            SetA = Set,
            SetB = Set,
            Title = "Invented Game",
            Icon = Icon,
            A = days.Select(d => new BatchTrophy(1 + Array.IndexOf(days, d), d)).ToList(),
            B = days.Select(d => new BatchTrophy(1 + Array.IndexOf(days, d), d.AddSeconds(30))).ToList(),
        };

        var lastMonth = new SyntheticTrophyBatch();
        lastMonth.Games.Add(Game(SyntheticTrophyBatch.Utc(2026, 9, 5, 18, 0)));
        var built = PsnSharedActivityBuilder.Build(
            [Encoding.UTF8.GetBytes(lastMonth.TimestampsJson())], Encoding.UTF8.GetBytes(lastMonth.ManifestJson()),
            SyntheticTrophyBatch.AccountA, SyntheticTrophyBatch.AccountB);
        Current = Path.Combine(psnFolder, "zz-current-activity.json");
        File.WriteAllBytes(Current, built.Json);

        var thisMonth = new SyntheticTrophyBatch();
        thisMonth.Games.Add(Game(SyntheticTrophyBatch.Utc(2026, 9, 5, 18, 0), SyntheticTrophyBatch.Utc(2026, 10, 9, 19, 0)));
        (Timestamps, GameList) = thisMonth.WriteTo(psnFolder, "zz-month");

        var definitions = new SyntheticTrophyDefinitions();
        definitions.Sets.Add(new SyntheticDefinitionSet(Set,
        [
            new SyntheticDefinition(1, "bronze", "Invented First"),
            new SyntheticDefinition(2, "bronze", "Invented Second"),
        ]));
        Definitions = definitions.WriteTo(psnFolder, "zz-definitions.json");

        // Last month's import, as the memories file holds it before this refresh.
        MemoriesDatabase.Create(MemoriesPath, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        new PsnImporter().Import(MemoriesPath,
            PsnSharedActivityReader.Parse(built.Json, SyntheticTrophyBatch.AccountA, SyntheticTrophyBatch.AccountB, SyntheticTrophyBatch.AccountA));
        SqliteConnection.ClearAllPools();
    }

    internal string NewPlanFolder() => Path.Combine(Private, "plan-" + Guid.NewGuid().ToString("N")[..8]);

    internal string NewArchiveOut() => Path.Combine(Private, "archive-" + Guid.NewGuid().ToString("N")[..8]);

    internal List<string> InputArgs()
    {
        var args = new List<string> { "--workspace", D.WorkspacePath, "--msgstore", D.MsgstorePath, "--media-root", MediaRoot };

        if (Psn)
        {
            args.AddRange(
            [
                "--psn-timestamps", Timestamps!, "--psn-manifest", GameList!, "--psn-account-a", SyntheticTrophyBatch.AccountA,
                "--psn-account-b", SyntheticTrophyBatch.AccountB, "--psn-local", SyntheticTrophyBatch.AccountA,
                "--psn-current", Current!, "--psn-definitions", Definitions!,
            ]);
        }

        return args;
    }

    internal (int Exit, string Out, string Err) Run(IEnumerable<string> args, RefreshHooks? hooks = null)
    {
        D.CloseAll();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = RefreshTool.Run([.. args], stdout, stderr, hooks ?? Hooks());
        var (o, e) = (stdout.ToString(), stderr.ToString());

        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, o, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, e, StringComparison.OrdinalIgnoreCase);
        }

        return (exit, o, e);
    }

    /// <summary>Hooks with a clock that moves a minute per backup, so two applies never share a backup folder name.</summary>
    internal RefreshHooks Hooks(Action<string>? before = null, Action<string>? after = null) => new()
    {
        Now = () => new DateTime(2026, 11, 9, 10, 0, 0).AddMinutes(_clock++),
        BeforeStep = before,
        AfterStep = after,
    };

    internal (int Exit, string Out, string Err, string Sha, string Folder) Plan(string? folder = null, int expectRestored = 0)
    {
        folder ??= NewPlanFolder();

        var (exit, o, e) = Run(
        [
            "plan", .. InputArgs(), "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
            "--local-participant", "1", "--expect-restored", expectRestored.ToString(CultureInfo.InvariantCulture),
            "--expect-missing", "0", "--plan-folder", folder,
        ]);

        var sha = o.Split('\n').FirstOrDefault(l => l.StartsWith("PLAN SHA-256: ", StringComparison.Ordinal))?["PLAN SHA-256: ".Length..].Trim() ?? "";

        return (exit, o, e, sha, folder);
    }

    internal (int Exit, string Out, string Err) Apply(string folder, string sha, string archiveOut, RefreshHooks? hooks = null) =>
        Run(["apply", .. InputArgs(), "--plan-folder", folder, "--plan-sha256", sha, "--backup-root", BackupRoot, "--archive-out", archiveOut], hooks);

    /// <summary>Every input file's SHA-256 (the workspace folder's files, the msgstore, the media and PSN files), by path.</summary>
    internal Dictionary<string, string> Snapshot()
    {
        SqliteConnection.ClearAllPools();

        var files = Directory.GetFiles(D.DirectoryPath, "*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(MediaRoot, "*", SearchOption.AllDirectories))
            .Concat(Psn ? Directory.GetFiles(Path.GetDirectoryName(Timestamps)!) : []);

        return files.ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose() => D.Dispose();
}
