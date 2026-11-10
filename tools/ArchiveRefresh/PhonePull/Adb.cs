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
