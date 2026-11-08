using System.Diagnostics;
using System.Globalization;
using Chatcheology.Mobile.Import;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The import under way and how it ended: progress with Cancel while copying, an indeterminate
    /// "Checking…" without Cancel while the copy is validated, the screen kept on throughout, then the
    /// outcome. Sizes, counts and check names only: never a file name, path or message text.
    /// </summary>
    /// <remarks>
    /// CH-71 step 2: the page also asks the one-time adoption question (<see cref="IImportQuestions"/>) when a version 4
    /// archive's earlier messages are exactly an archive installed before version 4: Replace / Import as new / Cancel.
    /// </remarks>
    internal sealed class ImportPage : ContentPage, IImportQuestions
    {
        private readonly AppState _state;
        private readonly IImportSource _source;
        private readonly StartPage _start;
        private readonly ImportReadCounts _readCounts;
        private readonly bool _locationRefused;
        private readonly CancellationTokenSource _cancel = new();

        private readonly Label _stage = new() { FontSize = 18, FontAttributes = FontAttributes.Bold };
        private readonly ProgressBar _bar = new();
        private readonly ActivityIndicator _checking = new() { IsVisible = false, IsRunning = false };
        private readonly Label _detail = new() { FontSize = 14 };
        private readonly Button _cancelButton = new() { Text = "Cancel" };
        private readonly VerticalStackLayout _outcome = new() { Spacing = 8, IsVisible = false };
        private bool _running;
        private bool _leaving;

        internal ImportPage(AppState state, IImportSource source, StartPage start, ImportReadCounts readCounts, bool locationRefused)
        {
            _state = state;
            _source = source;
            _start = start;
            _readCounts = readCounts;
            _locationRefused = locationRefused;

            Title = "Import";
            NavigationPage.SetHasBackButton(this, false);

            _cancelButton.Clicked += (_, _) =>
            {
                _cancelButton.IsEnabled = false;
                _detail.Text = "Cancelling…";
                _cancel.Cancel();
            };

            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = 16,
                    Spacing = 12,
                    Children = { _stage, _bar, _checking, _detail, _cancelButton, _outcome },
                },
            };

            _stage.Text = "Reading the folder…";
        }

        /// <summary>Starts the import; the page shows its progress and outcome.</summary>
        internal async void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            DeviceDisplay.Current.KeepScreenOn = true;

            var watch = Stopwatch.StartNew();
            var progress = new LatestProgress(this);
            ImportResult? result = null;
            string? failure = null;

            try
            {
                result = await Task.Run(() => _state.Importer.Import(_source, progress, _cancel.Token, this));

                // Counts only: never an id, a title or text.
                var refreshed = result.Outcome == ImportOutcome.Refreshed
                    ? $" added={result.AddedMessages} adopted={(result.Adopted ? 1 : 0)} replacedRemoved={(result.ReplacedRemoved ? 1 : 0)}"
                    : result.UnmatchedOlderArchives > 0 ? $" unmatchedOlder={result.UnmatchedOlderArchives}" : string.Empty;
                AppState.LogTiming("import", watch.Elapsed, $"outcome={result.Outcome} refusal={result.Refusal}{refreshed} {_readCounts}");
            }
            catch (Exception)
            {
                // Anything the importer does not turn into a result (it has already deleted its copy):
                // reported here rather than ending the app from an async handler.
                failure = "The import stopped with an error. Nothing was kept.";
            }
            finally
            {
                DeviceDisplay.Current.KeepScreenOn = false;
                _running = false;
                _state.Importing = false;
                NavigationPage.SetHasBackButton(this, true);
            }

            ShowOutcome(result, failure);
        }

        /// <inheritdoc />
        protected override bool OnBackButtonPressed() => _running || base.OnBackButtonPressed();

        /// <summary>
        /// The adoption question (CH-71 step 2), asked on the UI thread while the import's worker thread waits. Back,
        /// a dismissed sheet or any failure answers Cancel, so nothing is replaced without a Replace tap.
        /// </summary>
        public AdoptionAnswer AskAdoption(AdoptionQuestion question, CancellationToken cancellationToken)
        {
            try
            {
                var button = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var stage = _stage.Text;
                    var detail = _detail.Text;

                    try
                    {
                        _stage.Text = AdoptionQuestion.Heading;
                        _detail.Text = question.Detail;

                        return await DisplayActionSheetAsync(
                            question.Text, AdoptionQuestion.CancelButton, null, AdoptionQuestion.ReplaceButton, AdoptionQuestion.ImportAsNewButton);
                    }
                    finally
                    {
                        _stage.Text = stage;
                        _detail.Text = detail;
                    }
                }).WaitAsync(cancellationToken).GetAwaiter().GetResult();

                return AdoptionQuestion.AnswerFor(button);
            }
            catch (Exception)
            {
                // Never replace on doubt; nothing here may end the app.
                return AdoptionAnswer.Cancel;
            }
        }

        private void Report(ImportProgress value)
        {
            if (!_running)
            {
                return;
            }

            if (value.Stage == ImportStage.Copying)
            {
                _stage.Text = "Copying the archive…";
                _bar.Progress = value.Fraction;
                _detail.Text = value.Text;
                return;
            }

            // Checking: no progress and no cancel; the validator runs to the end.
            _stage.Text = "Checking…";
            _bar.IsVisible = false;
            _cancelButton.IsVisible = false;
            _checking.IsVisible = true;
            _checking.IsRunning = true;
            _detail.Text = "The copy is being checked. This cannot be cancelled.";
        }

        private void ShowOutcome(ImportResult? result, string? failure)
        {
            _bar.IsVisible = false;
            _checking.IsVisible = false;
            _checking.IsRunning = false;
            _cancelButton.IsVisible = false;
            _outcome.Children.Clear();
            _outcome.IsVisible = true;

            _stage.Text = result?.Outcome switch
            {
                ImportOutcome.Installed => "Imported",
                ImportOutcome.Refreshed => "Updated",
                ImportOutcome.AlreadyInstalled => "Already imported",
                ImportOutcome.NotEnoughSpace => "Not enough space",
                ImportOutcome.Cancelled => "Cancelled",
                _ => "Not imported",
            };

            _detail.Text = failure ?? result!.Message;

            if (result is { Refusal: ImportRefusal.FailedValidation })
            {
                foreach (var check in result.FailedChecks)
                {
                    _outcome.Children.Add(new Label
                    {
                        FontSize = 14,
                        Text = check.Ran
                            ? $"{check.Name}: {check.Problems.ToString("N0", CultureInfo.CurrentCulture)} problems in {check.Examined.ToString("N0", CultureInfo.CurrentCulture)} examined"
                            : $"{check.Name}: could not be run",
                    });
                }

                // CH-58: refused photo-location access may be why; one generic line, nothing per file.
                if (MediaLocationAccess.ShowsRefusedLine(_locationRefused, result))
                {
                    _outcome.Children.Add(new Label { FontSize = 14, Text = MediaLocationAccess.RefusedLine });
                }
            }

            if (result is { Outcome: ImportOutcome.Refreshed, ReplacedRemoved: true, ReplacedInstallId: { } replaced })
            {
                // CH-71 step 2: the replaced copy's noted-date notification goes; the new copy's is scheduled below.
                // The own-memories notification ("A memory today", one per phone) is deliberately not cancelled even when
                // its tap names the replaced folder: cancelling would remove the owner's reminder for the day. Its tap then
                // finds no such folder and falls back to the start screen, or at app start to the usual opening rule (StartPage
                // looks the id up and opens nothing for a missing one); the next one names the
                // new folder (UpdateAsync below picks the last-opened archive, whose last-opened time was carried over).
                NotedDateNotifier.Cancel(replaced);
            }

            if (result is { Outcome: ImportOutcome.Installed or ImportOutcome.Refreshed })
            {
                // Its noted dates notify from now on (CH-47).
                _ = NotedDateNotifier.UpdateAsync();
            }

            if (result is { Outcome: ImportOutcome.Installed or ImportOutcome.Refreshed or ImportOutcome.AlreadyInstalled, InstallId: { } id })
            {
                var open = new Button { Text = "Open the archive", TextColor = Colors.White }.Themed(VisualElement.BackgroundColorProperty, "Primary");
                open.Clicked += async (_, _) =>
                {
                    if (!Leave())
                    {
                        return;
                    }

                    try
                    {
                        await Navigation.PopAsync();
                        await _start.OpenByIdAsync(id);
                    }
                    catch (Exception)
                    {
                        // The start screen shows what it can; nothing here may end the app.
                    }
                };

                _outcome.Children.Add(open);
            }

            var back = new Button { Text = "Back" };
            back.Clicked += async (_, _) =>
            {
                if (!Leave())
                {
                    return;
                }

                try
                {
                    await Navigation.PopAsync();
                }
                catch (Exception)
                {
                    // Nothing here may end the app (CH-60).
                }
            };
            _outcome.Children.Add(back);
        }

        /// <summary>True the first time only: a second tap while the page closes does nothing.</summary>
        private bool Leave()
        {
            if (_leaving)
            {
                return false;
            }

            _leaving = true;
            return true;
        }

        /// <summary>
        /// Shows only the latest progress on the UI thread: the copy reports for every buffer, and the
        /// screen needs one update at a time.
        /// </summary>
        private sealed class LatestProgress(ImportPage page) : IProgress<ImportProgress>
        {
            private ImportProgress? _latest;
            private int _posted;

            public void Report(ImportProgress value)
            {
                Volatile.Write(ref _latest, value);

                if (Interlocked.Exchange(ref _posted, 1) == 0)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Volatile.Write(ref _posted, 0);

                        if (Volatile.Read(ref _latest) is { } latest)
                        {
                            page.Report(latest);
                        }
                    });
                }
            }
        }
    }
}
