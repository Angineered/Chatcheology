using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// Runs a test body on its own single-threaded apartment thread, which WPF requires.
    /// </summary>
    /// <remarks>
    /// xUnit runs tests on pool threads, which are multi-threaded apartments; WPF elements cannot
    /// be created there. Each call gets a fresh thread and dispatcher, which is shut down
    /// afterwards so no window or dispatcher outlives the test.
    /// </remarks>
    internal static class StaRunner
    {
        internal static void Run(Action body)
        {
            ExceptionDispatchInfo? failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(TimeSpan.FromMinutes(1)))
            {
                throw new TimeoutException("The UI test did not finish within a minute.");
            }

            failure?.Throw();
        }
    }
}
