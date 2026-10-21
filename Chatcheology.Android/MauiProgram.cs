using Chatcheology.Android.Controls;

namespace Chatcheology.Android
{
    /// <summary>Builds the MAUI app.</summary>
    public static class MauiProgram
    {
        /// <summary>The app, with the native conversation list's handler registered.</summary>
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            builder
                .UseMauiApp<App>()
                .ConfigureMauiHandlers(handlers =>
                    handlers.AddHandler<NativeConversationList, NativeConversationListHandler>());

            return builder.Build();
        }
    }
}
