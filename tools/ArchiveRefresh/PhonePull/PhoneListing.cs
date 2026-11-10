using System.Globalization;

namespace ArchiveRefresh.PhonePull;

/// <summary>A file on the phone: its path relative to the WhatsApp folder ('/'-separated), size and whole-second mtime.</summary>
internal sealed record PhoneFile(string RelativePath, long Size, long MtimeSeconds);

/// <summary>The phone's WhatsApp folder as one listing: its files and its folders (relative, '/'-separated).</summary>
internal sealed record PhoneListing(IReadOnlyList<PhoneFile> Files, IReadOnlyList<string> Folders);

/// <summary>Reads the output of <see cref="AdbCommands.ListCommand"/>.</summary>
internal static class PhoneListingParser
{
    private const int ModeTypeMask = 0xF000;
    private const int ModeDirectory = 0x4000;
    private const int ModeRegular = 0x8000;

    /// <summary>
    /// Parses the listing. Each record is <c>mode/size/mtime/path</c> with the path under <paramref name="root"/>. A line
    /// that is not a record continues the previous record's name (a name holding a newline); such a name is later refused
    /// as one Windows cannot hold. The mtime is whole seconds; a fractional mtime (a toybox that prints one) is cut to its
    /// whole seconds, which is what <c>adb pull -a</c> sets on the laptop.
    /// </summary>
    /// <exception cref="PullRefusedException">The output is not a listing in the expected format.</exception>
    internal static PhoneListing Parse(string output, string root)
    {
        var records = new List<(int Mode, long Size, long Mtime, string Path)>();

        var lines = output.Split('\n');
        var count = lines.Length;

        // The output ends with a newline, so the last piece is empty.
        if (count > 0 && lines[^1].Length == 0)
        {
            count--;
        }

        for (var i = 0; i < count; i++)
        {
            // A carriage return can only come from adb's terminal mode, never from a name Windows could hold.
            var line = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];

            if (TryRecord(line, out var record))
            {
                records.Add(record);
            }
            else if (records.Count > 0)
            {
                var last = records[^1];
                records[^1] = last with { Path = last.Path + "\n" + line };
            }
            else
            {
                throw new PullRefusedException(
                    "The phone's file listing is not in the expected format (its stat command may not support '-c %f/%s/%Y/%n'). Nothing was copied.");
            }
        }

        var files = new List<PhoneFile>();
        var folders = new List<string>();
        var sawRoot = false;
        var prefix = root + "/";

        foreach (var (mode, size, mtime, path) in records)
        {
            string relative;

            if (path == root)
            {
                if ((mode & ModeTypeMask) != ModeDirectory)
                {
                    throw new PullRefusedException("The phone's WhatsApp folder is not a folder. Nothing was copied.");
                }

                sawRoot = true;

                continue;
            }

            if (path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length)
            {
                relative = path[prefix.Length..];
            }
            else
            {
                throw new PullRefusedException("The phone's file listing holds a path outside the WhatsApp folder. Nothing was copied.");
            }

            switch (mode & ModeTypeMask)
            {
                case ModeRegular: files.Add(new PhoneFile(relative, size, mtime)); break;
                case ModeDirectory: folders.Add(relative); break;
                default: break; // find lists only files and folders.
            }
        }

        if (!sawRoot)
        {
            throw new PullRefusedException(
                "The phone's file listing does not include the WhatsApp folder itself (is WhatsApp's folder on the phone, and the phone unlocked?). Nothing was copied.");
        }

        return new PhoneListing(files, folders);
    }

    private static bool TryRecord(string line, out (int Mode, long Size, long Mtime, string Path) record)
    {
        record = default;
        var first = line.IndexOf('/', StringComparison.Ordinal);

        if (first <= 0)
        {
            return false;
        }

        var second = line.IndexOf('/', first + 1);

        if (second <= first + 1)
        {
            return false;
        }

        var third = line.IndexOf('/', second + 1);

        if (third <= second + 1 || third + 1 >= line.Length)
        {
            return false;
        }

        var mtimeText = line.AsSpan(second + 1, third - second - 1);
        var dot = mtimeText.IndexOf('.');

        if (dot >= 0)
        {
            if (dot == 0 || dot == mtimeText.Length - 1 || !IsDigits(mtimeText[(dot + 1)..]))
            {
                return false;
            }

            mtimeText = mtimeText[..dot];
        }

        if (!int.TryParse(line.AsSpan(0, first), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var mode)
            || !IsDigits(line.AsSpan(first + 1, second - first - 1))
            || !long.TryParse(line.AsSpan(first + 1, second - first - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            || !IsDigits(mtimeText)
            || !long.TryParse(mtimeText, NumberStyles.None, CultureInfo.InvariantCulture, out var mtime)
            || line[third + 1] != '/')
        {
            return false;
        }

        record = (mode, size, mtime, line[(third + 1)..]);

        return true;
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True when every part of the relative path is a name Windows can hold (a full copy would fail on the others).</summary>
    internal static bool WindowsCanHold(string relativePath)
    {
        foreach (var part in relativePath.Split('/'))
        {
            if (part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.'))
            {
                return false;
            }

            foreach (var c in part)
            {
                if (c < ' ' || c is '<' or '>' or ':' or '"' or '\\' or '|' or '?' or '*')
                {
                    return false;
                }
            }

            var dot = part.IndexOf('.', StringComparison.Ordinal);

            if (ReservedNames.Contains(dot < 0 ? part : part[..dot]))
            {
                return false;
            }
        }

        return true;
    }
}
