using System.Diagnostics;
using System.Text;

namespace ArchiveRefresh.PhonePull;

/// <summary>What one adb run returned. The output is never printed: it may hold names, paths and the phone's serial.</summary>
internal sealed record AdbResult(int ExitCode, string Output, string Error);

/// <summary>Runs adb with an argument list. Tests give a fake adb; the tool gives <see cref="ProcessAdbRunner"/>.</summary>
internal interface IAdbRunner
{
    AdbResult Run(IReadOnlyList<string> arguments);
}

/// <summary>Runs the real adb.exe. Arguments go through the argument list (no shell); output is read as UTF-8.</summary>
internal sealed class ProcessAdbRunner(string adbPath) : IAdbRunner
{
    public AdbResult Run(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(adbPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("adb did not start.");
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return new AdbResult(process.ExitCode, output, error.GetAwaiter().GetResult());
    }
}

/// <summary>The adb commands the pull uses. Every one is read-only on the phone.</summary>
internal static class AdbCommands
{
    /// <summary>The phone's WhatsApp folder (the one holding <c>Media</c> and <c>Databases</c>).</summary>
    internal const string PhoneRoot = "/sdcard/Android/media/com.whatsapp/WhatsApp";

    /// <summary>
    /// Lists every regular file and folder under <see cref="PhoneRoot"/>, the root included, one line each:
    /// <c>&lt;mode hex&gt;/&lt;size&gt;/&lt;mtime seconds&gt;/&lt;full path&gt;</c> (toybox <c>stat -c '%f/%s/%Y/%n'</c>).
    /// The three leading fields are digits, and a name never holds '/', so the line splits unambiguously whatever the
    /// name holds (spaces, quotes, unicode). The command is one argument: adb sends it to the phone's shell as it is.
    /// </summary>
    internal static readonly string ListCommand =
        $"find '{PhoneRoot}' '(' -type f -o -type d ')' -exec stat -c '%f/%s/%Y/%n' {{}} +";

    internal static List<string> Target(string? serial) => serial is null ? [] : ["-s", serial];
}

/// <summary>What a listing that ended with an error said. Never quotes a line: they hold paths.</summary>
internal static class ListingErrors
{
    private const string NoSuchFile = "No such file or directory";

    private static List<string> Lines(string error) =>
        error.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();

    /// <summary>
    /// The number of items that vanished while find and stat ran (WhatsApp removing a file between the two), when every
    /// error line is such a "No such file or directory"; otherwise null.
    /// </summary>
    internal static int? VanishedOnly(string error)
    {
        var lines = Lines(error);

        return lines.Count > 0 && lines.All(l => l.Contains(NoSuchFile, StringComparison.Ordinal)) ? lines.Count : null;
    }

    /// <summary>A counts-only description of why the listing failed.</summary>
    internal static string Describe(AdbResult result)
    {
        var lines = Lines(result.Error);

        if (lines.Any(l => l.Contains("device", StringComparison.OrdinalIgnoreCase)
                           && (l.Contains("not found", StringComparison.OrdinalIgnoreCase) || l.Contains("offline", StringComparison.OrdinalIgnoreCase)
                               || l.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)))
            || lines.Any(l => l.Contains("no devices", StringComparison.OrdinalIgnoreCase)))
        {
            return "Listing the phone's WhatsApp folder failed: adb lost the phone (disconnected, locked or no longer authorised). " +
                   "Reconnect and unlock it, then run again.";
        }

        if (lines.Count == 0)
        {
            return $"Listing the phone's WhatsApp folder failed: adb ended with exit code {result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)} and no message.";
        }

        var denied = lines.Count(l => l.Contains("Permission denied", StringComparison.OrdinalIgnoreCase));
        var vanished = lines.Count(l => l.Contains(NoSuchFile, StringComparison.Ordinal));
        var unknown = lines.Count(l => l.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
                                       || l.Contains("invalid option", StringComparison.OrdinalIgnoreCase)
                                       || l.Contains("not found", StringComparison.OrdinalIgnoreCase));
        var other = lines.Count - denied - vanished - unknown;

        return "Listing the phone's WhatsApp folder failed: the phone reported " +
               $"{denied} 'permission denied', {unknown} 'unknown command or option' (its find or stat is not as expected), " +
               $"{vanished} 'no such file' and {other} other error line(s).";
    }
}

/// <summary>One line of <c>adb devices</c>.</summary>
internal sealed record AdbDevice(string Serial, string State);

internal static class AdbDevices
{
    /// <summary>The phones in <c>adb devices</c> output (emulators left out, as before).</summary>
    internal static List<AdbDevice> Parse(string output)
    {
        var devices = new List<AdbDevice>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
            {
                continue;
            }

            var tab = line.IndexOf('\t', StringComparison.Ordinal);

            if (tab <= 0)
            {
                continue;
            }

            var serial = line[..tab];

            if (serial.StartsWith("emulator-", StringComparison.Ordinal))
            {
                continue;
            }

            devices.Add(new AdbDevice(serial, line[(tab + 1)..].Trim()));
        }

        return devices;
    }
}
