using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The date picker: a month at a time, only months that have messages, and only days that have
    /// messages can be tapped. Tapping one jumps to the first message of that day. A year and a month
    /// selector above the grid reach far-past months in two taps.
    /// </summary>
    internal sealed class DatePickerPage : ContentPage
    {
        private readonly DayCalendar _calendar;
        private readonly Func<DateOnly, Task> _jump;
        private readonly Label _title = new() { FontSize = 18, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center };
        private readonly Grid _grid = new() { ColumnSpacing = 4, RowSpacing = 4 };
        private readonly Button _previousYear = new() { Text = "«" };
        private readonly Button _previousMonth = new() { Text = "‹" };
        private readonly Button _nextMonth = new() { Text = "›" };
        private readonly Button _nextYear = new() { Text = "»" };
        private readonly Picker _yearPicker = new() { Title = "Year" };
        private readonly Picker _monthPicker = new() { Title = "Month" };
        private readonly IReadOnlyList<int> _years;
        private IReadOnlyList<CalendarMonthChoice> _monthChoices = [];
        private int _index;
        private bool _chosen;

        /// <summary>Set while the selectors are moved to the shown month, so that move is not taken as a choice.</summary>
        private bool _syncing;

        internal DatePickerPage(DayCalendar calendar, DateOnly near, Func<DateOnly, Task> jump)
        {
            _calendar = calendar;
            _jump = jump;
            _index = Math.Max(0, calendar.IndexOfMonthNear(near));

            Title = "Go to date";

            for (var column = 0; column < 7; column++)
            {
                _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            }

            _previousYear.Clicked += (_, _) => Show(_calendar.YearStep(_index, -1));
            _previousMonth.Clicked += (_, _) => Show(_index - 1);
            _nextMonth.Clicked += (_, _) => Show(_index + 1);
            _nextYear.Clicked += (_, _) => Show(_calendar.YearStep(_index, +1));

            // The selectors (CH-31): only years and months that have messages. A year moves the grid to
            // its month nearest the one shown; a month moves it to that month.
            _years = calendar.Years;
            _yearPicker.ItemsSource = _years.Select(year => year.ToString(System.Globalization.CultureInfo.CurrentCulture)).ToList();

            _yearPicker.SelectedIndexChanged += (_, _) =>
            {
                if (!_syncing && _yearPicker.SelectedIndex >= 0)
                {
                    Show(_calendar.IndexInYear(_years[_yearPicker.SelectedIndex], _calendar.Month(_index).Month));
                }
            };

            _monthPicker.SelectedIndexChanged += (_, _) =>
            {
                if (!_syncing && _monthPicker.SelectedIndex >= 0 && _monthPicker.SelectedIndex < _monthChoices.Count)
                {
                    Show(_monthChoices[_monthPicker.SelectedIndex].Index);
                }
            };

            var selectors = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
                ColumnSpacing = 12,
            };

            selectors.Add(_yearPicker, 0);
            selectors.Add(_monthPicker, 1);

            var header = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 4,
            };

            header.Add(_previousYear, 0);
            header.Add(_previousMonth, 1);
            header.Add(_title, 2);
            header.Add(_nextMonth, 3);
            header.Add(_nextYear, 4);

            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = 16,
                    Spacing = 12,
                    Children =
                    {
                        selectors,
                        header,
                        _grid,
                        new Label
                        {
                            Text = "Only days with messages can be chosen; months without any are skipped.",
                            FontSize = 13,
                        }.TextIn("MutedText"),
                    },
                },
            };

            Show(_index);
        }

        private void Show(int index)
        {
            if (index < 0 || index >= _calendar.MonthCount)
            {
                return;
            }

            _index = index;

            var month = _calendar.Month(index);
            _title.Text = month.Title;

            _previousMonth.IsEnabled = index > 0;
            _nextMonth.IsEnabled = index < _calendar.MonthCount - 1;
            _previousYear.IsEnabled = _calendar.YearStep(index, -1) >= 0;
            _nextYear.IsEnabled = _calendar.YearStep(index, +1) >= 0;

            _syncing = true;

            try
            {
                _yearPicker.SelectedIndex = _years.ToList().IndexOf(month.Year);
                _monthChoices = _calendar.MonthsIn(month.Year);
                _monthPicker.ItemsSource = _monthChoices.Select(choice => choice.Name).ToList();
                _monthPicker.SelectedIndex = _monthChoices.ToList().FindIndex(choice => choice.Index == index);
            }
            finally
            {
                _syncing = false;
            }

            _grid.Children.Clear();
            _grid.RowDefinitions.Clear();

            var rows = 1 + (month.Cells.Count + 6) / 7;

            for (var row = 0; row < rows; row++)
            {
                _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            for (var column = 0; column < 7; column++)
            {
                _grid.Add(new Label { Text = month.WeekdayNames[column], FontSize = 12, HorizontalTextAlignment = TextAlignment.Center }.TextIn("MutedText"), column, 0);
            }

            for (var i = 0; i < month.Cells.Count; i++)
            {
                var cell = month.Cells[i];

                if (cell.Day is not { } day)
                {
                    continue;
                }

                View view;

                if (cell.HasMessages)
                {
                    var button = new Button
                    {
                        Text = cell.Text,
                        Padding = 0,
                        HeightRequest = 44,
                        TextColor = Colors.White,
                    }.Themed(VisualElement.BackgroundColorProperty, "Primary");

                    button.Clicked += async (_, _) =>
                    {
                        // One day only: a second tap while the page closes is ignored.
                        if (_chosen)
                        {
                            return;
                        }

                        _chosen = true;

                        try
                        {
                            await Navigation.PopAsync();
                            await _jump(day);
                        }
                        catch (Exception)
                        {
                            // The jump reports its own read failures; nothing else may end the app.
                        }
                    };

                    view = button;
                }
                else
                {
                    view = new Label
                    {
                        Text = cell.Text,
                        HeightRequest = 44,
                        Opacity = 0.5,
                        HorizontalTextAlignment = TextAlignment.Center,
                        VerticalTextAlignment = TextAlignment.Center,
                    }.TextIn("MutedText");
                }

                _grid.Add(view, i % 7, 1 + i / 7);
            }
        }
    }
}
