using AnikiHelper.Services.VideoPlayer;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace AnikiHelper
{
    public partial class AnikiVideoLibraryManagerView : UserControl, INotifyPropertyChanged, IDisposable
    {
        private readonly IPlayniteAPI playniteApi;
        private readonly AnikiVideoPlayerService playerService;
        private readonly ILogger logger;
        private readonly ObservableCollection<AnikiVideoLibraryManagerItem> items = new ObservableCollection<AnikiVideoLibraryManagerItem>();
        private CancellationTokenSource loadCts;
        private CancellationTokenSource scanCts;
        private AnikiVideoLibraryManagerItem selectedItem;
        private bool disposed;
        private bool controlsReady;

        public event PropertyChangedEventHandler PropertyChanged;

        public ICollectionView FilteredItems { get; }

        public AnikiVideoLibraryManagerItem SelectedItem
        {
            get => selectedItem;
            set
            {
                if (ReferenceEquals(selectedItem, value))
                {
                    return;
                }

                selectedItem = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedItem)));
                UpdateDetailButtons();
            }
        }

        public AnikiVideoLibraryManagerView(IPlayniteAPI playniteApi, AnikiVideoPlayerService playerService, ILogger logger)
        {
            this.playniteApi = playniteApi;
            this.playerService = playerService;
            this.logger = logger ?? LogManager.GetLogger();

            // Build the XAML tree first. LibraryFilterCombo has an item selected in XAML,
            // so SelectionChanged can fire from inside InitializeComponent(). At that point
            // controls declared later in the XAML (LibraryCountText / EmptyLibraryText) do
            // not exist yet. Do not let those early routed events touch the view state.
            InitializeComponent();

            FilteredItems = CollectionViewSource.GetDefaultView(items);
            FilteredItems.Filter = FilterItem;
            DataContext = this;

            controlsReady = true;
            UpdateCountAndEmptyState();
            UpdateDetailButtons();

            Loaded += async (_, __) => await ReloadAsync().ConfigureAwait(true);
            Unloaded += (_, __) => CancelBackgroundWork();
        }

        private string Loc(string key, string fallback)
        {
            try
            {
                var value = Application.Current?.TryFindResource(key) as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private async Task ReloadAsync()
        {
            if (disposed || playerService == null)
            {
                return;
            }

            var old = loadCts;
            loadCts = new CancellationTokenSource();
            try { old?.Cancel(); old?.Dispose(); } catch { }
            var owner = loadCts;
            var selectedPath = SelectedItem?.FullPath ?? string.Empty;

            try
            {
                // Build cached cards off-thread, then refresh the filesystem silently.
                LibraryCountText.Text = Loc("VideoLibraryManager_Loading", "Loading library...");
                EmptyLibraryText.Visibility = Visibility.Collapsed;
                var cached = await Task.Run(
                    () => playerService.BuildCachedDesktopLibraryManagerItems(),
                    owner.Token).ConfigureAwait(true);
                if (cached != null && cached.Count > 0)
                {
                    await ReplaceItemsAsync(cached, selectedPath, owner.Token).ConfigureAwait(true);
                    LibraryCountText.Text = BuildCountText() + "  •  " + Loc("VideoLibraryManager_Refreshing", "Refreshing...");
                }

                var loaded = await playerService.BuildDesktopLibraryManagerItemsAsync(owner.Token).ConfigureAwait(true);
                if (owner.IsCancellationRequested || !ReferenceEquals(loadCts, owner))
                {
                    return;
                }

                await ReplaceItemsAsync(
                    loaded ?? Array.Empty<AnikiVideoLibraryManagerItem>(),
                    selectedPath,
                    owner.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[AnikiHelper][VideoCenter][LibraryManager] Failed to load desktop library manager.");
                if (items.Count == 0)
                {
                    LibraryCountText.Text = Loc("VideoLibraryManager_LoadError", "Unable to load the Video Center library.");
                }
                else
                {
                    UpdateCountAndEmptyState();
                }
            }
            finally
            {
                if (ReferenceEquals(loadCts, owner))
                {
                    loadCts = null;
                }
                owner.Dispose();
            }
        }

        private async Task ReplaceItemsAsync(
            IEnumerable<AnikiVideoLibraryManagerItem> source,
            string preferredPath,
            CancellationToken cancellationToken)
        {
            var snapshot = (source ?? Enumerable.Empty<AnikiVideoLibraryManagerItem>()).ToList();

            // A WrapPanel is not virtualized by WPF, so inserting hundreds of poster cards in a
            // single dispatcher turn forces all templates/images to be created at once. Add them
            // progressively and yield between batches. The user can keep using Playnite while the
            // grid fills instead of receiving one long UI stall at the end of a scan/refresh.
            items.Clear();
            const int batchSize = 24;
            for (var index = 0; index < snapshot.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(snapshot[index]);

                if ((index + 1) % batchSize == 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }

            FilteredItems.Refresh();
            UpdateCountAndEmptyState();

            var selection = !string.IsNullOrWhiteSpace(preferredPath)
                ? items.FirstOrDefault(x => string.Equals(x.FullPath, preferredPath, StringComparison.OrdinalIgnoreCase))
                : null;
            selection = selection ?? items.FirstOrDefault(x => !x.IsLibraryRoot) ?? items.FirstOrDefault();
            SelectedItem = selection;
            if (selection != null)
            {
                LibraryItemsList.SelectedItem = selection;
                LibraryItemsList.ScrollIntoView(selection);
            }
        }

        private string BuildCountText()
        {
            var count = FilteredItems?.Cast<object>().Count() ?? 0;
            var problems = items.Count(x => x != null && x.IsProblem);
            return string.Format(Loc("VideoLibraryManager_Count", "{0} media"), count) +
                   (problems > 0 ? "  •  " + problems.ToString() + " " + Loc("VideoLibraryManager_ProblemsShort", "problems") : string.Empty);
        }

        private bool FilterItem(object value)
        {
            var item = value as AnikiVideoLibraryManagerItem;
            if (item == null)
            {
                return false;
            }

            var query = (LibrarySearchBox?.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(query) &&
                (item.Name ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0)
            {
                return false;
            }

            var filter = (LibraryFilterCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
            switch (filter)
            {
                case "movies": return string.Equals(item.Kind, "movies", StringComparison.OrdinalIgnoreCase) && (!item.IsLibraryRoot || item.IsUnavailable);
                case "series": return string.Equals(item.Kind, "series", StringComparison.OrdinalIgnoreCase) && (!item.IsLibraryRoot || item.IsUnavailable);
                case "anime": return string.Equals(item.Kind, "anime", StringComparison.OrdinalIgnoreCase) && (!item.IsLibraryRoot || item.IsUnavailable);
                case "problems": return item.IsProblem;
                case "unwatched": return !item.IsLibraryRoot && item.IsAvailable && !item.IsWatched;
                case "missing": return item.IsMissingArtwork || item.IsMissingBackdrop || item.IsMissingHero;
                case "missingcover": return item.IsMissingArtwork;
                case "missinglandscape": return item.IsMissingBackdrop;
                case "missinghero": return item.IsMissingHero;
                case "unavailable": return item.IsUnavailable;
                default: return !item.IsLibraryRoot || item.IsUnavailable;
            }
        }

        private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!controlsReady)
            {
                return;
            }

            FilteredItems?.Refresh();
            UpdateCountAndEmptyState();
        }

        private void LibraryFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!controlsReady)
            {
                return;
            }

            FilteredItems?.Refresh();
            UpdateCountAndEmptyState();
        }

        private void LibraryItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedItem = LibraryItemsList.SelectedItem as AnikiVideoLibraryManagerItem;
        }

        private async void RefreshLibrary_Click(object sender, RoutedEventArgs e)
        {
            await ReloadAsync().ConfigureAwait(true);
        }

        private async void SearchArtwork_Click(object sender, RoutedEventArgs e)
        {
            await OpenArtworkPickerAsync("cover").ConfigureAwait(true);
        }

        private async void SearchLandscape_Click(object sender, RoutedEventArgs e)
        {
            await OpenArtworkPickerAsync("landscape").ConfigureAwait(true);
        }

        private async void SearchHero_Click(object sender, RoutedEventArgs e)
        {
            await OpenArtworkPickerAsync("hero").ConfigureAwait(true);
        }

        private async void SearchLogo_Click(object sender, RoutedEventArgs e)
        {
            await OpenArtworkPickerAsync("logo").ConfigureAwait(true);
        }

        private async Task OpenArtworkPickerAsync(string artworkTarget)
        {
            var item = SelectedItem;
            if (item == null || item.IsLibraryRoot || !item.IsAvailable)
            {
                return;
            }

            try
            {
                var picker = new AnikiVideoArtworkManagerView(playniteApi, playerService, item, logger, artworkTarget);
                var window = playniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true
                });
                var targetLabel = string.Equals(artworkTarget, "logo", StringComparison.OrdinalIgnoreCase)
                    ? Loc("VideoLibraryManager_Logo", "Logo")
                    : (string.Equals(artworkTarget, "hero", StringComparison.OrdinalIgnoreCase)
                        ? Loc("VideoLibraryManager_Hero", "Hero wallpaper")
                        : (string.Equals(artworkTarget, "landscape", StringComparison.OrdinalIgnoreCase)
                            ? Loc("VideoLibraryManager_Landscape", "Landscape")
                            : Loc("VideoLibraryManager_Cover", "Cover")));
                window.Title = Loc("VideoLibraryManager_ArtworkWindowTitle", "Choose artwork") + " - " + targetLabel + " - " + item.Name;
                window.Width = 1040;
                window.Height = 720;
                window.MinWidth = 820;
                window.MinHeight = 560;
                window.Content = picker;
                window.Owner = Window.GetWindow(this) ?? playniteApi.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.ShowDialog();
                picker.Dispose();

                if (picker.ArtworkWasApplied)
                {
                    // Manual override is now the highest-priority artwork source for the chosen slot.
                    FilteredItems.Refresh();
                    UpdateCountAndEmptyState();
                    await playerService.RefreshDesktopLibraryManagerItemAsync(item, CancellationToken.None).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[AnikiHelper][VideoCenter][LibraryManager] Desktop artwork picker failed.");
                playniteApi.Dialogs.ShowErrorMessage(
                    Loc("VideoLibraryManager_ArtworkError", "Artwork search could not be opened.") + Environment.NewLine + ex.Message,
                    "Aniki Helper");
            }
        }

        private void EditMetadata_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedItem;
            if (item == null || item.IsLibraryRoot || !item.IsAvailable) return;
            try
            {
                var editor = new AnikiVideoMetadataEditorView(playerService, item, logger);
                var window = playniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true
                });
                window.Title = Loc("VideoLibraryManager_EditMetadata", "Edit metadata") + " - " + item.Name;
                window.Width = 760;
                window.Height = 620;
                window.MinWidth = 620;
                window.MinHeight = 500;
                window.Content = editor;
                window.Owner = Window.GetWindow(this) ?? playniteApi.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.ShowDialog();
                if (editor.WasSaved)
                {
                    FilteredItems.Refresh();
                    UpdateCountAndEmptyState();
                }
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[AnikiHelper][VideoCenter][LibraryManager] Metadata editor failed.");
            }
        }

        private void ToggleWatched_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedItem;
            if (item == null || item.IsLibraryRoot || !item.IsAvailable) return;
            playerService?.SetDesktopItemWatched(item, !item.IsWatched);
            UpdateDetailButtons();
            FilteredItems?.Refresh();
            UpdateCountAndEmptyState();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedItem;
            if (item == null || string.IsNullOrWhiteSpace(item.FullPath))
            {
                return;
            }

            try
            {
                var target = item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath);
                if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
                {
                    playniteApi.Dialogs.ShowMessage(
                        Loc("VideoLibraryManager_PathUnavailable", "This location is currently unavailable."),
                        "Aniki Helper");
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][LibraryManager] Failed to open media folder.");
            }
        }

        private async void ScanArtwork_Click(object sender, RoutedEventArgs e)
        {
            if (scanCts != null || playerService == null)
            {
                return;
            }

            scanCts = new CancellationTokenSource();
            ScanArtworkButton.IsEnabled = false;
            CancelScanButton.Visibility = Visibility.Visible;
            CancelScanButton.IsEnabled = true;
            ScanProgressPanel.Visibility = Visibility.Visible;
            ScanProgressBar.Value = 0;
            ScanStatusText.Text = Loc("VideoArtworkScan_Preparing", "Scanning libraries...");

            var progress = new Progress<AnikiVideoArtworkScanProgress>(p =>
            {
                ScanProgressBar.Value = p.Percent;
                var current = string.IsNullOrWhiteSpace(p.CurrentItem) ? string.Empty : "  •  " + p.CurrentItem;
                ScanStatusText.Text = string.Format(
                    Loc("VideoArtworkScan_ProgressAssets", "Scanning {0}/{1}  •  Cover +{2}  •  Landscape +{3}  •  Wallpaper +{4}  •  Logo +{5}"),
                    p.ProcessedItems,
                    p.TotalItems,
                    p.CoversFound,
                    p.LandscapesFound,
                    p.HeroesFound,
                    p.LogosFound) + current;
            });

            try
            {
                var result = await playerService.ScanMissingLibraryArtworkAsync(progress, scanCts.Token).ConfigureAwait(true);
                ScanProgressBar.Value = 100;
                ScanStatusText.Text = "✓ " + Loc("VideoArtworkScan_Completed", "Artwork scan completed.") + "  " + string.Format(
                    Loc("VideoArtworkScan_ResultAssets", "{0} media  •  +{1} covers  •  +{2} landscapes  •  +{3} wallpapers  •  +{4} logos  •  {5} complete  •  {6} incomplete  •  {7} failed"),
                    result.TotalItems,
                    result.CoversFound,
                    result.LandscapesFound,
                    result.HeroesFound,
                    result.LogosFound,
                    result.CompleteItems,
                    result.IncompleteItems,
                    result.FailedItems);
                CancelScanButton.IsEnabled = false;
                CancelScanButton.Visibility = Visibility.Collapsed;

                // Let WPF paint the completed state before starting the post-scan library refresh.
                await Dispatcher.Yield(DispatcherPriority.Background);
                await ReloadAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                ScanStatusText.Text = Loc("VideoArtworkScan_Cancelled", "Artwork scan cancelled.");
                CancelScanButton.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[AnikiHelper][VideoCenter][LibraryManager] Artwork scan failed.");
                ScanStatusText.Text = Loc("VideoArtworkScan_Error", "Artwork scan could not be completed.");
                CancelScanButton.Visibility = Visibility.Collapsed;
            }
            finally
            {
                scanCts?.Dispose();
                scanCts = null;
                ScanArtworkButton.IsEnabled = true;
                CancelScanButton.IsEnabled = false;
            }
        }

        private void CancelScan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                scanCts?.Cancel();
                CancelScanButton.IsEnabled = false;
            }
            catch
            {
            }
        }

        private void UpdateCountAndEmptyState()
        {
            if (!controlsReady || FilteredItems == null || LibraryCountText == null || EmptyLibraryText == null)
            {
                return;
            }

            var count = FilteredItems.Cast<object>().Count();
            LibraryCountText.Text = BuildCountText();
            EmptyLibraryText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDetailButtons()
        {
            if (SearchArtworkButton == null || OpenFolderButton == null)
            {
                return;
            }

            var actionable = SelectedItem != null && !SelectedItem.IsLibraryRoot && SelectedItem.IsAvailable;
            SearchArtworkButton.IsEnabled = actionable;
            if (SearchLandscapeButton != null) SearchLandscapeButton.IsEnabled = actionable;
            if (SearchHeroButton != null) SearchHeroButton.IsEnabled = actionable;
            if (EditMetadataButton != null) EditMetadataButton.IsEnabled = actionable;
            if (ToggleWatchedButton != null)
            {
                ToggleWatchedButton.IsEnabled = actionable && (SelectedItem.IsVideo || SelectedItem.IsDirectory);
                ToggleWatchedButton.Content = SelectedItem?.IsWatched == true
                    ? Loc("VideoLibraryManager_MarkUnwatched", "Mark as unwatched")
                    : Loc("VideoLibraryManager_MarkWatched", "Mark as watched");
            }
            OpenFolderButton.IsEnabled = SelectedItem != null && !string.IsNullOrWhiteSpace(SelectedItem.FullPath);
        }

        private void CancelBackgroundWork()
        {
            try { loadCts?.Cancel(); } catch { }
            try { scanCts?.Cancel(); } catch { }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CancelBackgroundWork();
            try { loadCts?.Dispose(); } catch { }
            try { scanCts?.Dispose(); } catch { }
            loadCts = null;
            scanCts = null;
        }
    }
}
