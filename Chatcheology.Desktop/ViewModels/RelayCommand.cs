using System.Windows.Input;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// An <see cref="ICommand"/> over a synchronous action.
    /// </summary>
    /// <remarks>
    /// Phase 8.0 deliberately had no synchronous command, because every action it offered read the
    /// workspace. The review panel adds actions that touch nothing — arming a decision so its
    /// consequence can be stated, and standing back down from one — and making those asynchronous
    /// would dress up a field assignment as work.
    /// </remarks>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        /// <summary>Creates a command over <paramref name="execute"/>.</summary>
        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        /// <inheritdoc />
        public event EventHandler? CanExecuteChanged;

        /// <inheritdoc />
        public bool CanExecute(object? parameter) => _canExecute is null || _canExecute();

        /// <inheritdoc />
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                _execute();
            }
        }

        /// <summary>Tells the binding to ask <see cref="CanExecute"/> again.</summary>
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
