using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.Memories;
using Chatcheology.Data.NewerMessages;
using Chatcheology.Data.OlderExports;
using Microsoft.Data.Sqlite;

namespace ArchiveRefresh;

/// <summary>A refusal: the run stops with a counts-only reason. What was already applied is said by the caller.</summary>
internal sealed class RefreshRefusedException(string message) : Exception(message);

/// <summary>One file's size and SHA-256, or its absence. Never its path.</summary>
internal sealed record FileFingerprint(long? Size, string? Sha256)
{
    internal static readonly FileFingerprint Absent = new(null, null);

    internal bool Exists => Size is not null;

    internal string Text => Size is { } size ? $"{size.ToString(CultureInfo.InvariantCulture)}:{Sha256}" : "absent";

    internal static FileFingerprint Parse(string text)
    {
        if (text == "absent")
        {
            return Absent;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);

        if (colon <= 0 || !long.TryParse(text[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            || text[(colon + 1)..] is not { Length: 64 } sha)
        {
            throw new FormatException("A file fingerprint is malformed.");
        }

        return new FileFingerprint(size, sha);
    }

    /// <summary>The file's fingerprint, read with others allowed to read only (a program writing it makes this refuse).</summary>
    /// <exception cref="RefreshRefusedException">The file is there but cannot be read.</exception>
    internal static FileFingerprint Of(string path, string what)
    {
        if (!File.Exists(path))
        {
            return Absent;
        }

        SqliteConnection.ClearAllPools();

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

            return new FileFingerprint(stream.Length, sha);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RefreshRefusedException(
                $"The {what} could not be read ({exception.GetType().Name}): is another program (the viewer?) holding it? Nothing was written.");
        }
    }
}

/// <summary>The files beside the workspace a refresh may write, and the digests that tie a plan to its inputs.</summary>
internal static class RefreshFiles
{
    internal const string Workspace = "workspace";

    /// <summary>Every file a refresh may write, by its label, in backup order.</summary>
    internal static readonly string[] Writable =
    [
        Workspace, OlderExportsDatabase.FileName, NewerMessagesDatabase.FileName, MemoriesDatabase.FileName, ArchiveIdFile.FileName,
    ];

    /// <summary>The SQLite files among <see cref="Writable"/>: none may have a companion file when the refresh runs.</summary>
    internal static readonly string[] Databases =
        [Workspace, OlderExportsDatabase.FileName, NewerMessagesDatabase.FileName, MemoriesDatabase.FileName];

    private static readonly string[] CompanionSuffixes = ["-wal", "-shm", "-journal"];

    /// <summary>The path of a writable file, by its label.</summary>
    internal static string PathOf(string workspacePath, string label) =>
        label == Workspace
            ? Path.GetFullPath(workspacePath)
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(workspacePath))!, label);

    /// <summary>Every writable file's fingerprint, by label.</summary>
    internal static SortedDictionary<string, FileFingerprint> StateOf(string workspacePath)
    {
        var state = new SortedDictionary<string, FileFingerprint>(StringComparer.Ordinal);

        foreach (var label in Writable)
        {
            state[label] = FileFingerprint.Of(PathOf(workspacePath, label), label == Workspace ? "workspace" : label);
        }

        return state;
    }

    /// <summary>A state as one line of text: <c>label=size:sha</c> or <c>label=absent</c>, joined by <c>;</c>.</summary>
    internal static string StateText(IReadOnlyDictionary<string, FileFingerprint> state) =>
        string.Join(";", Writable.Select(label => label + "=" + state[label].Text));

    internal static SortedDictionary<string, FileFingerprint> ParseState(string text)
    {
        var state = new SortedDictionary<string, FileFingerprint>(StringComparer.Ordinal);

        foreach (var part in text.Split(';'))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                throw new FormatException("A recorded state is malformed.");
            }

            state[part[..equals]] = FileFingerprint.Parse(part[(equals + 1)..]);
        }

        if (!Writable.All(state.ContainsKey) || state.Count != Writable.Length)
        {
            throw new FormatException("A recorded state does not name exactly the refresh's files.");
        }

        return state;
    }

    /// <summary>Refuses when a SQLite companion file sits beside any of the refresh's databases.</summary>
    /// <exception cref="RefreshRefusedException">A companion file exists.</exception>
    internal static void RequireNoCompanions(string workspacePath)
    {
        var found = Databases.Count(label =>
            CompanionSuffixes.Any(suffix => File.Exists(PathOf(workspacePath, label) + suffix)));

        if (found > 0)
        {
            throw new RefreshRefusedException(
                $"{found.ToString(CultureInfo.InvariantCulture)} of the databases beside the workspace has a -wal, -shm or " +
                "-journal file beside it: a program has it open, or did not close it cleanly. Close the viewer (open and close " +
                "it once if it was not closed cleanly) and run this again. Nothing was written.");
        }
    }

    /// <summary>The SHA-256 of a path's full, upper-cased form: ties apply to the plan's paths without storing them.</summary>
    internal static string PathDigest(string path)
    {
        string full;

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new RefreshRefusedException("A path given is not a usable path. Nothing was written.");
        }

        return Sha256Hex(Encoding.UTF8.GetBytes(full.ToUpperInvariant()));
    }

    internal static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The SHA-256 of a short value (an account name, a collection name): never stored as itself.</summary>
    internal static string ValueDigest(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value));
}
