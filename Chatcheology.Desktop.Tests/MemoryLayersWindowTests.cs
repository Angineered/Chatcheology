using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;
using Chatcheology.Mobile.Conversation;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// Memory layers in the real window (CH-42 step 5): the rows draw among the messages, the Layers
    /// control and the details area open, and every binding they declare finds its property.
    /// </summary>
    public sealed class MemoryLayersWindowTests
    {
        [Fact]
        public void RowsLayersAndDetails_BindWithoutErrors()
        {
            StaRunner.Run(() =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                using var workspace = new ArchiveTestWorkspace();
                var (_, beach) = MemoryLayersTests.Seed(workspace, photos: true);

                var errors = new BindingErrors();
                PresentationTraceSources.Refresh();
                PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

                var window = new MainWindow(
                    () => workspace.DatabasePath,
                    new LayerSettings(Path.Combine(workspace.Root, "settings", "desktop-layers.json")))
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
                    PumpUntil(() => viewModel.Messages.Count == 5 && !viewModel.IsBusy && viewModel.LayersLoad.IsCompleted);

                    viewModel.LocalParticipant = viewModel.Participants[0];
                    PumpUntil(() => viewModel.LayersLoad.IsCompleted && viewModel.Layers.Count == 2);

                    viewModel.Layers.Single(l => l.CollectionID == MemoryLayersTests.Gaming).IsShown = true;
                    PumpUntil(() => viewModel.LayersLoad.IsCompleted && viewModel.Rows.Count == 10);

                    viewModel.IsLayersOpen = true;
                    Settle(window);

                    // Every row drew, the memory rows with their own template.
                    var list = (ItemsControl)window.FindName("MessageList");
                    Assert.Equal(10, list.Items.Count);
                    Assert.Equal(5, RealisedWhileScrolling<MemoryRowViewModel>(window, list));

                    viewModel.IsLayersOpen = false;

                    viewModel.SelectMemoryRow(viewModel.Rows.OfType<MemoryRowViewModel>().Single(r => r.Key == new MemoryKey(100, 0)));
                    Settle(window);
                    Assert.True(viewModel.IsMemoryDetailsOpen);

                    viewModel.SelectMemoryRow(viewModel.Rows.OfType<MemoryRowViewModel>().Single(r => r.Key == new MemoryKey(beach, null)));
                    Settle(window);
                    Assert.True(viewModel.MemoryDetails!.HasLinks);

                    // CH-46: the strip draws every photo, and the large view opens over the window.
                    var strip = (ItemsControl)window.FindName("MemoryPhotoStrip");
                    Assert.Equal(3, strip.Items.Count);
                    Assert.All(strip.Items.Cast<object>(), item => Assert.True(Drawn(strip, item)));

                    viewModel.OpenMemoryPhoto(1);
                    Settle(window);
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("MemoryPhotoViewer")).Visibility);

                    viewModel.MemoryPhotoViewer!.NextCommand.Execute(null);
                    Settle(window);
                    viewModel.MemoryPhotoViewer!.CloseCommand.Execute(null);
                    Settle(window);
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("MemoryPhotoViewer")).Visibility);

                    viewModel.CloseMemoryDetailsCommand.Execute(null);
                    Settle(window);

                    Assert.Empty(errors.Messages);
                }
                finally
                {
                    PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
                    window.Close();
                    SqliteConnection.ClearAllPools();
                }
            });
        }

        /// <summary>Brings each row into view in turn and counts the rows of type T that drew.</summary>
        private static int RealisedWhileScrolling<T>(Window window, ItemsControl list)
        {
            var panel = Descendant<VirtualizingStackPanel>(list)!;
            var seen = new HashSet<object>();

            for (var i = 0; i < list.Items.Count; i++)
            {
                panel.BringIndexIntoViewPublic(i);
                Settle(window);

                foreach (var item in list.Items.OfType<T>().Where(item => Drawn(list, item!)))
                {
                    seen.Add(item!);
                }
            }

            return seen.Count;
        }

        private static TElement? Descendant<TElement>(DependencyObject root)
            where TElement : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);

                if (child is TElement found)
                {
                    return found;
                }

                if (Descendant<TElement>(child) is { } deeper)
                {
                    return deeper;
                }
            }

            return null;
        }

        private static bool Drawn(ItemsControl list, object item) =>
            list.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter presenter
            && VisualTreeHelper.GetChildrenCount(presenter) > 0;

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

        /// <summary>Binding errors that name one of the memory layers' view-models or the shell's new properties.</summary>
        private sealed class BindingErrors : TraceListener
        {
            private static readonly string[] Names =
            [
                nameof(MemoryRowViewModel),
                nameof(MemoryLayerViewModel),
                nameof(MemoryFilterViewModel),
                nameof(MemoryDetailsViewModel),
                nameof(MemoryItemViewModel),
                nameof(MemoryStripPhotoViewModel),
                nameof(MemoryPhotoViewerViewModel),

                "MemoryLinkLine",
                nameof(MainViewModel),
            ];

            internal List<string> Messages { get; } = [];

            public override void Write(string? message)
            {
            }

            public override void WriteLine(string? message)
            {
                if (message is not null && Names.Any(name => message.Contains(name, StringComparison.Ordinal)))
                {
                    lock (Messages)
                    {
                        Messages.Add(message);
                    }
                }
            }
        }
    }
}
