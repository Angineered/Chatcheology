using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Chatcheology.Data.Workspace;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// CH-12: moving to another page or date starts the conversation at the top of the new page,
    /// and nothing else moves it.
    /// </summary>
    /// <remarks>
    /// <para>These run the real <see cref="MainWindow"/> end to end over a synthetic workspace:
    /// the real view-model, the real read-only session, the real message templates. The operator's
    /// gestures are the commands the buttons and the date control are bound to.</para>
    /// <para>The workspace holds four invented days of plain text messages, more than a page each,
    /// so every page is far taller than the window and every navigation lands on a different
    /// page.</para>
    /// </remarks>
    public sealed class ConversationNavigationScrollTests
    {
        private const int MessagesPerDay = 160;
        private const int Days = 4;
        private const double Tolerance = 0.5;

        private static readonly DateTime FirstDay = new(2026, 4, 6);

        [Fact]
        public void NextStartsTheNextPageAtItsTop() =>
            WithViewer((window, viewModel, scroller) =>
            {
                ScrollFarDown(window, scroller);

                Navigate(window, viewModel, () => viewModel.NextCommand.Execute(null));

                AssertAtTop(scroller, viewModel, "Next");
            });

        [Fact]
        public void PreviousStartsThePreviousPageAtItsTop() =>
            WithViewer((window, viewModel, scroller) =>
            {
                Navigate(window, viewModel, () => viewModel.NextCommand.Execute(null));

                ScrollFarDown(window, scroller);

                Navigate(window, viewModel, () => viewModel.PreviousCommand.Execute(null));

                AssertAtTop(scroller, viewModel, "Previous");
            });

        [Fact]
        public void GoingToADateStartsAtThatDatesFirstMessage() =>
            WithViewer((window, viewModel, scroller) =>
            {
                ScrollFarDown(window, scroller);

                viewModel.JumpDate = FirstDay.AddDays(2);

                Navigate(window, viewModel, () => viewModel.JumpToDateCommand.Execute(null));

                AssertAtTop(scroller, viewModel, "Go to date");

                // The chosen date's first message is the page's first, and it is on screen.
                Assert.Equal(2 * MessagesPerDay + 1, viewModel.Messages[0].SequenceNumber);

                var first = window.MessageList.ItemContainerGenerator.ContainerFromIndex(0)
                    as FrameworkElement;

                Assert.NotNull(first);
                Assert.True(first.IsVisible);
            });

        /// <summary>
        /// Phase 8.1: closing the review panel returns the operator to the same place. Opening and
        /// closing it is not a move, so the conversation must not jump to the top.
        /// </summary>
        [Fact]
        public void OpeningAndClosingTheReviewPanelKeepsThePlace() =>
            WithViewer((window, viewModel, scroller) =>
            {
                ScrollFarDown(window, scroller);

                var before = scroller.VerticalOffset;

                var opened = viewModel.OpenReviewAsync(SyntheticWorkspace.AttachmentID);
                PumpUntil(() => opened.IsCompleted);
                Settle(window);

                Assert.True(viewModel.Review.IsOpen, $"The review panel did not open: {viewModel.StatusText}");
                Assert.True(scroller.VerticalOffset > 0, "Opening the review panel moved the list to the top.");

                viewModel.CloseReview();
                Settle(window);

                Assert.False(viewModel.Review.IsOpen);
                Assert.True(
                    Math.Abs(scroller.VerticalOffset - before) <= Tolerance,
                    $"Offset was {before:0.#} before the panel opened and {scroller.VerticalOffset:0.#} " +
                    $"after it closed.");
            });

        // -------------------------------------------------------------------------------------
        // Harness.
        // -------------------------------------------------------------------------------------

        private static void WithViewer(Action<MainWindow, MainViewModel, ScrollViewer> body) =>
            StaRunner.Run(() =>
            {
                // The view-model's awaits resume on the thread that started them, as they do in
                // the application.
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                using var workspace = new SyntheticWorkspace();

                var window = new MainWindow(() => workspace.DatabasePath)
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                };

                try
                {
                    window.Show();

                    var viewModel = (MainViewModel)window.DataContext;

                    viewModel.OpenWorkspaceCommand.Execute(null);

                    PumpUntil(() => viewModel.Messages.Count > 0 && !viewModel.IsBusy);
                    Settle(window);

                    var scroller = window.MessageList.Template.FindName("MessageScroller", window.MessageList)
                        as ScrollViewer
                        ?? throw new InvalidOperationException("The conversation has no scroll viewer.");

                    body(window, viewModel, scroller);
                }
                finally
                {
                    window.Close();
                    SqliteConnection.ClearAllPools();
                }
            });

        private static void ScrollFarDown(MainWindow window, ScrollViewer scroller)
        {
            scroller.ScrollToVerticalOffset(scroller.ExtentHeight * 0.8);
            Settle(window);

            Assert.True(
                scroller.VerticalOffset > 4 * scroller.ViewportHeight,
                $"Scenario needs a far scroll; offset {scroller.VerticalOffset:0.#}, viewport " +
                $"{scroller.ViewportHeight:0.#}.");
        }

        private static void Navigate(MainWindow window, MainViewModel viewModel, Action gesture)
        {
            var range = viewModel.RangeText;

            gesture();

            PumpUntil(() => viewModel.RangeText != range && !viewModel.IsBusy);
            Settle(window);
        }

        private static void AssertAtTop(ScrollViewer scroller, MainViewModel viewModel, string gesture) =>
            Assert.True(
                scroller.VerticalOffset <= Tolerance,
                $"After {gesture} ({viewModel.RangeText}) the conversation is at offset " +
                $"{scroller.VerticalOffset:0.#}, not at the top.");

        /// <summary>Lets layout and any queued scroll commands run to completion.</summary>
        private static void Settle(Window window)
        {
            for (var pass = 0; pass < 4; pass++)
            {
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, () => { });
            }
        }

        /// <summary>
        /// Runs the dispatcher until the condition holds, so the view-model's reads can resume on
        /// this thread.
        /// </summary>
        private static void PumpUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);

            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("The viewer did not reach the expected state in time.");
                }

                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// A synthetic workspace: one conversation between two invented participants, four days of
        /// numbered text messages, and one attachment with nothing resolved.
        /// </summary>
        private sealed class SyntheticWorkspace : IDisposable
        {
            internal const long AttachmentID = 1;

            private const string Stamp = "2026-04-01T00:00:00.0000000Z";

            private readonly string _directoryPath;

            internal SyntheticWorkspace()
            {
                _directoryPath = Path.Combine(
                    Path.GetTempPath(),
                    "Chatcheology.Desktop.Tests",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(_directoryPath);

                DatabasePath = Path.Combine(_directoryPath, "workspace.db");

                WorkspaceDatabase.Initialise(DatabasePath);

                using var connection = WorkspaceDatabase.OpenConnection(DatabasePath);
                using var command = connection.CreateCommand();

                command.CommandText =
                    $"""
                    INSERT INTO ImportSource (SourceType, DisplayName, ImportedDateTimeUtc)
                    VALUES ('SyntheticExport', 'Synthetic export', '{Stamp}');

                    INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                    VALUES ('Ada and Bo', '{Stamp}');

                    INSERT INTO Participant (DisplayName) VALUES ('Ada');
                    INSERT INTO Participant (DisplayName) VALUES ('Bo');

                    INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 1);
                    INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 2);

                    WITH RECURSIVE n(i) AS (
                        SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {Days * MessagesPerDay})
                    INSERT INTO Message (
                        ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                        SenderParticipantID, MessageType, MessageContent, RawContent,
                        SourceLineStart, SourceLineEnd)
                    SELECT 1, 1, i,
                           strftime('%Y-%m-%dT%H:%M:%S', '{FirstDay:yyyy-MM-dd} 08:00:00',
                                    '+' || ((i - 1) / {MessagesPerDay}) || ' days',
                                    '+' || ((i - 1) % {MessagesPerDay}) || ' minutes'),
                           1 + (i % 2), 'User', 'Synthetic message ' || i, 'raw', i, i
                    FROM n;

                    INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                    VALUES (5, 1, 'Unresolved');
                    """;

                command.ExecuteNonQuery();
            }

            internal string DatabasePath { get; }

            public void Dispose()
            {
                SqliteConnection.ClearAllPools();

                try
                {
                    Directory.Delete(_directoryPath, recursive: true);
                }
                catch (IOException)
                {
                    // A leaked handle shows up as a test failure elsewhere, not here.
                }
            }
        }
    }
}
