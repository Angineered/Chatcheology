using Chatcheology.Android.Pages;
using Chatcheology.Mobile.Library;

namespace Chatcheology.Android
{
    /// <summary>The application: one window, a navigation stack, the start page.</summary>
    public partial class App : Application
    {
        private readonly ArchiveLibrary _library;

        /// <summary>Creates the app and removes whatever an interrupted import left in staging.</summary>
        public App()
        {
            InitializeComponent();

            // The app's private files folder (Context.FilesDir). Only this app can read it.
            _library = new ArchiveLibrary(FileSystem.AppDataDirectory);
            _library.CleanStaging();
        }

        /// <inheritdoc />
        protected override Window CreateWindow(IActivationState? activationState) =>
            new(new NavigationPage(new StartPage(_library))
            {
                BarBackgroundColor = (Color)Resources["Primary"],
                BarTextColor = Colors.White,
            });
    }
}
