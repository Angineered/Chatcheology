using Chatcheology.Android.Pages;

namespace Chatcheology.Android
{
    /// <summary>The application: one window, a navigation stack, the start page.</summary>
    public partial class App : Application
    {
        private readonly AppState _state;

        /// <summary>Creates the app and removes whatever an interrupted import left in staging.</summary>
        public App()
        {
            InitializeComponent();

            // The app's private files folder (Context.FilesDir). Only this app can read it.
            _state = new AppState(FileSystem.AppDataDirectory);

            try
            {
                _state.Library.CleanStaging();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the next start; staging never counts as installed, so nothing is lost.
            }

            // Shared memory-photo copies (CH-59) from an earlier run. Never throws.
            PhotoShareCopies.Clean();
        }

        /// <inheritdoc />
        protected override Window CreateWindow(IActivationState? activationState) =>
            new(new NavigationPage(new StartPage(_state)) { BarTextColor = Colors.White }
                .Themed(NavigationPage.BarBackgroundColorProperty, "BarBackground"));
    }
}
