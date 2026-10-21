using Android.App;
using Android.Runtime;

namespace Chatcheology.Android
{
    /// <summary>The Android application, which hands over to MAUI.</summary>
    [Application]
    public class MainApplication : MauiApplication
    {
        /// <summary>Called by the Android runtime.</summary>
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        /// <inheritdoc />
        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
