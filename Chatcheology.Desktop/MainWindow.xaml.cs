using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            : this(null)
        {
        }

        /// <param name="chooseWorkspace">
        /// Stands in for the file dialog. Only the tests supply one, so they can open a synthetic
        /// workspace in the real window; the application always asks the operator.
        /// </param>
        internal MainWindow(Func<string?>? chooseWorkspace)
        {
            InitializeComponent();

            _viewModel = new MainViewModel(chooseWorkspace ?? ChooseWorkspace);
            DataContext = _viewModel;

            _viewModel.MovedToPage += OnMovedToPage;

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

        /// <remarks>
        /// A page the operator has moved to starts at its top (CH-12). The list keeps one scroll
        /// viewer for every page it shows, so without this the offset reached on the old page would
        /// carry over and land somewhere in the middle of the new one.
        /// </remarks>
        private void OnMovedToPage(object? sender, EventArgs e)
        {
            if (MessageList.Template?.FindName("MessageScroller", MessageList) is ScrollViewer scroller)
            {
                scroller.ScrollToHome();
            }
        }

        private void OnLinkFailed(object sender, RoutedEventArgs e)
        {
            if (e is LinkedText.LinkFailedEventArgs failure)
            {
                _viewModel.ReportStatus(failure.Message);
            }
        }

        /// <remarks>
        /// Opening the review panel is a read. It records nothing, and clicking a card is the one
        /// gesture that starts it — no decision follows from a click anywhere in the conversation.
        /// </remarks>
        private async void AttachmentCardClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: AttachmentViewModel attachment })
            {
                e.Handled = true;

                await _viewModel.OpenReviewAsync(attachment.AttachmentID);
            }
        }

        private void CloseReviewClick(object sender, RoutedEventArgs e) =>
            _viewModel.CloseReview();

        /// <remarks>
        /// Candidate media goes through the same read-only path the conversation's own media uses.
        /// Opening a candidate's file is looking at it; nothing is recorded by having looked.
        /// </remarks>
        private void OpenCandidateMediaClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: CandidateViewModel candidate }
                && candidate.OpenExternally() is { } failure)
            {
                _viewModel.ReportStatus(failure);
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
