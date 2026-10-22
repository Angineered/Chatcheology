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

            return builder.Build();
        }
    }
}
