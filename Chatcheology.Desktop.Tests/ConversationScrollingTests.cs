using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// CH-7: the conversation pane must let the operator scroll every message fully into view,
    /// including tall photo messages and the last message of a page.
    /// </summary>
    /// <remarks>
    /// <para>These tests lay out the real <see cref="MainWindow"/> and its real conversation list,
    /// with the list's panel, scrolling and virtualisation settings exactly as the application
    /// ships them. Only the items are synthetic: plain blocks of known heights standing in for
    /// message bubbles, so the test is about the list's scrolling and not about any data.</para>
    /// <para>The heights mirror the report. A photo message is an image of up to 320 pixels plus
    /// its headline, detail and file-name lines and the bubble's padding, roughly 460 pixels in
    /// all; an album-like message with several attachments is taller than the window.</para>
    /// <para>The review panel's candidate list is covered the same way.</para>
    /// </remarks>
    public sealed class ConversationScrollingTests
    {
        private const double TextMessage = 60;
        private const double PhotoMessage = 460;
        private const double TallPhotoMessage = 700;
        private const double CandidateCard = 380;
        private const double Tolerance = 0.5;

        /// <summary>
        /// The reported end-of-page shape. Item scrolling happened to get this one right, so it
        /// guards the pixel-scrolling end-of-list estimate rather than reproducing the defect.
        /// </summary>
        [Fact]
        public void LastPhotoAfterTwoTallPhotosCanBeFullyScrolledIntoView()
        {
            var heights = Enumerable.Repeat(TextMessage, 40)
                .Concat([TallPhotoMessage, TallPhotoMessage, PhotoMessage])
                .ToList();

            StaRunner.Run(() => WithConversation(heights, list =>
            {
                list.ScrollViewer.ScrollToEnd();
                list.Settle();

                var last = list.BoundsOf(heights.Count - 1);

                Assert.True(
                    last.Top >= -Tolerance && last.Bottom <= list.ViewportHeight + Tolerance,
                    $"Last item spans {last.Top:0.#}–{last.Bottom:0.#}; viewport is " +
                    $"0–{list.ViewportHeight:0.#}.");
            }));
        }

        [Fact]
        public void BottomOfALastMessageTallerThanTheViewportCanBeReached()
        {
            var heights = Enumerable.Repeat(TextMessage, 40)
                .Concat([PhotoMessage, 4 * PhotoMessage])
                .ToList();

            StaRunner.Run(() => WithConversation(heights, list =>
            {
                Assert.True(4 * PhotoMessage > list.ViewportHeight, "Scenario needs a tall item.");

                list.ScrollViewer.ScrollToEnd();
                list.Settle();

                var last = list.BoundsOf(heights.Count - 1);

                Assert.True(
                    Math.Abs(last.Bottom - list.ViewportHeight) <= Tolerance,
                    $"Last item's bottom is at {last.Bottom:0.#}; viewport bottom is " +
                    $"{list.ViewportHeight:0.#}.");
            }));
        }

        /// <summary>
        /// A run of photos in one minute, some of them taller than the window (as on a smaller
        /// window, or with a caption), scrolled a line at a time: every photo's lower edge must
        /// be on screen at some point.
        /// </summary>
        [Theory]
        [InlineData(22)]
        [InlineData(7)]
        public void ScrollingThroughARunOfPhotosShowsTheBottomOfEveryPhoto(int photoCount)
        {
            var heights = Enumerable.Repeat(TextMessage, 10)
                .Concat(Enumerable.Range(0, photoCount)
                    .Select(i => i % 3 == 2 ? PhotoMessage : TallPhotoMessage))
                .ToList();

            StaRunner.Run(() => WithConversation(heights, list =>
            {
                var unseen = list.WalkToEnd(() => list.ScrollViewer.LineDown());

                Assert.True(
                    unseen.Count == 0,
                    $"Never fully scrolled into view: items {string.Join(", ", unseen)} of " +
                    $"{heights.Count}.");
            }));
        }

        /// <summary>
        /// The same run, each photo message shorter than the window, scrolled with the mouse wheel:
        /// no photo may be skipped past without its whole bubble having been on screen.
        /// </summary>
        [Theory]
        [InlineData(22)]
        [InlineData(7)]
        public void WheelingThroughARunOfPhotosShowsEveryPhotoWhole(int photoCount)
        {
            var heights = Enumerable.Repeat(TextMessage, 10)
                .Concat(Enumerable.Repeat(PhotoMessage, photoCount))
                .ToList();

            StaRunner.Run(() => WithConversation(heights, list =>
            {
                Assert.True(PhotoMessage < list.ViewportHeight, "Scenario needs photos that fit.");

                var unseen = list.WalkToEnd(() => list.WheelDownOneNotch());

                Assert.True(
                    unseen.Count == 0,
                    $"Never fully on screen: items {string.Join(", ", unseen)} of {heights.Count}.");
            }));
        }

        /// <summary>
        /// The review panel's candidate list has the same arrangement and the same hazard: a
        /// candidate card with its 260-pixel preview is taller than the space the panel's pinned
        /// actions leave the list, so each card's lower part must still be reachable.
        /// </summary>
        [Fact]
        public void ScrollingThroughCandidatesShowsTheBottomOfEveryCard()
        {
            var heights = Enumerable.Repeat(CandidateCard, 6).ToList();

            StaRunner.Run(() => WithList(window => window.CandidateList, heights, list =>
            {
                Assert.True(list.ViewportHeight > 0, "The candidate list was not laid out.");

                var unseen = list.WalkToEnd(() => list.WheelDownOneNotch());

                Assert.True(
                    unseen.Count == 0,
                    $"Never fully scrolled into view: candidates {string.Join(", ", unseen)} " +
                    $"of {heights.Count}; viewport {list.ViewportHeight:0.#}.");
            }));
        }

        /// <summary>
        /// The fix must not cost the list its virtualisation: a long page still realises only the
        /// containers near the viewport.
        /// </summary>
        [Fact]
        public void LongConversationStaysVirtualised()
        {
            var heights = Enumerable.Range(0, 2000)
                .Select(i => i % 5 == 0 ? TallPhotoMessage : TextMessage)
                .ToList();

            StaRunner.Run(() => WithConversation(heights, list =>
            {
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list.Control));

                list.ScrollViewer.ScrollToVerticalOffset(list.ScrollViewer.ExtentHeight / 2);
                list.Settle();
                list.ScrollViewer.ScrollToEnd();
                list.Settle();

                var realised = Enumerable.Range(0, heights.Count)
                    .Count(i => list.Control.ItemContainerGenerator.ContainerFromIndex(i) is not null);

                Assert.InRange(realised, 1, 100);
            }));
        }

        // -------------------------------------------------------------------------------------
        // Harness.
        // -------------------------------------------------------------------------------------

        private sealed record SyntheticMessage(int Index, double Height);

        private const string SyntheticTemplate =
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "  <Border Height='{Binding Height}' Margin='8,2' Background='White' />" +
            "</DataTemplate>";

        private static void WithConversation(IReadOnlyList<double> heights, Action<ListProbe> body) =>
            WithList(window => window.MessageList, heights, body);

        /// <summary>
        /// Lays out the picked list in a real, shown <see cref="MainWindow"/>, with synthetic
        /// items of the given heights.
        /// </summary>
        private static void WithList(
            Func<MainWindow, ItemsControl> pick,
            IReadOnlyList<double> heights,
            Action<ListProbe> body)
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
                var list = pick(window);

                // A panel hidden until a view-model opens it (the review panel) is shown
                // directly; nothing about its layout is changed.
                for (DependencyObject? node = list; node is not null and not Window;
                     node = LogicalTreeHelper.GetParent(node))
                {
                    if (node is UIElement { Visibility: not Visibility.Visible } hidden)
                    {
                        hidden.Visibility = Visibility.Visible;
                    }
                }

                // The synthetic items have their own template; the panel, scroll viewer and
                // virtualisation settings are the window's own.
                list.ItemTemplate = (DataTemplate)XamlReader.Parse(SyntheticTemplate);
                list.ItemsSource = heights.Select((h, i) => new SyntheticMessage(i, h)).ToList();

                window.Show();

                var probe = new ListProbe(list);
                probe.Settle();

                body(probe);
            }
            finally
            {
                window.Close();
            }
        }

        private sealed class ListProbe
        {
            private readonly FrameworkElement _viewport;

            internal ListProbe(ItemsControl control)
            {
                Control = control;
                Settle();

                ScrollViewer = FindDescendant<ScrollViewer>(control)
                    ?? throw new InvalidOperationException("The list has no scroll viewer.");

                _viewport = FindDescendant<ScrollContentPresenter>(ScrollViewer)
                    ?? throw new InvalidOperationException("The scroll viewer has no presenter.");
            }

            internal ItemsControl Control { get; }

            internal ScrollViewer ScrollViewer { get; }

            internal double ViewportHeight => _viewport.ActualHeight;

            /// <summary>
            /// Lets queued scroll commands and the re-measures they cause run to completion. A
            /// virtualising panel can need more than one pass to settle after a jump.
            /// </summary>
            internal void Settle()
            {
                for (var pass = 0; pass < 4; pass++)
                {
                    Control.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, () => { });
                }
            }

            /// <summary>An item's bounds in viewport coordinates.</summary>
            internal Rect BoundsOf(int index)
            {
                var container = Control.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement
                    ?? throw new InvalidOperationException($"Item {index} is not realised.");

                return container
                    .TransformToAncestor(_viewport)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            }

            /// <summary>
            /// One notch of the mouse wheel, raised as a real wheel event so the scroll viewer and
            /// its panel handle it exactly as they handle the operator's wheel.
            /// </summary>
            internal void WheelDownOneNotch()
            {
                ScrollViewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -Mouse.MouseWheelDeltaForOneLine)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                });
            }

            /// <summary>
            /// Scrolls from the top to the end one <paramref name="step"/> at a time, and returns
            /// the items whose lower edge was never on screen (with their top, if they fit).
            /// </summary>
            internal List<int> WalkToEnd(Action step)
            {
                var count = Control.Items.Count;
                var seen = new bool[count];

                ScrollViewer.ScrollToHome();
                Settle();

                for (var i = 0; i < 5000; i++)
                {
                    MarkVisibleBottoms(seen);

                    var before = ScrollViewer.VerticalOffset;

                    step();
                    Settle();

                    if (ScrollViewer.VerticalOffset <= before)
                    {
                        break;
                    }
                }

                MarkVisibleBottoms(seen);

                return Enumerable.Range(0, count).Where(i => !seen[i]).ToList();
            }

            /// <summary>
            /// Marks every realised item whose lower edge is inside the viewport, and whose top is
            /// too unless the item is taller than the viewport.
            /// </summary>
            internal void MarkVisibleBottoms(bool[] seen)
            {
                for (var i = 0; i < seen.Length; i++)
                {
                    if (Control.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement)
                    {
                        continue;
                    }

                    var bounds = BoundsOf(i);
                    var bottomVisible = bounds.Bottom >= -Tolerance
                        && bounds.Bottom <= ViewportHeight + Tolerance;
                    var topVisibleIfItFits = bounds.Height > ViewportHeight
                        || bounds.Top >= -Tolerance;

                    if (bottomVisible && topVisibleIfItFits && bounds.Bottom > 0)
                    {
                        seen[i] = true;
                    }
                }
            }

            private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);

                    if (child is T match)
                    {
                        return match;
                    }

                    if (FindDescendant<T>(child) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;
            }
        }
    }
}
