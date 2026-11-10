using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ArchiveRefresh.PhonePull;

/// <summary>The command line of <c>pull</c>.</summary>
internal sealed record PullOptions(string Adb, string Destination, string? Previous, string? Serial)
{
    internal const string Usage = """
        ArchiveRefresh pull (CH-76): copies the phone's WhatsApp folder to a new folder on the laptop. Read-only on the
        phone. Counts only on screen.

            ArchiveRefresh pull --adb <adb.exe> --destination <new folder> [--previous <last month's destination>] [--serial <id>]

        Without --previous every file is pulled. With --previous, a file whose relative path, size and mtime (whole
        seconds) match the previous copy is hard-linked from it (copied locally when the two are not on one NTFS volume);
        the rest, and always the Databases folder, are pulled. The previous copy is never changed. The destination gets
        WhatsApp\ (the media root), Databases\msgstore.db.crypt15 and pull-manifest.tsv. A stopped pull: run the same
        command again and it continues.
        """;

    /// <exception cref="UsageException">The arguments are not a valid pull command.</exception>
    internal static PullOptions Parse(string[] args)
    {
        string? adb = null, destination = null, previous = null, serial = null;

        string Next(ref int i, string name) =>
            i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1])
                ? args[++i]
                : throw new UsageException($"{name} needs a value.");

        string Once(string? current, ref int i, string name) =>
            current is null ? Next(ref i, name) : throw new UsageException($"{name} is given twice.");

        for (var index = args.Length > 0 && args[0] == "pull" ? 1 : 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--adb": adb = Once(adb, ref index, "--adb"); break;
                case "--destination": destination = Once(destination, ref index, "--destination"); break;
                case "--previous": previous = Once(previous, ref index, "--previous"); break;
                case "--serial": serial = Once(serial, ref index, "--serial"); break;
                default:
                    throw new UsageException(
                        $"Argument {(index + 1).ToString(CultureInfo.InvariantCulture)} is not a recognised option.");
            }
        }

        if (adb is null || destination is null)
        {
            throw new UsageException("pull needs --adb and --destination.");
        }

        return new PullOptions(adb, destination, previous, serial);
    }
}

/// <summary>What the pull runs against: adb, the hard-link maker and the free space. Tests give fakes.</summary>
internal sealed class PullEnvironment
{
    internal required IAdbRunner Adb { get; init; }
    internal IFileLinker Linker { get; init; } = new NtfsLinker();

    /// <summary>Free bytes on the volume holding the given existing folder.</summary>
    internal Func<string, long> FreeBytes { get; init; } = folder => new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;

    /// <summary>Most characters of remote paths in one <c>adb pull</c> (Windows allows 32,767 on a command line).</summary>
    internal int MaxPullArgumentChars { get; init; } = 24_000;
}

/// <summary>
/// The phone pull (CH-76). Lists the phone's WhatsApp folder in one adb call, then builds the destination as a complete
/// media root: files unchanged since the previous copy are hard-linked (or copied locally), the rest pulled with
/// <c>adb pull -a</c>. The previous copy is only read. See the README beside this tool.
/// </summary>
internal static class PhonePuller
{
    /// <summary>Space kept free beyond what the pull writes.</summary>
    internal const long MarginBytes = 5L * 1024 * 1024 * 1024;

    private enum Kind
    {
        Same,
        New,
        Changed,
        Database,
    }

    private sealed record Item(PhoneFile File, Kind Kind, string Local, string? PreviousPath)
    {
        internal bool Done { get; set; }
        internal Obtained? How { get; set; }
    }

    /// <returns>The counts-only report.</returns>
    /// <exception cref="PullRefusedException">Refused before writing, or stopped part-way (the message says what to do).</exception>
    internal static string Run(PullOptions options, PullEnvironment env, TextWriter progress)
    {
        var clock = Stopwatch.StartNew();
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Destination));
        var previous = options.Previous is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Previous));

        if (Path.GetPathRoot(destination) is { } destinationRoot
            && string.Equals(Path.TrimEndingDirectorySeparator(destinationRoot), destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new PullRefusedException("The destination cannot be a drive root; give a new folder.");
        }

        var previousMedia = previous is null ? null : Path.Combine(previous, PullFiles.MediaFolderName);

        if (previous is not null)
        {
            if (!Directory.Exists(previousMedia))
            {
                throw new PullRefusedException("The previous copy was not found, or holds no WhatsApp folder. Nothing was copied.");
            }

            if (File.Exists(Path.Combine(previous, PullFiles.StateFileName)))
            {
                throw new PullRefusedException(
                    "The previous copy is an unfinished pull. Finish it first (run its pull again), or give another previous copy.");
            }

            if (Inside(destination, previous) || Inside(previous, destination))
            {
                throw new PullRefusedException("The destination and the previous copy must be separate folders, neither inside the other.");
            }
        }

        var resume = false;

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            if (File.Exists(Path.Combine(destination, PullFiles.StateFileName)))
            {
                var recorded = PullFiles.ReadStatePrevious(destination);

                if (!string.Equals(recorded, previous ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PullRefusedException(
                        "This unfinished pull was started with a different previous copy (or none). Run it again with the same " +
                        "-Previous, or delete the destination folder and start again.");
                }

                resume = true;
            }
            else if (File.Exists(Path.Combine(destination, PullFiles.ManifestFileName)))
            {
                throw new PullRefusedException("The destination already holds a finished pull; give a new folder.");
            }
            else
            {
                throw new PullRefusedException("The destination already exists; give a new folder.");
            }
        }

        // 1. Connect and check: exactly one authorised phone, or the one named by --serial.
        var target = SelectPhone(env.Adb, options.Serial, progress);

        // 2. One listing of the phone's WhatsApp folder (files and folders, size and whole-second mtime).
        var listed = env.Adb.Run([.. target, "shell", AdbCommands.ListCommand]);

        if (listed.ExitCode != 0)
        {
            throw new PullRefusedException(
                "Listing the phone's WhatsApp folder failed (keep the phone unlocked and connected, then run again). Nothing was copied.");
        }

        var listing = PhoneListingParser.Parse(listed.Output, AdbCommands.PhoneRoot);
        CheckNames(listing);

        var msgstore = listing.Files.FirstOrDefault(f => f.RelativePath == PullFiles.DatabasesFolderName + "/" + PullFiles.MsgstoreName)
                       ?? throw new PullRefusedException(
                           "The phone has no Databases/msgstore.db.crypt15. Back up the chats in WhatsApp first. Nothing was copied.");

        // 3. Classify every file against the previous copy, and against what a stopped run already wrote.
        var media = Path.Combine(destination, PullFiles.MediaFolderName);
        var items = new List<Item>(listing.Files.Count);

        foreach (var file in listing.Files)
        {
            var local = Path.Combine(media, ToLocal(file.RelativePath));
            string? previousPath = null;
            Kind kind;

            if (file.RelativePath.StartsWith(PullFiles.DatabasesFolderName + "/", StringComparison.Ordinal))
            {
                kind = Kind.Database;
            }
            else if (previousMedia is not null && new FileInfo(Path.Combine(previousMedia, ToLocal(file.RelativePath))) is { Exists: true } old)
            {
                kind = Matches(old, file) ? Kind.Same : Kind.Changed;
                previousPath = old.FullName;
            }
            else
            {
                kind = Kind.New;
            }

            var item = new Item(file, kind, local, kind == Kind.Same ? previousPath : null);

            if (resume && new FileInfo(local) is { Exists: true } present && Matches(present, file))
            {
                item.Done = true;
            }

            items.Add(item);
        }

        var gone = 0;

        if (previousMedia is not null)
        {
            var onPhone = new HashSet<string>(listing.Files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
            gone = EnumerateFiles(previousMedia).Count(path => !onPhone.Contains(ToRelative(previousMedia, path)));
        }

        var msgstoreLocal = Path.Combine(destination, PullFiles.DatabasesFolderName, PullFiles.MsgstoreName);
        var msgstoreDone = resume && new FileInfo(msgstoreLocal) is { Exists: true } m && Matches(m, msgstore);

        // 4. Free space: only what must be written, plus a margin.
        var linkLikely = previous is not null && env.Linker.LikelySameVolume(previous, Existing(destination));
        var toPull = items.Where(i => !i.Done && i.Kind != Kind.Same).ToList();
        var toLink = items.Where(i => !i.Done && i.Kind == Kind.Same).ToList();
        var needed = toPull.Sum(i => i.File.Size) + (msgstoreDone ? 0 : msgstore.Size)
                     + (linkLikely ? 0 : toLink.Sum(i => i.File.Size)) + MarginBytes;
        var free = env.FreeBytes(Existing(destination));

        if (free < needed)
        {
            throw new PullRefusedException(
                $"Not enough free space: this pull needs {Gb(needed)} GB (with a {Gb(MarginBytes)} GB margin) and the drive has " +
                $"{Gb(free)} GB. Choose another destination or free some space. Nothing was copied.");
        }

        progress.WriteLine(
            $"To do: {Count(toLink.Count)} file(s) from the previous copy, {Count(toPull.Count)} file(s) ({Gb(toPull.Sum(i => i.File.Size))} GB) " +
            "to pull from the phone. Keep the phone unlocked and connected.");

        // 5. Write: the state file first, so a stop at any point can be continued.
        if (!resume)
        {
            Directory.CreateDirectory(destination);
            PullFiles.WriteState(destination, previous);
        }
        else
        {
            foreach (var partial in EnumerateFiles(destination).Where(p => p.EndsWith(PullFiles.PartialSuffix, StringComparison.Ordinal)))
            {
                File.Delete(partial);
            }
        }

        Directory.CreateDirectory(media);
        Directory.CreateDirectory(Path.Combine(destination, PullFiles.DatabasesFolderName));

        foreach (var folder in listing.Folders)
        {
            Directory.CreateDirectory(Path.Combine(media, ToLocal(folder)));
        }

        long bytesPulled = 0;

        if (!msgstoreDone)
        {
            RemoveOwn(msgstoreLocal);
            Pull(env.Adb, target, [AdbCommands.PhoneRoot + "/" + msgstore.RelativePath], Path.Combine(destination, PullFiles.DatabasesFolderName));

            if (!File.Exists(msgstoreLocal))
            {
                throw Stopped();
            }

            bytesPulled += new FileInfo(msgstoreLocal).Length;
        }

        foreach (var item in toLink)
        {
            RemoveOwn(item.Local);
            Directory.CreateDirectory(Path.GetDirectoryName(item.Local)!);

            if (env.Linker.TryLink(item.PreviousPath!, item.Local))
            {
                item.How = Obtained.Linked;
            }
            else
            {
                var partial = item.Local + PullFiles.PartialSuffix;
                File.Copy(item.PreviousPath!, partial, overwrite: true);
                File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(item.PreviousPath!));
                File.Move(partial, item.Local);
                item.How = Obtained.Copied;
            }

            item.Done = true;
        }

        var pulledFiles = 0;
        var nextReport = 10;

        foreach (var batch in Batches(toPull, env.MaxPullArgumentChars))
        {
            foreach (var item in batch)
            {
                // Never pull over an existing name: it may be a hard link into the previous copy.
                RemoveOwn(item.Local);
            }

            var folder = Path.GetDirectoryName(batch[0].Local)!;
            Directory.CreateDirectory(folder);
            Pull(env.Adb, target, batch.Select(i => AdbCommands.PhoneRoot + "/" + i.File.RelativePath).ToList(), folder);

            foreach (var item in batch)
            {
                var got = new FileInfo(item.Local);

                if (!got.Exists)
                {
                    throw Stopped();
                }

                bytesPulled += got.Length;
                item.How = Obtained.Pulled;
                item.Done = true;
            }

            pulledFiles += batch.Count;

            if (toPull.Count > 0 && pulledFiles * 100 / toPull.Count >= nextReport)
            {
                progress.WriteLine($"  pulled {Count(pulledFiles)} of {Count(toPull.Count)} file(s)");
                nextReport = (pulledFiles * 100 / toPull.Count / 10 + 1) * 10;
            }
        }

        // 6. Check the result is the phone's folder exactly: every listed file there, nothing else.
        var byLocal = items.ToDictionary(i => i.Local, StringComparer.OrdinalIgnoreCase);
        var removed = 0;

        foreach (var path in EnumerateFiles(media))
        {
            if (!byLocal.ContainsKey(path))
            {
                // Only an unfinished run can leave a file the phone no longer has; it is this pull's own output.
                File.Delete(path);
                removed++;
            }
        }

        var entries = new List<ManifestEntry>(items.Count);
        var differ = 0;

        foreach (var item in items)
        {
            var got = new FileInfo(item.Local);

            if (!got.Exists)
            {
                throw Stopped();
            }

            if (!Matches(got, item.File))
            {
                differ++;
            }

            entries.Add(new ManifestEntry(item.File.RelativePath, got.Length, PullFiles.MtimeSeconds(got),
                item.How ?? (item.Kind == Kind.Same ? HowPlaced(item.Local) : Obtained.Pulled)));
        }

        PullFiles.WriteManifest(destination, entries);
        PullFiles.DeleteState(destination);

        var report = new StringBuilder();
        report.AppendLine("Phone pull (CH-76): counts only.");
        report.AppendLine(previous is null ? "  previous copy                                         none (full pull)" : "  previous copy                                         given");
        report.AppendLine($"  files on the phone                                    {Count(items.Count)}");
        report.AppendLine($"  folders on the phone                                  {Count(listing.Folders.Count)}");
        report.AppendLine($"  same as the previous copy, hard-linked                {Count(entries.Count(e => e.How == Obtained.Linked))}");
        report.AppendLine($"  same as the previous copy, copied locally             {Count(entries.Count(e => e.How == Obtained.Copied))}");
        report.AppendLine($"  new (pulled)                                          {Count(items.Count(i => i.Kind == Kind.New))}");
        report.AppendLine($"  changed since the previous copy (pulled)              {Count(items.Count(i => i.Kind == Kind.Changed))}");
        report.AppendLine($"  Databases folder files (always pulled)                {Count(items.Count(i => i.Kind == Kind.Database))}");
        report.AppendLine($"  on the previous copy, gone from the phone (not copied) {Count(gone)}");

        if (resume)
        {
            report.AppendLine($"  already in place from the stopped run                 {Count(items.Count - toLink.Count - toPull.Count)}");
            report.AppendLine($"  removed from the stopped run (no longer on the phone)  {Count(removed)}");
        }

        report.AppendLine($"  bytes pulled                                          {bytesPulled.ToString("N0", CultureInfo.InvariantCulture)} ({Gb(bytesPulled)} GB)");
        report.AppendLine($"  duration                                              {clock.Elapsed:hh\\:mm\\:ss}");
        report.AppendLine($"  msgstore.db.crypt15 on the phone dated                {DateTimeOffset.FromUnixTimeSeconds(msgstore.MtimeSeconds).ToLocalTime():yyyy-MM-dd HH:mm}");

        if (differ > 0)
        {
            report.AppendLine($"ATTENTION: {Count(differ)} file(s) changed on the phone while copying (kept as copied; WhatsApp was busy). Run a new pull if that matters.");
        }

        report.AppendLine("Done. The manifest pull-manifest.tsv is in the destination; give this destination as -Previous next month.");

        return report.ToString();
    }

    private static List<string> SelectPhone(IAdbRunner adb, string? serial, TextWriter progress)
    {
        if (adb.Run(["start-server"]).ExitCode != 0)
        {
            throw new PullRefusedException("adb could not start. Nothing was copied.");
        }

        var result = adb.Run(["devices"]);

        if (result.ExitCode != 0)
        {
            throw new PullRefusedException("adb devices failed. Nothing was copied.");
        }

        var devices = AdbDevices.Parse(result.Output);
        progress.WriteLine($"adb devices: {Count(devices.Count)} phone(s) found" +
                           (devices.Count == 0 ? "" : " (" + string.Join(", ", devices.GroupBy(d => d.State).Select(g => $"{g.Key} {Count(g.Count())}")) + ")"));

        if (serial is not null)
        {
            if (!devices.Any(d => d.Serial == serial && d.State == "device"))
            {
                throw new PullRefusedException(
                    "The phone named by -Serial is not connected and authorised (state 'device'). Unlock it and accept the USB debugging prompt.");
            }

            return AdbCommands.Target(serial);
        }

        if (devices.Count != 1 || devices[0].State != "device")
        {
            throw new PullRefusedException(
                "Need exactly one authorised phone (state 'device'), or pass -Serial. Unlock the phone and accept the USB debugging prompt.");
        }

        return AdbCommands.Target(null);
    }

    private static void CheckNames(PhoneListing listing)
    {
        var all = listing.Files.Select(f => f.RelativePath).Concat(listing.Folders).ToList();
        var cannot = all.Count(p => !PhoneListingParser.WindowsCanHold(p));

        if (cannot > 0)
        {
            throw new PullRefusedException(
                $"{Count(cannot)} name(s) on the phone cannot be stored on Windows (a character such as : ? * \" or a line break, a " +
                "trailing dot or space, or a reserved name); a full copy would fail on them too. Nothing was copied.");
        }

        var clashes = all.Count - all.Distinct(StringComparer.OrdinalIgnoreCase).Count();

        if (clashes > 0)
        {
            throw new PullRefusedException(
                $"{Count(clashes)} name(s) on the phone differ from another only in upper/lower case, which Windows keeps as one " +
                "file; a full copy would lose one of each. Nothing was copied.");
        }
    }

    /// <summary>Pulls the remote files into one existing local folder (adb pull -a: the phone's mtime kept).</summary>
    private static void Pull(IAdbRunner adb, List<string> target, IReadOnlyList<string> remotes, string localFolder)
    {
        var result = adb.Run([.. target, "pull", "-a", .. remotes, localFolder]);

        if (result.ExitCode != 0)
        {
            throw Stopped();
        }
    }

    private static PullRefusedException Stopped() => new(
        "The copy from the phone stopped (phone locked or disconnected?). Run the same command again: it continues where it " +
        "stopped. Or delete the destination folder and start again.");

    /// <summary>The files to pull in groups of one folder each, every group within the command-line budget.</summary>
    private static IEnumerable<List<Item>> Batches(IEnumerable<Item> items, int maxChars)
    {
        foreach (var folder in items.GroupBy(i => Path.GetDirectoryName(i.Local)!, StringComparer.OrdinalIgnoreCase))
        {
            var batch = new List<Item>();
            var chars = 0;

            foreach (var item in folder)
            {
                var length = AdbCommands.PhoneRoot.Length + item.File.RelativePath.Length + 4;

                if (batch.Count > 0 && chars + length > maxChars)
                {
                    yield return batch;
                    batch = [];
                    chars = 0;
                }

                batch.Add(item);
                chars += length;
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
        }
    }

    /// <summary>Removes a file of this pull's own destination (a link is only a name: its other copy stays as it is).</summary>
    private static void RemoveOwn(string path)
    {
        // Never change its attributes first: those belong to the file, so to the previous copy's name as well.
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static Obtained HowPlaced(string path)
    {
        try
        {
            return NtfsLinker.LinkCount(path) > 1 ? Obtained.Linked : Obtained.Copied;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Obtained.Copied;
        }
    }

    private static bool Matches(FileInfo local, PhoneFile phone) =>
        local.Length == phone.Size && PullFiles.MtimeSeconds(local) == phone.MtimeSeconds;

    private static IEnumerable<string> EnumerateFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            MatchType = MatchType.Win32,
        });

    private static string ToLocal(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private static string ToRelative(string folder, string path) =>
        Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool Inside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder itself or its nearest existing parent (for the volume's free space).</summary>
    private static string Existing(string folder)
    {
        var current = folder;

        while (!Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current) ?? throw new PullRefusedException("The destination's drive was not found.");
        }

        return current;
    }

    private static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
}
