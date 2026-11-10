using ArchiveRefresh.PhonePull;
using Microsoft.Data.Sqlite;

namespace ArchiveRefresh;

/// <summary>The monthly archive refresh (CH-71 step 5, D9). See the README beside this file.</summary>
internal static class Program
{
    private static int Main(string[] args) => RefreshTool.Run(args, Console.Out, Console.Error, new RefreshHooks());
}

internal static class RefreshTool
{
    /// <returns>0 done (apply: the archive passed every check); 1 failed or the archive did not pass; 2 refused or stopped.</returns>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, RefreshHooks hooks)
    {
        if (args is ["pull", ..])
        {
            return PullTool.Run(args, stdout, stderr, null);
        }

        if (args is ["-h" or "--help" or "/?"] or ["plan" or "apply", "-h" or "--help" or "/?"])
        {
            stdout.WriteLine(RefreshOptions.Usage);

            return 0;
        }

        RefreshOptions options;

        try
        {
            options = RefreshOptions.Parse(args);
        }
        catch (UsageException exception)
        {
            stderr.WriteLine(exception.Message);
            stderr.WriteLine();
            stderr.WriteLine(RefreshOptions.Usage);

            return 2;
        }

        try
        {
            if (options.Apply)
            {
                var result = RefreshApplier.Apply(options, hooks);
                stdout.Write(result.Report);

                return result.Passed ? 0 : 1;
            }

            stdout.Write(RefreshPlanner.Plan(options).Report);

            return 0;
        }
        catch (RefreshRefusedException exception)
        {
            stderr.WriteLine("Refused: " + exception.Message);

            return 2;
        }
        catch (Exception exception)
        {
            // The exception's own message may carry a path, so only its type is printed.
            stderr.WriteLine($"The run could not complete ({exception.GetType().Name}). " +
                             (options.Apply
                                 ? "Each step is one transaction and the progress file records what finished: run the same apply again."
                                 : "Nothing was written to any input; plan again with a new plan folder."));

            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }
}
