using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// The change notification every view-model here needs, and nothing else.
    /// </summary>
    /// <remarks>
    /// Twenty lines rather than a framework dependency. This application binds a handful of
    /// properties on four view-models; a toolkit would bring source generators, attributes and a
    /// package to keep current in exchange for saving those lines.
    /// </remarks>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Raises <see cref="PropertyChanged"/> for the calling property.</summary>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// <summary>
        /// Stores <paramref name="value"/> and notifies, unless it is what is already there.
        /// </summary>
        /// <returns>Whether the value changed.</returns>
        protected bool SetProperty<T>(
            ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);

            return true;
        }
    }
}
