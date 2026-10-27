namespace Chatcheology.Android
{
    /// <summary>
    /// The app's colours for code (CH-41): each name has a Light and a Dark value in App.xaml
    /// (<c>&lt;name&gt;Light</c>, <c>&lt;name&gt;Dark</c>), and the phone's setting picks one.
    /// </summary>
    /// <remarks>
    /// <see cref="Set"/> binds a property to the pair, so it follows a change of the phone's setting while
    /// the screen is open; <see cref="Current"/> is for Android views, which read a colour once and are
    /// recoloured by their owner when <see cref="Application.RequestedThemeChanged"/> is raised.
    /// </remarks>
    internal static class Palette
    {
        /// <summary>Whether the phone is in dark mode now.</summary>
        internal static bool IsDark => Application.Current?.RequestedTheme == AppTheme.Dark;

        /// <summary>The colour <paramref name="name"/> for the phone's current mode.</summary>
        internal static Color Current(string name) => IsDark ? Dark(name) : Light(name);

        /// <summary>The light value of <paramref name="name"/>.</summary>
        internal static Color Light(string name) => (Color)Application.Current!.Resources[name + "Light"];

        /// <summary>The dark value of <paramref name="name"/>.</summary>
        internal static Color Dark(string name) => (Color)Application.Current!.Resources[name + "Dark"];

        /// <summary>Binds <paramref name="property"/> of <paramref name="target"/> to the colour pair <paramref name="name"/>.</summary>
        internal static void Set(BindableObject target, BindableProperty property, string name) =>
            target.SetAppThemeColor(property, Light(name), Dark(name));

        /// <summary><see cref="Set"/>, returning <paramref name="target"/>, for use where a control is created.</summary>
        internal static T Themed<T>(this T target, BindableProperty property, string name)
            where T : BindableObject
        {
            Set(target, property, name);
            return target;
        }

        /// <summary>A label's text colour from the pair <paramref name="name"/>, returning the label.</summary>
        internal static T TextIn<T>(this T label, string name)
            where T : Label => label.Themed(Label.TextColorProperty, name);
    }
}
