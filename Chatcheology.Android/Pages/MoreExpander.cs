using Chatcheology.Mobile.Settings;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// A "More" control (CH-78): a heading with an open or closed mark that shows or hides <see cref="Items"/> when
    /// tapped. Closed at first; at least 48 dp tall; screen readers hear the name and expanded or collapsed (the mark
    /// is drawn only). Used by Settings (More settings) and the start screen (Import archive).
    /// </summary>
    internal sealed class MoreExpander : VerticalStackLayout
    {
        private readonly Label _mark = new() { FontSize = 14, VerticalOptions = LayoutOptions.Center };
        private readonly Label _name;
        private readonly Grid _heading;
        private MoreControl _state;

        /// <param name="name">What it is called.</param>
        /// <param name="fontSize">The heading's text size.</param>
        internal MoreExpander(string name, double fontSize)
        {
            _state = new MoreControl(name);
            _name = new Label { Text = name, FontSize = fontSize, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
            AutomationProperties.SetIsInAccessibleTree(_mark, false);
            AutomationProperties.SetIsInAccessibleTree(_name, false);

            _heading = new Grid
            {
                ColumnSpacing = 8,
                MinimumHeightRequest = 48,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            };

            _heading.Add(_mark, 0);
            _heading.Add(_name, 1);

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Toggle();
            _heading.GestureRecognizers.Add(tap);

            Spacing = 10;
            Add(_heading);
            Add(Items);
            Show();
        }

        /// <summary>The items behind the control, shown only while it is open.</summary>
        internal VerticalStackLayout Items { get; } = new() { Spacing = 10, IsVisible = false };

        private void Toggle()
        {
            _state = _state.Toggled();
            Show();
        }

        private void Show()
        {
            _mark.Text = _state.Mark;
            Items.IsVisible = _state.IsExpanded;
            SemanticProperties.SetDescription(_heading, _state.ScreenReaderText);
        }
    }
}
