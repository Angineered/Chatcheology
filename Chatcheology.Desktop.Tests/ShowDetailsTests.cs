using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Chatcheology.Data.Media;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// CH-20: the "Show details" toggle. Off by default; with it off a card keeps its media, its
    /// Open media action and its copy label and note, and drops the wording a preview makes
    /// redundant. Toggling is presentation only and never a page move.
    /// </summary>
    /// <remarks>
    /// The first half builds cards in memory from synthetic values; the second lays the window's
    /// own card template out off screen; the third runs the real window over a synthetic
    /// workspace of two pages.
    /// </remarks>
    public sealed class ShowDetailsTests
    {
        // -------------------------------------------------------------------------------------
        // Cards, in memory.
        // -------------------------------------------------------------------------------------

        [Fact]
        public void ACard_StartsWithDetailsOff()
        {
            var card = Card(Unresolved(Phase7UnresolvedPlaceholderReason.NeverEvaluated));

            Assert.False(card.ShowDetails);
            Assert.False(card.AreDetailsShown);
        }

        [Fact]
        public void ResolvedWithAThumbnail_ShowsOnlyThePreviewUntilDetailsAreOn()
        {
            using var directory = new ImageDirectory();

            var card = Card(Resolved("Image"), Available(directory.WriteImage("preview.png"), MediaType.Image));

            Assert.True(card.HasThumbnail);
            Assert.False(card.CanOpenExternally);

            Assert.False(card.IsHeadlineShown);
            Assert.False(card.IsAssetTextShown);
            Assert.False(card.AreDetailsShown);
            Assert.False(card.IsFileNameShown);

            card.ShowDetails = true;

            Assert.True(card.IsHeadlineShown);
            Assert.True(card.IsAssetTextShown);
            Assert.True(card.AreDetailsShown);
            Assert.True(card.IsFileNameShown);
            Assert.Equal("Media resolved", card.HeadlineText);
        }

        [Fact]
        public void ResolvedWithoutAThumbnail_KeepsItsHeadlineAboveOpenMedia()
        {
            var card = Card(
                Resolved("Audio"),
                Available(@"C:\synthetic\voice.opus", MediaType.Audio));

            Assert.False(card.HasThumbnail);
            Assert.True(card.CanOpenExternally);

            Assert.True(card.IsHeadlineShown);
            Assert.Equal("Media resolved", card.HeadlineText);
            Assert.False(card.IsAssetTextShown);
            Assert.False(card.AreDetailsShown);
            Assert.False(card.IsFileNameShown);

            card.ShowDetails = true;

            Assert.True(card.IsHeadlineShown);
            Assert.True(card.IsAssetTextShown);
            Assert.True(card.AreDetailsShown);
            Assert.True(card.IsFileNameShown);
        }

        [Fact]
        public void ResolvedButUnavailable_KeepsItsHeadline()
        {
            var card = Card(Resolved("Audio"), media: null);

            Assert.True(card.IsResolvedAndUnavailable);
            Assert.Equal("Media resolved — file currently unavailable", card.HeadlineText);

            Assert.True(card.IsHeadlineShown);
            Assert.False(card.IsAssetTextShown);
            Assert.False(card.AreDetailsShown);
            Assert.False(card.IsFileNameShown);

            card.ShowDetails = true;

            Assert.True(card.IsHeadlineShown);
            Assert.True(card.IsAssetTextShown);
            Assert.True(card.AreDetailsShown);

            // There is no reachable copy, so there is no file name to show either way.
            Assert.False(card.IsFileNameShown);
        }

        [Theory]
        [InlineData(Phase7UnresolvedPlaceholderReason.NeverEvaluated, "Media not yet matched")]
        [InlineData(Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence, "Media missing")]
        public void AnUnresolvedCard_KeepsItsHeadline(
            Phase7UnresolvedPlaceholderReason reason, string headline)
        {
            var card = Card(Unresolved(reason));

            Assert.Equal(headline, card.HeadlineText);

            Assert.True(card.IsHeadlineShown);
            Assert.False(card.IsAssetTextShown);
            Assert.False(card.AreDetailsShown);
            Assert.False(card.IsFileNameShown);

            card.ShowDetails = true;

            Assert.True(card.IsHeadlineShown);
            Assert.True(card.AreDetailsShown);
            Assert.False(card.IsAssetTextShown);
            Assert.False(card.IsFileNameShown);
        }

        [Fact]
        public void TogglingDetails_RaisesTheVisibilityProperties()
        {
            var card = Card(Unresolved(Phase7UnresolvedPlaceholderReason.NeverEvaluated));
            var raised = new List<string?>();

            card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            card.ShowDetails = true;

            Assert.Contains(nameof(AttachmentViewModel.ShowDetails), raised);
            Assert.Contains(nameof(AttachmentViewModel.IsHeadlineShown), raised);
            Assert.Contains(nameof(AttachmentViewModel.AreDetailsShown), raised);
            Assert.Contains(nameof(AttachmentViewModel.IsAssetTextShown), raised);
            Assert.Contains(nameof(AttachmentViewModel.IsFileNameShown), raised);
        }

        [Fact]
        public void WithDetailsOff_TheLowQualityLabelAndNoteBehaveAsBefore()
        {
            var copy = Card(
                Unresolved(Phase7UnresolvedPlaceholderReason.NeverEvaluated),
                fact: CopyFact(1, copyOf: 2));
            var full = Card(
                Unresolved(Phase7UnresolvedPlaceholderReason.NeverEvaluated),
                lowQualityCopies: 1);

            Assert.False(copy.ShowDetails);
            Assert.False(full.ShowDetails);

            Assert.False(copy.IsShown);
            Assert.False(copy.HasLowQualityCopyLabel);
            Assert.True(full.HasLowQualityCopyNote);

            copy.ShowLowQualityCopies = true;
            full.ShowLowQualityCopies = true;

            Assert.True(copy.IsShown);
            Assert.True(copy.HasLowQualityCopyLabel);
            Assert.False(full.HasLowQualityCopyNote);

            // The details toggle leaves the copy presentation alone in either direction.
            copy.ShowDetails = true;
            full.ShowDetails = true;
            copy.ShowDetails = false;
            full.ShowDetails = false;

            Assert.True(copy.IsShown);
            Assert.True(copy.HasLowQualityCopyLabel);
            Assert.False(full.HasLowQualityCopyNote);
        }

        // -------------------------------------------------------------------------------------
        // The window's own card template, off screen.
        // -------------------------------------------------------------------------------------

        [Fact]
        public void TheCardTemplate_HidesTheDetailLinesUntilDetailsAreOn()
        {
            using var directory = new ImageDirectory();
            var path = directory.WriteImage("preview.png");

            StaRunner.Run(() =>
            {
                var withPreview = Card(Resolved("Image"), Available(path, MediaType.Image));

                WithCard(withPreview, presenter =>
                {
                    Assert.Equal(Visibility.Collapsed, LineVisibility(presenter, withPreview.HeadlineText));
                    Assert.Equal(Visibility.Collapsed, LineVisibility(presenter, withPreview.AssetText));
                    Assert.Equal(Visibility.Collapsed, LineVisibility(presenter, withPreview.DetailText));
                    Assert.Equal(Visibility.Collapsed, LineVisibility(presenter, withPreview.FileName));
                    Assert.Equal(Visibility.Visible, Find<Image>(presenter)!.Visibility);

                    withPreview.ShowDetails = true;
                    presenter.UpdateLayout();

                    Assert.Equal(Visibility.Visible, LineVisibility(presenter, withPreview.HeadlineText));
                    Assert.Equal(Visibility.Visible, LineVisibility(presenter, withPreview.AssetText));
                    Assert.Equal(Visibility.Visible, LineVisibility(presenter, withPreview.DetailText));
                    Assert.Equal(Visibility.Visible, LineVisibility(presenter, withPreview.FileName));
                });

                var audio = Card(Resolved("Audio"), Available(@"C:\synthetic\voice.opus", MediaType.Audio));

                WithCard(audio, presenter =>
                {
                    Assert.Equal(Visibility.Visible, LineVisibility(presenter, "Media resolved"));
                    Assert.Equal(Visibility.Collapsed, LineVisibility(presenter, audio.DetailText));
                    Assert.Equal(Visibility.Visible, Find<Button>(presenter)!.Visibility);
                });

                var missing = Card(Unresolved(Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence));

                WithCard(missing, presenter =>
                {
                    Assert.Equal(Visibility.Visible, LineVisibility(presenter, "Media missing"));
                    Assert.Equal(Visibility.Collapsed, LineVisibility(presenter, missing.DetailText));
                    Assert.Equal(
                        Visibility.Visible,
                        Find<System.Windows.Shapes.Rectangle>(presenter)!.Visibility);
                });
            });
        }

        // -------------------------------------------------------------------------------------
        // The real window over a synthetic workspace.
        // -------------------------------------------------------------------------------------

        [Fact]
        public void TheViewer_TogglesDetailsWithoutMovingAndNewPagesInheritIt() =>
            WithViewer((window, viewModel, scroller) =>
            {
                Assert.False(viewModel.ShowDetails);
                Assert.Equal(Visibility.Visible, window.ShowDetailsToggle.Visibility);
                Assert.False(window.ShowDetailsToggle.IsChecked);

                var first = Cards(viewModel);

                Assert.NotEmpty(first);
                Assert.All(first, card => Assert.False(card.ShowDetails));

                scroller.ScrollToVerticalOffset(scroller.ExtentHeight * 0.5);
                Settle(window);

                var offset = scroller.VerticalOffset;
                Assert.True(offset > 0, "Scenario needs a scrolled list.");

                var moved = 0;
                viewModel.MovedToPage += (_, _) => moved++;

                var range = viewModel.RangeText;
                var messages = viewModel.Messages.ToList();

                window.ShowDetailsToggle.IsChecked = true;
                Settle(window);

                Assert.True(viewModel.ShowDetails);
                Assert.All(first, card => Assert.True(card.ShowDetails));

                // Presentation only: not a move, not a rebuild, not a reload.
                Assert.Equal(0, moved);
                Assert.Equal(range, viewModel.RangeText);
                Assert.Equal(messages, viewModel.Messages.ToList());
                Assert.True(scroller.VerticalOffset > 0, "Toggling details moved the list to the top.");

                // A page built later starts with the toggle as it stands.
                viewModel.NextCommand.Execute(null);
                PumpUntil(() => viewModel.RangeText != range && !viewModel.IsBusy);
                Settle(window);

                Assert.Equal(1, moved);

                var second = Cards(viewModel);

                Assert.NotEmpty(second);
                Assert.All(second, card => Assert.True(card.ShowDetails));

                var nextRange = viewModel.RangeText;

                viewModel.ShowDetails = false;
                Settle(window);

                Assert.All(second, card => Assert.False(card.ShowDetails));
                Assert.Equal(1, moved);
                Assert.Equal(nextRange, viewModel.RangeText);
            });

        // -------------------------------------------------------------------------------------
        // Harness.
        // -------------------------------------------------------------------------------------

        private static List<AttachmentViewModel> Cards(MainViewModel viewModel) =>
            viewModel.Messages.SelectMany(message => message.Attachments).ToList();

        private static void WithCard(AttachmentViewModel card, Action<ContentPresenter> body)
        {
            var window = new MainWindow
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };

            try
            {
                var presenter = new ContentPresenter
                {
                    Content = card,
                    ContentTemplate = (DataTemplate)window.FindResource("AttachmentTemplate"),
                };

                window.Content = presenter;
                window.Show();
                window.UpdateLayout();

                body(presenter);
            }
            finally
            {
                window.Close();
            }
        }

        private static Visibility LineVisibility(DependencyObject root, string text)
        {
            Assert.False(string.IsNullOrEmpty(text));

            var line = FindAll<TextBlock>(root).SingleOrDefault(block => block.Text == text)
                ?? throw new InvalidOperationException($"The card has no line reading '{text}'.");

            return line.Visibility;
        }

        private static T? Find<T>(DependencyObject root) where T : DependencyObject =>
            FindAll<T>(root).FirstOrDefault();

        private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);

                if (child is T match)
                {
                    yield return match;
                }

                foreach (var nested in FindAll<T>(child))
                {
                    yield return nested;
                }
            }
        }

        private static AttachmentViewModel Card(
            Phase7ReconstructionState state,
            MediaAvailability? media = null,
            AttachmentMsgstoreFact? fact = null,
            int lowQualityCopies = 0) =>
            new(
                new ConversationAttachment { AttachmentID = state.AttachmentID, Ordinal = 1 },
                state,
                media,
                fact,
                lowQualityCopies);

        private static MediaAvailability Available(string fullPath, MediaType mediaType) =>
            new(
                1,
                new MediaAssetLocation
                {
                    MediaAssetID = 7,
                    MediaFileID = 70,
                    MediaSourceID = 1,
                    MediaSourceDisplayName = "Synthetic source",
                    RootPath = Path.GetDirectoryName(fullPath)!,
                    RelativePath = Path.GetFileName(fullPath),
                    FileName = Path.GetFileName(fullPath),
                    Extension = Path.GetExtension(fullPath),
                    MediaType = mediaType,
                    SizeBytes = 2048,
                    FullPath = fullPath,
                });

        private static AttachmentMsgstoreFact CopyFact(long attachmentID, long copyOf) =>
            new()
            {
                AttachmentID = attachmentID,
                IsForwarded = false,
                ForwardScore = null,
                LowQualityCopyOfAttachmentID = copyOf,
                SourceBatch = "synthetic",
                RecordedDateTimeUtc = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc),
            };

        private static Phase7ReconstructionState Unresolved(Phase7UnresolvedPlaceholderReason reason) =>
            new()
            {
                AttachmentID = 42,
                Status = Phase7ResolutionStatus.Unresolved,
                ResolvedAsset = null,
                PlaceholderReason = reason,
                EvidenceRunID = null,
                ExactDateCandidateCount = null,
                AdjacentDateContextCount = null,
                IsWithinDirectionImpossibleGroup = null,
                CandidateTierCount = null,
                HasCurrentReviewedInsufficientEvidence =
                    reason == Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence,
                AllCurrentCandidatesIndependentlyDisproven = false,
            };

        private static Phase7ReconstructionState Resolved(string mediaType) =>
            new()
            {
                AttachmentID = 42,
                Status = Phase7ResolutionStatus.Resolved,
                ResolvedAsset = new Phase7ResolvedAssetDescriptor
                {
                    MediaAssetID = 7,
                    MediaType = mediaType,
                    SizeBytes = 2048,
                    DurationMS = null,
                    Width = null,
                    Height = null,
                },
                PlaceholderReason = null,
                EvidenceRunID = null,
                ExactDateCandidateCount = null,
                AdjacentDateContextCount = null,
                IsWithinDirectionImpossibleGroup = null,
                CandidateTierCount = null,
                HasCurrentReviewedInsufficientEvidence = false,
                AllCurrentCandidatesIndependentlyDisproven = false,
            };

        private static void WithViewer(Action<MainWindow, MainViewModel, ScrollViewer> body) =>
            StaRunner.Run(() =>
            {
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

        private static void Settle(Window window)
        {
            for (var pass = 0; pass < 4; pass++)
            {
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, () => { });
            }
        }

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

        /// <summary>A temporary folder holding small, entirely invented images.</summary>
        private sealed class ImageDirectory : IDisposable
        {
            internal ImageDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "Chatcheology.Desktop.Tests",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            internal string WriteImage(string fileName)
            {
                const int Width = 48;
                const int Height = 32;

                var stride = Width * 4;
                var pixels = new byte[stride * Height];

                for (var index = 0; index < pixels.Length; index += 4)
                {
                    pixels[index] = 0x40;
                    pixels[index + 1] = 0x80;
                    pixels[index + 2] = 0xC0;
                    pixels[index + 3] = 0xFF;
                }

                var bitmap = BitmapSource.Create(
                    Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                var path = System.IO.Path.Combine(Path, fileName);

                using var stream = File.Create(path);
                encoder.Save(stream);

                return path;
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // A leaked handle shows up as a test failure elsewhere, not here.
                }
            }
        }

        /// <summary>
        /// A synthetic workspace: one conversation between two invented participants, two pages of
        /// numbered text messages, and one unresolved attachment on each page.
        /// </summary>
        private sealed class SyntheticWorkspace : IDisposable
        {
            private const int MessageCount = 320;

            private const string Stamp = "2026-04-01T00:00:00.0000000Z";

            private readonly string _directoryPath;

            internal SyntheticWorkspace()
            {
                _directoryPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "Chatcheology.Desktop.Tests",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(_directoryPath);

                DatabasePath = System.IO.Path.Combine(_directoryPath, "workspace.db");

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
                        SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {MessageCount})
                    INSERT INTO Message (
                        ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                        SenderParticipantID, MessageType, MessageContent, RawContent,
                        SourceLineStart, SourceLineEnd)
                    SELECT 1, 1, i,
                           strftime('%Y-%m-%dT%H:%M:%S', '2026-04-06 08:00:00',
                                    '+' || (i - 1) || ' minutes'),
                           1 + (i % 2), 'User', 'Synthetic message ' || i, 'raw', i, i
                    FROM n;

                    INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                    VALUES (5, 1, 'Unresolved');

                    INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                    VALUES (200, 1, 'Unresolved');
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
