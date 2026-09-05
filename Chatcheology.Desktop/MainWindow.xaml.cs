using System.Windows;
using Chatcheology.Desktop.Controls;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Win32;

namespace Chatcheology.Desktop
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// <remarks>
    /// The window owns two things a view-model should not: the file dialog, and the click that
    /// hands a media file to the operating system. Everything else it does is data binding.
    /// </remarks>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainViewModel(ChooseWorkspace);
            DataContext = _viewModel;

            // One handler for every message on the page: the event bubbles from whichever text
            // block held the link that would not open.
            AddHandler(LinkedText.LinkFailedEvent, new RoutedEventHandler(OnLinkFailed));
        }

        /// <remarks>
        /// No initial directory and no remembered location. The operator says where their workspace
        /// is; the application does not assume, and does not record where it was told.
        /// </remarks>
        private string? ChooseWorkspace()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open Chatcheology workspace",
                Filter = "Chatcheology workspace (*.db)|*.db|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        private void OnLinkFailed(object sender, RoutedEventArgs e)
        {
            if (e is LinkedText.LinkFailedEventArgs failure)
            {
                _viewModel.ReportStatus(failure.Message);
            }
        }

        private void OpenMediaClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: AttachmentViewModel attachment })
            {
                attachment.OpenExternally();
            }
        }

        /// <remarks>
        /// Closing the window closes the workspace, which disposes the connection and clears the
        /// SQLite pool, so the file is not left held open as the process winds down.
        /// </remarks>
        protected override void OnClosed(EventArgs e)
        {
            _viewModel.CloseSession();

            base.OnClosed(e);
        }
    }
}
