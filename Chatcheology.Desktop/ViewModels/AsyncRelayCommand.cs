using System.Windows.Input;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// An <see cref="ICommand"/> over an asynchronous action.
    /// </summary>
    /// <remarks>
    /// Every action in this viewer opens a workspace, reads a page or probes the file system, so
    /// every one of them is asynchronous; a synchronous companion would only ever be reached by
    /// mistake and is deliberately not here.
    /// <para>
    /// The command refuses to start again while it is running. Navigation that supersedes an
    /// earlier request is handled where the request is made, by cancelling the older one, rather
    /// than by letting two overlap here.
    /// </para>
    /// </remarks>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;

        private bool _running;

        /// <summary>Creates a command over <paramref name="execute"/>.</summary>
        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        /// <inheritdoc />
        public event EventHandler? CanExecuteChanged;

        /// <inheritdoc />
        public bool CanExecute(object? parameter) =>
            !_running && (_canExecute is null || _canExecute());

        /// <inheritdoc />
        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _running = true;
            RaiseCanExecuteChanged();

            try
            {
                await _execute().ConfigureAwait(true);
            }
            finally
            {
                _running = false;
                RaiseCanExecuteChanged();
            }
        }

        /// <summary>Tells the binding to ask <see cref="CanExecute"/> again.</summary>
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
