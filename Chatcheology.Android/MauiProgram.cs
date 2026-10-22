using Chatcheology.Android.Controls;

namespace Chatcheology.Android
{
    /// <summary>Builds the MAUI app.</summary>
    public static class MauiProgram
    {
        /// <summary>The app.</summary>
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            builder.UseMauiApp<App>();
            builder.ConfigureMauiHandlers(handlers =>
            {
                handlers.AddHandler<DecodedImage, DecodedImageHandler>();
                handlers.AddHandler<ZoomImage, ZoomImageHandler>();
            });

            return builder.Build();
        }
    }
}
