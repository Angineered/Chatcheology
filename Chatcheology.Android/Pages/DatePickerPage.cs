using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The date picker: a month at a time, only months that have messages, and only days that have
    /// messages can be tapped. Tapping one jumps to the first message of that day.
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
        private int _index;

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
                        header,
                        _grid,
                        new Label
                        {
                            Text = "Only days with messages can be chosen; months without any are skipped.",
                            FontSize = 13,
                            TextColor = (Color)Application.Current!.Resources["MutedText"],
                        },
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

            _grid.Children.Clear();
            _grid.RowDefinitions.Clear();

            var rows = 1 + (month.Cells.Count + 6) / 7;

            for (var row = 0; row < rows; row++)
            {
                _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var muted = (Color)Application.Current!.Resources["MutedText"];
            var primary = (Color)Application.Current!.Resources["Primary"];

            for (var column = 0; column < 7; column++)
            {
                _grid.Add(new Label { Text = month.WeekdayNames[column], FontSize = 12, TextColor = muted, HorizontalTextAlignment = TextAlignment.Center }, column, 0);
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
                        BackgroundColor = primary,
                        TextColor = Colors.White,
                    };

                    button.Clicked += async (_, _) =>
                    {
                        await Navigation.PopAsync();
                        await _jump(day);
                    };

                    view = button;
                }
                else
                {
                    view = new Label
                    {
                        Text = cell.Text,
                        HeightRequest = 44,
                        TextColor = muted,
                        Opacity = 0.5,
                        HorizontalTextAlignment = TextAlignment.Center,
                        VerticalTextAlignment = TextAlignment.Center,
                    };
                }

                _grid.Add(view, i % 7, 1 + i / 7);
            }
        }
    }
}
