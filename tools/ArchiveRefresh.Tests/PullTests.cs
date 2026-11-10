using System.Security.Cryptography;
using System.Text;
using ArchiveRefresh.PhonePull;

namespace ArchiveRefresh.Tests;

/// <summary>
/// A fake adb over a synthetic "phone" folder in a temp directory: <c>devices</c>, the one listing command (answered as
/// toybox <c>stat -c '%f/%s/%Y/%n'</c> would), and <c>pull -a</c> (copies, then sets the whole-second mtime as adb does).
/// </summary>
internal sealed class FakeAdb(string phoneFolder) : IAdbRunner
{
    internal string DevicesOutput { get; set; } = "List of devices attached\nSYNTHSERIAL01\tdevice\n\n";

    /// <summary>When set, the pull call with this number (1-based) stops after copying its first file.</summary>
    internal int? FailOnPull { get; set; }

    internal int PullCalls { get; private set; }
    internal List<string> PulledRemotes { get; } = [];
    internal List<IReadOnlyList<string>> Calls { get; } = [];

    public AdbResult Run(IReadOnlyList<string> arguments)
    {
        Calls.Add(arguments);
        var args = arguments.ToList();

        if (args.Count >= 2 && args[0] == "-s")
        {
            args = args[2..];
        }

        switch (args[0])
        {
            case "start-server": return new AdbResult(0, "", "");
            case "devices": return new AdbResult(0, DevicesOutput, "");
            case "shell":
                Assert.Equal(AdbCommands.ListCommand, args[1]);

                return new AdbResult(0, Listing(), "");
            case "pull":
                Assert.Equal("-a", args[1]);
                PullCalls++;
                var local = args[^1];
                Assert.True(Directory.Exists(local), "adb pull with several files needs an existing local folder");

                var remotes = args[2..^1];

                for (var i = 0; i < remotes.Count; i++)
                {
                    var source = PhonePath(remotes[i]);
                    var target = Path.Combine(local, Path.GetFileName(source));
                    File.Copy(source, target, overwrite: true);
                    File.SetLastWriteTimeUtc(target, DateTimeOffset.FromUnixTimeSeconds(Seconds(source)).UtcDateTime);
                    PulledRemotes.Add(remotes[i]);

                    if (FailOnPull == PullCalls)
                    {
                        return new AdbResult(1, "", "adb: error: device offline");
                    }
                }

                return new AdbResult(0, $"{remotes.Count} files pulled", "");
            default:
                throw new InvalidOperationException("Unexpected adb command.");
        }
    }

    private string PhonePath(string remote)
    {
        Assert.StartsWith(AdbCommands.PhoneRoot + "/", remote, StringComparison.Ordinal);

        return Path.Combine(phoneFolder, remote[(AdbCommands.PhoneRoot.Length + 1)..].Replace('/', '\\'));
    }

    private static long Seconds(string path) => new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();

    private string Listing()
    {
        var text = new StringBuilder();
        text.Append("41f9/3452/1700000000/").Append(AdbCommands.PhoneRoot).Append('\n');

        foreach (var entry in Directory.EnumerateFileSystemEntries(phoneFolder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(phoneFolder, entry).Replace('\\', '/');
            var isFolder = Directory.Exists(entry);
            text.Append(isFolder ? "41f9/3452/" : "81b0/")
                .Append(isFolder ? "" : new FileInfo(entry).Length + "/")
                .Append(isFolder ? 1700000000 : Seconds(entry))
                .Append('/').Append(AdbCommands.PhoneRoot).Append('/').Append(relative).Append('\n');
        }

        return text.ToString();
    }
}

/// <summary>A linker that can never link, as for two volumes: every same file is copied locally.</summary>
internal sealed class NoLinks : IFileLinker
{
    public bool LikelySameVolume(string existingFolder, string newFolder) => false;

    public bool TryLink(string existingPath, string newPath) => false;
}

/// <summary>The phone pull (CH-76) on synthetic data only: a fake adb, temp folders, invented names.</summary>
public sealed class PullTests : IDisposable
{
    private static readonly string[] Names =
    [
        "Media/WhatsApp Images/IMG-20990101-WA0001.jpg",
        "Media/WhatsApp Images/Sent/IMG-20990101-WA0002.jpg",
        "Media/WhatsApp Documents/Synthetic plan 'v2' (final).pdf",
        "Media/WhatsApp Documents/Ünïcödé naïve café 合成.txt",
        "Media/WhatsApp Video/VID-20990101-WA0003.mp4",
        "Databases/msgstore.db.crypt15",
        "Databases/msgstore-2099-01-01.1.db.crypt15",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ch76-pull-" + Guid.NewGuid().ToString("N"));
    private readonly string _phone;
    private readonly FakeAdb _adb;
    private long _clock = 4_000_000_000; // 2096: whole seconds, plus a fraction below, as a phone's mtime has.

    public PullTests()
    {
        _phone = Path.Combine(_root, "phone");
        Directory.CreateDirectory(Path.Combine(_phone, "Media", "WhatsApp Audio")); // an empty folder
        Directory.CreateDirectory(Path.Combine(_phone, "Media", ".Statuses"));

        foreach (var name in Names)
        {
            WritePhone(name, "SYNTH content of " + name);
        }

        _adb = new FakeAdb(_phone);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WritePhone(string relative, string content)
    {
        var path = Path.Combine(_phone, relative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _clock += 100;
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(_clock).UtcDateTime.AddMilliseconds(437));
    }

    private string Dest(string name) => Path.Combine(_root, name);

    private (int Exit, string Out, string Err) Pull(string destination, string? previous = null, string? serial = null,
        IFileLinker? linker = null, long free = long.MaxValue / 4, string[]? extra = null)
    {
        var args = new List<string> { "pull", "--adb", "adb.exe", "--destination", destination };

        if (previous is not null)
        {
            args.AddRange(["--previous", previous]);
        }

        if (serial is not null)
        {
            args.AddRange(["--serial", serial]);
        }

        args.AddRange(extra ?? []);
        var env = new PullEnvironment { Adb = _adb, Linker = linker ?? new NtfsLinker(), FreeBytes = _ => free, MaxPullArgumentChars = 200 };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = PullTool.Run([.. args], stdout, stderr, env);

        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string Line(string report, string label) =>
        report.Split('\n').First(l => l.TrimStart().StartsWith(label, StringComparison.Ordinal)).Trim();

    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(folder, p),
            p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) + "|" + File.GetLastWriteTimeUtc(p).Ticks + "|" + new FileInfo(p).Length);

    /// <summary>The media root holds exactly the phone's files, same bytes, mtime = the phone's whole seconds.</summary>
    private void AssertMirror(string destination)
    {
        var media = Path.Combine(destination, "WhatsApp");
        var phoneFiles = Directory.EnumerateFiles(_phone, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(_phone, p)).Order().ToList();
        var copyFiles = Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(media, p)).Order().ToList();
        Assert.Equal(phoneFiles, copyFiles);

        foreach (var relative in phoneFiles)
        {
            var source = Path.Combine(_phone, relative);
            var copy = Path.Combine(media, relative);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copy));
            var phoneSeconds = new DateTimeOffset(File.GetLastWriteTimeUtc(source)).ToUnixTimeSeconds();
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(phoneSeconds).UtcDateTime, File.GetLastWriteTimeUtc(copy));
        }

        var phoneFolders = Directory.EnumerateDirectories(_phone, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(_phone, p)).Order();
        var copyFolders = Directory.EnumerateDirectories(media, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(media, p)).Order();
        Assert.Equal(phoneFolders, copyFolders);

        var msgstore = Path.Combine(destination, "Databases", "msgstore.db.crypt15");
        Assert.Equal(File.ReadAllBytes(Path.Combine(_phone, "Databases", "msgstore.db.crypt15")), File.ReadAllBytes(msgstore));
        Assert.False(File.Exists(Path.Combine(destination, PullFiles.StateFileName)));
        Assert.Equal(phoneFiles.Count, PullFiles.ReadManifest(destination).Count);
    }

    // -------------------------------------------------------------------------------------------
    // The listing.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_listing_parses_names_with_spaces_quotes_slashes_digits_and_unicode()
    {
        const string root = AdbCommands.PhoneRoot;
        var output =
            $"41f9/3452/1700000000/{root}\n" +
            $"41f9/3452/1700000001/{root}/Media\n" +
            $"81b0/12/1700000002/{root}/Media/a b  c.jpg\n" +
            $"81b0/0/1700000003/{root}/Media/it's \"quoted\" & $(odd) `x`.pdf\n" +
            $"81b0/7/1700000004.123456789/{root}/Media/Ünïcödé 合成 🎉.txt\n" +
            $"81b0/9/1700000005/{root}/Media/12/34/5.txt\r\n" +
            $"81b0/3/1700000006/{root}/Media/line\nbreak.txt\n";

        var listing = PhoneListingParser.Parse(output, root);

        Assert.Equal(["Media"], listing.Folders);
        Assert.Equal(new PhoneFile("Media/a b  c.jpg", 12, 1700000002), listing.Files[0]);
        Assert.Equal(new PhoneFile("Media/it's \"quoted\" & $(odd) `x`.pdf", 0, 1700000003), listing.Files[1]);
        Assert.Equal(new PhoneFile("Media/Ünïcödé 合成 🎉.txt", 7, 1700000004), listing.Files[2]); // fraction cut, not rounded
        Assert.Equal(new PhoneFile("Media/12/34/5.txt", 9, 1700000005), listing.Files[3]);
        Assert.Equal(new PhoneFile("Media/line\nbreak.txt", 3, 1700000006), listing.Files[4]);

        Assert.True(PhoneListingParser.WindowsCanHold(listing.Files[0].RelativePath));
        Assert.True(PhoneListingParser.WindowsCanHold(listing.Files[2].RelativePath));
        Assert.False(PhoneListingParser.WindowsCanHold(listing.Files[1].RelativePath)); // the quote
        Assert.False(PhoneListingParser.WindowsCanHold(listing.Files[4].RelativePath)); // the line break
        Assert.False(PhoneListingParser.WindowsCanHold("Media/CON.txt"));
        Assert.False(PhoneListingParser.WindowsCanHold("Media/trailing."));
    }

    [Theory]
    [InlineData("%f/%s/%Y/%n\n")] // a stat that does not know -c prints the format or an error
    [InlineData("stat: Unknown option 'c'\n")]
    [InlineData("")]
    [InlineData("81b0/12/1700000002//elsewhere/file.jpg\n41f9/1/1/" + AdbCommands.PhoneRoot + "\n")]
    [InlineData("81b0/12/1700000002/" + AdbCommands.PhoneRoot + "/Media/x.jpg\n")] // no root line: format not proven
    public void A_listing_not_in_the_expected_format_is_refused(string output) =>
        Assert.Throws<PullRefusedException>(() => PhoneListingParser.Parse(output, AdbCommands.PhoneRoot));

    [Fact]
    public void The_list_command_is_one_find_with_stat_c_over_the_WhatsApp_folder() =>
        Assert.Equal("find '/sdcard/Android/media/com.whatsapp/WhatsApp' '(' -type f -o -type d ')' -exec stat -c '%f/%s/%Y/%n' {} +",
            AdbCommands.ListCommand);

    // -------------------------------------------------------------------------------------------
    // Full pull.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Without_previous_every_file_is_pulled_into_a_complete_media_root()
    {
        var destination = Dest("full");

        var run = Pull(destination);

        Assert.True(run.Exit == 0, run.Err);
        AssertMirror(destination);
        Assert.EndsWith("none (full pull)", Line(run.Out, "previous copy"));
        Assert.EndsWith("5", Line(run.Out, "new (pulled)"));
        Assert.EndsWith("2", Line(run.Out, "Databases folder files"));
        Assert.EndsWith("0", Line(run.Out, "same as the previous copy, hard-linked"));
        Assert.All(PullFiles.ReadManifest(destination), e => Assert.Equal(Obtained.Pulled, e.How));

        // Counts only: no names, folders or the serial on screen.
        Assert.DoesNotContain("IMG-", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHSERIAL", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, run.Out, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_existing_destination_is_refused_and_left_alone()
    {
        var destination = Dest("exists");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "SYNTH");

        var run = Pull(destination);

        Assert.Equal(2, run.Exit);
        Assert.Contains("already exists", run.Err, StringComparison.Ordinal);
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(destination).Select(Path.GetFileName));
        Assert.Equal(0, _adb.PullCalls);

        var finished = Dest("finished");
        Assert.Equal(0, Pull(finished).Exit);
        var again = Pull(finished);
        Assert.Equal(2, again.Exit);
        Assert.Contains("finished pull", again.Err, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // Incremental pull.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void With_previous_same_files_are_hard_linked_new_and_changed_pulled_gone_counted_and_the_previous_untouched()
    {
        var previous = Dest("october");
        Assert.Equal(0, Pull(previous).Exit);
        File.WriteAllText(Path.Combine(previous, "WhatsApp", "Media", "WhatsApp Images", "only-on-old-copy.jpg"), "SYNTH gone"); // gone from phone
        var before = Snapshot(previous);
        var mtimes = Directory.EnumerateFiles(previous, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.GetLastWriteTimeUtc);

        // The month: one new picture, one changed document (same size, later mtime), one changed video (other size).
        WritePhone("Media/WhatsApp Images/IMG-20990201-WA0009.jpg", "SYNTH new picture");
        var doc = Path.Combine(_phone, "Media", "WhatsApp Documents", "Synthetic plan 'v2' (final).pdf");
        var docText = File.ReadAllText(doc);
        WritePhone("Media/WhatsApp Documents/Synthetic plan 'v2' (final).pdf", docText.ToUpperInvariant());
        WritePhone("Media/WhatsApp Video/VID-20990101-WA0003.mp4", "SYNTH longer video content");
        WritePhone("Databases/msgstore.db.crypt15", "SYNTH content of Databases/msgstore.db.crypt15"); // same size: still pulled
        _adb.PulledRemotes.Clear();

        var destination = Dest("november");
        var run = Pull(destination, previous);

        Assert.True(run.Exit == 0, run.Err);
        AssertMirror(destination);
        Assert.EndsWith("3", Line(run.Out, "same as the previous copy, hard-linked"));
        Assert.EndsWith("0", Line(run.Out, "same as the previous copy, copied locally"));
        Assert.EndsWith("1", Line(run.Out, "new (pulled)"));
        Assert.EndsWith("2", Line(run.Out, "changed since the previous copy"));
        Assert.EndsWith("2", Line(run.Out, "Databases folder files"));
        Assert.EndsWith("1", Line(run.Out, "on the previous copy, gone from the phone"));

        // Pulled: the new and changed files, both Databases files and the separate msgstore copy; nothing else.
        Assert.Equal(6, _adb.PulledRemotes.Count);
        Assert.Equal(2, _adb.PulledRemotes.Count(r => r.EndsWith("/Databases/msgstore.db.crypt15", StringComparison.Ordinal)));
        Assert.DoesNotContain(_adb.PulledRemotes, r => r.Contains("WA0001", StringComparison.Ordinal));

        var linked = Path.Combine(destination, "WhatsApp", "Media", "WhatsApp Images", "IMG-20990101-WA0001.jpg");
        Assert.Equal(2, NtfsLinker.LinkCount(linked));
        Assert.False(File.Exists(Path.Combine(destination, "WhatsApp", "Media", "WhatsApp Images", "only-on-old-copy.jpg")));

        var manifest = PullFiles.ReadManifest(destination).ToDictionary(e => e.RelativePath);
        Assert.Equal(Obtained.Linked, manifest["Media/WhatsApp Images/IMG-20990101-WA0001.jpg"].How);
        Assert.Equal(Obtained.Pulled, manifest["Media/WhatsApp Documents/Synthetic plan 'v2' (final).pdf"].How);
        Assert.Equal(Obtained.Pulled, manifest["Databases/msgstore.db.crypt15"].How);

        // The previous copy: every byte, size and mtime as before.
        Assert.Equal(before, Snapshot(previous));
        Assert.All(mtimes, kv => Assert.Equal(kv.Value, File.GetLastWriteTimeUtc(kv.Key)));
    }

    [Fact]
    public void Across_volumes_same_files_are_copied_locally_with_their_mtime()
    {
        var previous = Dest("october");
        Assert.Equal(0, Pull(previous).Exit);
        var before = Snapshot(previous);
        _adb.PulledRemotes.Clear();

        var destination = Dest("november");
        var run = Pull(destination, previous, linker: new NoLinks());

        Assert.True(run.Exit == 0, run.Err);
        AssertMirror(destination);
        Assert.EndsWith("0", Line(run.Out, "same as the previous copy, hard-linked"));
        Assert.EndsWith("5", Line(run.Out, "same as the previous copy, copied locally"));
        Assert.Equal(3, _adb.PulledRemotes.Count); // two Databases files and the separate msgstore copy
        var copy = Path.Combine(destination, "WhatsApp", "Media", "WhatsApp Images", "IMG-20990101-WA0001.jpg");
        Assert.Equal(1, NtfsLinker.LinkCount(copy));
        Assert.All(PullFiles.ReadManifest(destination).Where(e => !e.RelativePath.StartsWith("Databases/", StringComparison.Ordinal)),
            e => Assert.Equal(Obtained.Copied, e.How));
        Assert.Equal(before, Snapshot(previous));
        Assert.Empty(Directory.EnumerateFiles(destination, "*" + PullFiles.PartialSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public void A_file_whose_mtime_differs_only_below_a_second_is_the_same_and_one_a_second_later_is_changed()
    {
        var previous = Dest("october");
        Assert.Equal(0, Pull(previous).Exit);

        var picture = Path.Combine(_phone, "Media", "WhatsApp Images", "IMG-20990101-WA0001.jpg");
        var video = Path.Combine(_phone, "Media", "WhatsApp Video", "VID-20990101-WA0003.mp4");
        File.SetLastWriteTimeUtc(picture, File.GetLastWriteTimeUtc(picture).AddMilliseconds(500)); // same second
        File.SetLastWriteTimeUtc(video, File.GetLastWriteTimeUtc(video).AddSeconds(1));

        var run = Pull(Dest("november"), previous);

        Assert.True(run.Exit == 0, run.Err);
        Assert.EndsWith("4", Line(run.Out, "same as the previous copy, hard-linked"));
        Assert.EndsWith("1", Line(run.Out, "changed since the previous copy"));
    }

    [Fact]
    public void A_stopped_pull_continues_when_run_again_and_ends_complete()
    {
        var previous = Dest("october");
        Assert.Equal(0, Pull(previous).Exit);
        var before = Snapshot(previous);
        WritePhone("Media/WhatsApp Images/IMG-20990201-WA0010.jpg", "SYNTH new one");
        WritePhone("Media/WhatsApp Images/IMG-20990201-WA0011.jpg", "SYNTH new two");
        WritePhone("Media/WhatsApp Images/IMG-20990201-WA0012.jpg", "SYNTH new three");

        var destination = Dest("november");
        _adb.FailOnPull = _adb.PullCalls + 2; // the msgstore pull succeeds, the next batch stops part-way
        var stopped = Pull(destination, previous);

        Assert.Equal(2, stopped.Exit);
        Assert.Contains("Run the same command again", stopped.Err, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(destination, PullFiles.StateFileName)));
        Assert.False(File.Exists(Path.Combine(destination, PullFiles.ManifestFileName)));

        // A rerun with another previous copy is refused; the same command continues.
        var other = Dest("other");
        Directory.CreateDirectory(Path.Combine(other, "WhatsApp"));
        Assert.Contains("different previous copy", Pull(destination, other).Err, StringComparison.Ordinal);
        Assert.Contains("different previous copy", Pull(destination).Err, StringComparison.Ordinal);

        // Meanwhile a picture the stopped run had linked left the phone: the rerun takes that name out of the unfinished
        // copy (only the name: the previous copy keeps its file).
        Assert.True(File.Exists(Path.Combine(destination, "WhatsApp", "Media", "WhatsApp Images", "Sent", "IMG-20990101-WA0002.jpg")));
        File.Delete(Path.Combine(_phone, "Media", "WhatsApp Images", "Sent", "IMG-20990101-WA0002.jpg"));
        _adb.FailOnPull = null;
        _adb.PulledRemotes.Clear();
        var rerun = Pull(destination, previous);

        Assert.True(rerun.Exit == 0, rerun.Err);
        AssertMirror(destination);
        Assert.DoesNotContain(_adb.PulledRemotes, r => r.Contains("WA0001", StringComparison.Ordinal));
        Assert.True(int.Parse(Line(rerun.Out, "already in place from the stopped run").Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1],
            System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.EndsWith("1", Line(rerun.Out, "removed from the stopped run"));
        Assert.Equal(before, Snapshot(previous));

        // A file the stopped run linked is reported as linked in the manifest.
        Assert.Equal(Obtained.Linked, PullFiles.ReadManifest(destination).Single(e => e.RelativePath.EndsWith("WA0001.jpg", StringComparison.Ordinal)).How);
    }

    [Fact]
    public void A_changed_file_is_never_pulled_through_a_hard_link_into_the_previous_copy()
    {
        var previous = Dest("october");
        Assert.Equal(0, Pull(previous).Exit);
        var before = Snapshot(previous);

        // A stopped run linked the picture; before the rerun the picture changes on the phone.
        var destination = Dest("november");
        _adb.FailOnPull = _adb.PullCalls + 2;
        Assert.Equal(2, Pull(destination, previous).Exit);
        WritePhone("Media/WhatsApp Images/IMG-20990101-WA0001.jpg", "SYNTH edited picture with other bytes");
        _adb.FailOnPull = null;

        var rerun = Pull(destination, previous);

        Assert.True(rerun.Exit == 0, rerun.Err);
        AssertMirror(destination);
        Assert.Equal(before, Snapshot(previous));
    }

    [Fact]
    public void The_previous_copy_must_be_a_finished_copy_holding_WhatsApp_and_separate_from_the_destination()
    {
        Assert.Contains("previous copy was not found", Pull(Dest("n1"), Dest("missing")).Err, StringComparison.Ordinal);

        var previous = Dest("october");
        Assert.Equal(0, Pull(previous).Exit);
        Assert.Contains("separate folders", Pull(Path.Combine(previous, "inside"), previous).Err, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(previous, PullFiles.StateFileName), "SYNTH");
        Assert.Contains("unfinished pull", Pull(Dest("n2"), previous).Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dest("n1")) || Directory.Exists(Dest("n2")) || Directory.Exists(Path.Combine(previous, "inside")));
    }

    // -------------------------------------------------------------------------------------------
    // Phone and space checks.
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("List of devices attached\n\n", null)]
    [InlineData("List of devices attached\nSYNTHSERIAL01\tunauthorized\n\n", null)]
    [InlineData("List of devices attached\nSYNTHSERIAL01\tdevice\nSYNTHSERIAL02\tdevice\n\n", null)]
    [InlineData("List of devices attached\nSYNTHSERIAL01\tdevice\n\n", "SYNTHSERIAL09")]
    [InlineData("List of devices attached\nSYNTHSERIAL01\toffline\nSYNTHSERIAL02\tdevice\n\n", "SYNTHSERIAL01")]
    public void Without_exactly_one_authorised_phone_or_the_named_one_nothing_is_copied(string devices, string? serial)
    {
        _adb.DevicesOutput = devices;
        var destination = Dest("refused");

        var run = Pull(destination, serial: serial);

        Assert.Equal(2, run.Exit);
        Assert.Contains("authorised", run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHSERIAL", run.Err + run.Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
        Assert.DoesNotContain(_adb.Calls, c => c.Contains("shell") || c.Contains("pull"));
    }

    [Fact]
    public void With_serial_every_adb_call_names_the_phone_and_emulators_are_ignored()
    {
        _adb.DevicesOutput = "List of devices attached\nemulator-5554\tdevice\nSYNTHSERIAL01\tdevice\nSYNTHSERIAL02\tdevice\n\n";

        var run = Pull(Dest("serial"), serial: "SYNTHSERIAL02");

        Assert.True(run.Exit == 0, run.Err);
        Assert.All(_adb.Calls.Where(c => c[0] != "start-server" && c[0] != "devices"), c => Assert.Equal(["-s", "SYNTHSERIAL02"], c.Take(2)));

        _adb.DevicesOutput = "List of devices attached\nemulator-5554\tdevice\nSYNTHSERIAL01\tdevice\n\n";
        Assert.Equal(0, Pull(Dest("one")).Exit);
    }

    [Fact]
    public void Free_space_needs_only_what_is_written_plus_the_margin()
    {
        var phoneBytes = Directory.EnumerateFiles(_phone, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        var msgstoreBytes = new FileInfo(Path.Combine(_phone, "Databases", "msgstore.db.crypt15")).Length;
        var full = phoneBytes + msgstoreBytes + PhonePuller.MarginBytes;

        var tooSmall = Pull(Dest("small"), free: full - 1);
        Assert.Equal(2, tooSmall.Exit);
        Assert.Contains("Not enough free space", tooSmall.Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dest("small")));
        Assert.Equal(0, Pull(Dest("october"), free: full).Exit);

        // With a previous copy on the same volume, only the Databases files (always pulled) and the msgstore count.
        var databases = Directory.EnumerateFiles(Path.Combine(_phone, "Databases")).Sum(p => new FileInfo(p).Length);
        var incremental = databases + msgstoreBytes + PhonePuller.MarginBytes;
        Assert.Equal(2, Pull(Dest("n1"), Dest("october"), free: incremental - 1).Exit);
        Assert.Equal(0, Pull(Dest("n2"), Dest("october"), free: incremental).Exit);

        // Across volumes the local copies count too.
        Assert.Equal(2, Pull(Dest("n3"), Dest("october"), linker: new NoLinks(), free: incremental).Exit);
        Assert.Equal(0, Pull(Dest("n4"), Dest("october"), linker: new NoLinks(), free: full).Exit);
    }

    [Fact]
    public void Without_a_msgstore_on_the_phone_nothing_is_copied()
    {
        File.Delete(Path.Combine(_phone, "Databases", "msgstore.db.crypt15"));

        var run = Pull(Dest("none"));

        Assert.Equal(2, run.Exit);
        Assert.Contains("msgstore.db.crypt15", run.Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dest("none")));
    }

    [Fact]
    public void Options_are_checked_and_values_never_echoed()
    {
        var run = Pull(Dest("x"), extra: ["--bogus", "C:\\SYNTH\\secret"]);

        Assert.Equal(2, run.Exit);
        Assert.DoesNotContain("SYNTH", run.Err, StringComparison.Ordinal);
        Assert.Equal(2, PullTool.Run(["pull", "--destination", Dest("y")], TextWriter.Null, TextWriter.Null, null));
    }
}
