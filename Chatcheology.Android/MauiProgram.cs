using Android.Runtime;
using Chatcheology.Android.Controls;

namespace Chatcheology.Android
{
    /// <summary>Builds the MAUI app.</summary>
    public static class MauiProgram
    {
        /// <summary>The app.</summary>
        public static MauiApp CreateMauiApp()
        {
            LogUnhandledExceptionTypes();

            var builder = MauiApp.CreateBuilder();

            builder.UseMauiApp<App>();
            builder.ConfigureMauiHandlers(handlers =>
            {
                handlers.AddHandler<DecodedImage, DecodedImageHandler>();
                handlers.AddHandler<ZoomImage, ZoomImageHandler>();
            });

            return builder.Build();
        }

        /// <summary>
        /// Logs the type of an exception that ends the app, and of each inner exception (CH-60): a crash
        /// reported as a TargetInvocationException otherwise hides its cause. Types only, as the Release log
        /// rule asks: no message, which may hold text, names or paths. It does not handle the exception.
        /// </summary>
        private static void LogUnhandledExceptionTypes()
        {
            AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => LogTypes("android", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogTypes("domain", e.ExceptionObject as Exception);
        }

        private static void LogTypes(string source, Exception? exception)
        {
            var types = new List<string>();

            for (var current = exception; current is not null && types.Count < 8; current = current.InnerException)
            {
                types.Add(current.GetType().FullName ?? current.GetType().Name);
            }

            global::Android.Util.Log.Error(AppState.LogTag, $"unhandled {source} types={string.Join(" > ", types)}");
        }
    }
}
