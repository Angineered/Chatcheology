namespace ArchiveRefresh.PhonePull;

/// <summary>The <c>pull</c> command (CH-76): copies the phone's WhatsApp folder, incrementally with --previous.</summary>
internal static class PullTool
{
    /// <param name="environment">The adb, linker and free space to use; null for the real adb named by --adb.</param>
    /// <returns>0 done; 1 failed; 2 refused or stopped (a stopped pull continues when run again).</returns>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, PullEnvironment? environment)
    {
        if (args is ["pull", "-h" or "--help" or "/?"])
        {
            stdout.WriteLine(PullOptions.Usage);

            return 0;
        }

        PullOptions options;

        try
        {
            options = PullOptions.Parse(args);
        }
        catch (UsageException exception)
        {
            stderr.WriteLine(exception.Message);
            stderr.WriteLine();
            stderr.WriteLine(PullOptions.Usage);

            return 2;
        }

        try
        {
            if (environment is null)
            {
                if (!File.Exists(options.Adb))
                {
                    stderr.WriteLine("Refused: adb was not found at the path given.");

                    return 2;
                }

                environment = new PullEnvironment { Adb = new ProcessAdbRunner(options.Adb) };
            }

            stdout.Write(PhonePuller.Run(options, environment, stdout));

            return 0;
        }
        catch (PullRefusedException exception)
        {
            stderr.WriteLine("Refused: " + exception.Message);

            return 2;
        }
        catch (Exception exception)
        {
            // The exception's own message may carry a path, so only its type is printed.
            stderr.WriteLine($"The pull could not complete ({exception.GetType().Name}). If the destination holds pull-state.tsv, " +
                             "run the same command again to continue; otherwise delete the destination folder and start again.");

            return 1;
        }
    }
}
