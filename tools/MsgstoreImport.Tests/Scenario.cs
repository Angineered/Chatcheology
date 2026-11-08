using System.Globalization;

namespace MsgstoreImport.Tests;

/// <summary>
/// One synthetic conversation exercising every scope rule, and the tool driven over it.
/// </summary>
/// <remarks>
/// Expected manifest (7 rows): Both, HashOnly, the three busy-minute attachments, and the two
/// direction-split attachments. Expected exclusions (9): name+size only, conflict, not in the
/// candidate set, paired with no asset, two in a key with fewer records, one in a key with more
/// records, no key_id, and one resolved by hand before the plan.
/// </remarks>
internal sealed class Scenario : IDisposable
{
    internal SyntheticData Data { get; } = new();

    internal long Both, HashOnly, NameSizeOnly, Conflict, NotInSet, NoAsset, NoKey, Resolved;
    internal long[] Fewer = [], More = [], Busy = [], Direction = [];

    internal SyntheticData.Asset BothAsset = null!, HashAsset = null!, NameSizeAsset = null!,
        ConflictHashAsset = null!, ConflictNameAsset = null!, ResolvedAsset = null!;

    internal SyntheticData.Asset[] BusyAssets = [], DirectionAssets = [];

    /// <summary>Every key_id used in the msgstore, to prove none reaches stdout.</summary>
    internal List<string> KeyIDs { get; } = [];

    internal Scenario()
    {
        var d = Data;

        string Key(string name)
        {
            var key = "SYNTHKEY" + name.ToUpperInvariant();
            KeyIDs.Add(key);
            return key;
        }

        // Both hash and name+size, same asset.
        Both = d.AddAttachment("09:00:00", outgoing: true);
        BothAsset = d.AddAsset();
        d.AddRecordFor(BothAsset, "09:00:20", fromMe: true, Key("both"));

        // Hash only (incoming).
        HashOnly = d.AddAttachment("09:01:00", outgoing: false);
        HashAsset = d.AddAsset();
        d.AddRecordFor(HashAsset, "09:01:40", fromMe: false, Key("hash"), nameAndSize: false);

        // Name+size only: no hash.
        NameSizeOnly = d.AddAttachment("09:02:00", outgoing: true);
        NameSizeAsset = d.AddAsset();
        d.AddRecord("09:02:10", true, NameSizeAsset.FileName, NameSizeAsset.Size, null, Key("namesize"));

        // Conflict: hash names one asset, name+size another; both in the candidate set.
        Conflict = d.AddAttachment("09:03:00", outgoing: true);
        ConflictHashAsset = d.AddAsset();
        ConflictNameAsset = d.AddAsset();
        d.AddRecord("09:03:05", true, ConflictNameAsset.FileName, ConflictNameAsset.Size, ConflictHashAsset.Base64Hash, Key("conflict"));

        // Hash-matched asset dated three days later: not an exact-date candidate.
        NotInSet = d.AddAttachment("09:04:00", outgoing: true);
        var later = d.AddAsset(SyntheticData.Day.AddDays(3));
        d.AddRecordFor(later, "09:04:00", fromMe: true, Key("notinset"));

        // Paired, but the record names nothing in the inventory.
        NoAsset = d.AddAttachment("09:05:00", outgoing: true);
        d.AddRecord("09:05:30", true, "never-recovered.jpg", 77, d.UnknownHash(), Key("noasset"));

        // Two attachments, one record: fewer records in the key.
        Fewer = [d.AddAttachment("09:06:00", true), d.AddAttachment("09:06:00", true)];
        d.AddRecordFor(d.AddAsset(), "09:06:10", fromMe: true, Key("fewer"));

        // One attachment, two records: more records in the key.
        More = [d.AddAttachment("09:07:00", false)];
        d.AddRecordFor(d.AddAsset(), "09:07:10", fromMe: false, Key("more1"));
        d.AddRecordFor(d.AddAsset(), "09:07:20", fromMe: false, Key("more2"));

        // Hash matched and in the set, but no key_id.
        NoKey = d.AddAttachment("09:08:00", outgoing: true);
        var noKeyAsset = d.AddAsset();
        d.AddRecord("09:08:00", true, noKeyAsset.FileName, noKeyAsset.Size, noKeyAsset.Base64Hash, keyID: null);

        // Would be importable, but is resolved by hand before the plan.
        Resolved = d.AddAttachment("09:09:00", outgoing: true);
        ResolvedAsset = d.AddAsset();
        d.AddRecordFor(ResolvedAsset, "09:09:00", fromMe: true, Key("resolved"));

        // A busy minute: three attachments, three records whose _id order is the reverse of sort_id.
        Busy = [d.AddAttachment("09:10:00", true), d.AddAttachment("09:10:00", true), d.AddAttachment("09:10:00", true)];
        BusyAssets = [d.AddAsset(), d.AddAsset(), d.AddAsset()];
        d.AddRecordFor(BusyAssets[2], "09:10:50", true, Key("busy3"), sortID: 3000);
        d.AddRecordFor(BusyAssets[1], "09:10:30", true, Key("busy2"), nameAndSize: false, sortID: 2000);
        d.AddRecordFor(BusyAssets[0], "09:10:10", true, Key("busy1"), sortID: 1000);

        // One minute, both directions: each pairs within its own direction.
        Direction = [d.AddAttachment("09:11:00", outgoing: true), d.AddAttachment("09:11:00", outgoing: false)];
        DirectionAssets = [d.AddAsset(), d.AddAsset()];
        d.AddRecordFor(DirectionAssets[1], "09:11:05", fromMe: false, Key("dirin"));
        d.AddRecordFor(DirectionAssets[0], "09:11:45", fromMe: true, Key("dirout"));

        // A record in another chat at a paired minute: must be ignored.
        d.AddRecord("09:00:30", true, BothAsset.FileName, BothAsset.Size, BothAsset.Base64Hash, Key("otherchat"), chat: 8);

        d.RunPhase7();
        d.ConfirmByHand(Resolved, ResolvedAsset.ID);
        d.CloseAll();
    }

    internal static readonly int ExpectedRows = 7;
    internal static readonly int ExpectedAttachments = 16;

    /// <summary>The manifest rows the scope rules must produce, as (attachment, asset, kind).</summary>
    internal List<(long, long, MatchKind)> ExpectedManifest() =>
    [
        (Both, BothAsset.ID, MatchKind.HashAndNameSize),
        (HashOnly, HashAsset.ID, MatchKind.HashOnly),
        (Busy[0], BusyAssets[0].ID, MatchKind.HashAndNameSize),
        (Busy[1], BusyAssets[1].ID, MatchKind.HashOnly),
        (Busy[2], BusyAssets[2].ID, MatchKind.HashAndNameSize),
        (Direction[0], DirectionAssets[0].ID, MatchKind.HashAndNameSize),
        (Direction[1], DirectionAssets[1].ID, MatchKind.HashAndNameSize),
    ];

    // ---------------------------------------------------------------------------------------
    // Driving the tool.
    // ---------------------------------------------------------------------------------------

    internal string ManifestPath => Path.Combine(Data.DirectoryPath, "manifest.tsv");

    internal string[] PlanArgs(string? manifestOut = null) =>
    [
        "plan", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
        "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
        "--utc-offset", SyntheticData.OffsetText, "--local-participant", "1",
        "--manifest-out", manifestOut ?? ManifestPath,
    ];

    internal string[] ApplyArgs(string sha, bool backup = true) =>
    [
        "apply", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
        "--manifest", ManifestPath, "--manifest-sha256", sha, .. backup ? new[] { "--i-have-a-backup" } : [],
    ];

    internal string[] UndoArgs(string sha) =>
    [
        "undo-batch", "--workspace", Data.WorkspacePath, "--manifest", ManifestPath,
        "--manifest-sha256", sha, "--i-have-a-backup",
    ];

    internal PlanResult Plan() => Planner.Plan(Options.Parse(PlanArgs()));

    internal WriteResult Apply(string sha) => Applier.Apply(Options.Parse(ApplyArgs(sha)));

    internal WriteResult Undo(string sha) => Undoer.Undo(Options.Parse(UndoArgs(sha)));

    internal static (int Exit, string Out, string Err) Cli(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = ImportTool.Run(args, stdout, stderr);

        return (exit, stdout.ToString(), stderr.ToString());
    }

    public void Dispose() => Data.Dispose();
}
