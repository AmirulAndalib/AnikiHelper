using AnikiHelper.Services.VideoPlayer;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AnikiHelper
{
    public partial class AnikiVideoIntroEndingManagerView : UserControl, INotifyPropertyChanged, IDisposable
    {
        private readonly IPlayniteAPI playniteApi;
        private readonly AnikiVideoPlayerService playerService;
        private readonly ILogger logger;
        private readonly ObservableCollection<AnikiVideoIntroEndingSeriesItem> items =
            new ObservableCollection<AnikiVideoIntroEndingSeriesItem>();
        private CancellationTokenSource loadCts;
        private CancellationTokenSource analysisCts;
        private AnikiVideoIntroEndingSeriesItem selectedItem;
        private bool loadedOnce;
        private bool isBusy;

        public event PropertyChangedEventHandler PropertyChanged;
        public ICollectionView FilteredItems { get; }

        public AnikiVideoIntroEndingSeriesItem SelectedItem
        {
            get => selectedItem;
            set
            {
                if (ReferenceEquals(selectedItem, value)) return;
                selectedItem = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedItem)));
                UpdateButtons();
            }
        }

        public AnikiVideoIntroEndingManagerView(
            IPlayniteAPI playniteApi,
            AnikiVideoPlayerService playerService,
            ILogger logger)
        {
            InitializeComponent();
            this.playniteApi = playniteApi;
            this.playerService = playerService;
            this.logger = logger ?? LogManager.GetLogger();
            FilteredItems = CollectionViewSource.GetDefaultView(items);
            FilteredItems.Filter = FilterItem;
            DataContext = this;
            Loaded += View_Loaded;
        }

        private async void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (loadedOnce) return;
            loadedOnce = true;
            await ReloadAsync().ConfigureAwait(true);
        }

        private async Task ReloadAsync()
        {
            loadCts?.Cancel();
            loadCts?.Dispose();
            var owner = new CancellationTokenSource();
            loadCts = owner;

            try
            {
                SetBusy(true, Loc("VideoIntroEnding_Loading", "Loading movies, TV series and anime..."), false);
                var loaded = await playerService.BuildIntroEndingManagerItemsAsync(owner.Token).ConfigureAwait(true);
                if (owner.IsCancellationRequested || !ReferenceEquals(loadCts, owner)) return;

                items.Clear();
                foreach (var item in loaded ?? Array.Empty<AnikiVideoIntroEndingSeriesItem>())
                {
                    items.Add(item);
                }
                FilteredItems.Refresh();
                SelectedItem = items.FirstOrDefault();
                if (SelectedItem != null) SeriesList.SelectedItem = SelectedItem;
                UpdateCount();

                if (playerService.IsIntroEndingDetectionAvailable)
                {
                    ProgressText.Text = Loc("VideoIntroEnding_Ready", "Ready. Markers are fetched automatically during library scans; use this window to refresh them manually.");
                }
                else
                {
                    ProgressText.Text = Loc("VideoIntroEnding_FfmpegMissing", "Online marker lookup is unavailable.");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[AnikiHelper][VideoCenter][IntroEnding] Failed to load detection manager.");
                playniteApi?.Dialogs?.ShowErrorMessage(
                    Loc("VideoIntroEnding_LoadError", "Unable to load the intro and ending detection manager.") + Environment.NewLine + ex.Message,
                    "Aniki Helper");
            }
            finally
            {
                if (ReferenceEquals(loadCts, owner)) loadCts = null;
                owner.Dispose();
                SetBusy(false, ProgressText.Text, false);
            }
        }

        private bool FilterItem(object value)
        {
            var item = value as AnikiVideoIntroEndingSeriesItem;
            if (item == null) return false;

            var query = (SearchBox?.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(query) &&
                (item.Name ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0)
            {
                return false;
            }

            var filter = (FilterCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
            switch (filter)
            {
                case "not":
                    return item.AnalyzedCount < item.EpisodeCount;
                case "detected":
                    return item.EpisodeCount > 0 &&
                           item.AnalyzedCount == item.EpisodeCount &&
                           item.IntroCount > 0 && item.EndingCount > 0;
                case "partial":
                    return item.AnalyzedCount > 0 &&
                           !(item.AnalyzedCount == item.EpisodeCount && item.IntroCount > 0 && item.EndingCount > 0);
                default:
                    return true;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilteredItems?.Refresh();
            UpdateCount();
        }

        private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FilteredItems?.Refresh();
            UpdateCount();
        }

        private void SeriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedItem = SeriesList.SelectedItem as AnikiVideoIntroEndingSeriesItem;
        }

        private async void AnalyzeSelected_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem == null) return;
            await RunAnalysisAsync(new[] { SelectedItem }, false).ConfigureAwait(true);
        }

        private async void ReanalyzeSelected_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem == null) return;
            await RunAnalysisAsync(new[] { SelectedItem }, true).ConfigureAwait(true);
        }

        private async void AnalyzeAll_Click(object sender, RoutedEventArgs e)
        {
            var pending = items.Where(x => x != null &&
                (x.AnalyzedCount < x.EpisodeCount || x.IntroCount < x.EpisodeCount || x.EndingCount < x.EpisodeCount)).ToList();
            if (pending.Count == 0)
            {
                ProgressText.Text = Loc("VideoIntroEnding_AllCurrent", "All listed items already have current marker lookup results.");
                return;
            }
            await RunAnalysisAsync(pending, true).ConfigureAwait(true);
        }

        private async Task RunAnalysisAsync(IEnumerable<AnikiVideoIntroEndingSeriesItem> source, bool force)
        {
            if (isBusy || playerService == null) return;
            if (!playerService.IsIntroEndingDetectionAvailable)
            {
                playniteApi?.Dialogs?.ShowErrorMessage(
                    Loc("VideoIntroEnding_FfmpegMissingHelp", "Online marker lookup is currently unavailable."),
                    "Aniki Helper");
                return;
            }

            analysisCts?.Cancel();
            analysisCts?.Dispose();
            var owner = new CancellationTokenSource();
            analysisCts = owner;
            var seriesItems = (source ?? Enumerable.Empty<AnikiVideoIntroEndingSeriesItem>()).Where(x => x != null).ToList();
            if (seriesItems.Count == 0)
            {
                if (ReferenceEquals(analysisCts, owner)) analysisCts = null;
                owner.Dispose();
                return;
            }

            try
            {
                SetBusy(true, Loc("VideoIntroEnding_Preparing", "Preparing marker lookup..."), true);
                foreach (var series in seriesItems)
                {
                    owner.Token.ThrowIfCancellationRequested();
                    var progress = new Progress<AnikiVideoIntroEndingProgress>(p =>
                    {
                        AnalysisProgress.Maximum = Math.Max(1, p?.Total ?? 1);
                        AnalysisProgress.Value = Math.Max(0, Math.Min(AnalysisProgress.Maximum, p?.Current ?? 0));
                        ProgressText.Text = p?.Message ?? string.Empty;
                    });

                    await playerService.AnalyzeIntroEndingSeriesAsync(series, force, progress, owner.Token).ConfigureAwait(true);
                    playerService.RefreshIntroEndingSeriesStatus(series);
                    FilteredItems.Refresh();
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedItem)));
                    UpdateCount();
                }

                ProgressText.Text = Loc("VideoIntroEnding_Completed", "Intro and ending marker refresh completed.");
            }
            catch (OperationCanceledException)
            {
                ProgressText.Text = Loc("VideoIntroEnding_Cancelled", "Marker refresh cancelled.");
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[AnikiHelper][VideoCenter][IntroEnding] Marker refresh failed.");
                ProgressText.Text = Loc("VideoIntroEnding_Failed", "Marker refresh failed.");
                playniteApi?.Dialogs?.ShowErrorMessage(
                    Loc("VideoIntroEnding_Failed", "Marker refresh failed.") + Environment.NewLine + ex.Message,
                    "Aniki Helper");
            }
            finally
            {
                if (ReferenceEquals(analysisCts, owner)) analysisCts = null;
                owner.Dispose();
                SetBusy(false, ProgressText.Text, false);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            analysisCts?.Cancel();
        }

        private void SetBusy(bool busy, string message, bool showProgress)
        {
            isBusy = busy;
            ProgressText.Text = message ?? string.Empty;
            AnalysisProgress.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
            if (showProgress)
            {
                AnalysisProgress.Minimum = 0;
                AnalysisProgress.Maximum = 1;
                AnalysisProgress.Value = 0;
            }
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            if (AnalyzeSelectedButton == null) return;
            var available = playerService?.IsIntroEndingDetectionAvailable == true;
            AnalyzeSelectedButton.IsEnabled = !isBusy && available && SelectedItem != null;
            ReanalyzeSelectedButton.IsEnabled = !isBusy && available && SelectedItem != null;
            AnalyzeAllButton.IsEnabled = !isBusy && available && items.Count > 0;
            CancelButton.IsEnabled = isBusy && analysisCts != null;
        }

        private void UpdateCount()
        {
            if (CountText == null || FilteredItems == null) return;
            var visible = FilteredItems.Cast<object>().Count();
            CountText.Text = string.Format(Loc("VideoIntroEnding_Count", "{0} media items"), visible);
        }

        private static string Loc(string key, string fallback)
        {
            try
            {
                var value = Application.Current?.TryFindResource(key) as string;
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        public void Dispose()
        {
            try { loadCts?.Cancel(); } catch { }
            try { analysisCts?.Cancel(); } catch { }
            try { loadCts?.Dispose(); } catch { }
            try { analysisCts?.Dispose(); } catch { }
            loadCts = null;
            analysisCts = null;
        }
    }
}
