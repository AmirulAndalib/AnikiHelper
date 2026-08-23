using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AnikiHelper.Services.GameList
{
    /// <summary>Experimental virtualized game list used to validate navigation and performance.</summary>
    internal sealed class AnikiExperimentalGameListControl : PluginUserControl
    {
        private const double FallbackItemWidth = 224.0;
        private const double FallbackItemHeight = 338.0;
        private const double ItemOuterMargin = 8.0;
        private const int FallbackColumns = 8;
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly AnikiHelper plugin;
        private readonly AnikiVirtualizingGameList list;
        private readonly TextBlock diagnosticText;

        private bool isLoaded;
        private bool syncingFromPlaynite;
        private FrameworkElement nativeGameList;
        private ContentControl hostControl;
        private string currentFingerprint = string.Empty;
        private readonly Dictionary<Guid, AnikiGameListEntry> entriesById = new Dictionary<Guid, AnikiGameListEntry>();
        private readonly HashSet<Guid> currentGameIds = new HashSet<Guid>();

        public AnikiExperimentalGameListControl(AnikiHelper plugin)
        {
            if (plugin == null)
            {
                throw new ArgumentNullException(nameof(plugin));
            }

            this.plugin = plugin;

            Focusable = false;
            IsTabStop = false;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;

            var root = new Grid
            {
                Background = Brushes.Transparent,
                ClipToBounds = false
            };

            list = new AnikiVirtualizingGameList
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Height = FallbackItemHeight + (ItemOuterMargin * 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                FocusVisualStyle = null,
                SelectionMode = SelectionMode.Single,
                IsSynchronizedWithCurrentItem = false
            };

            list.SelectionChanged += OnListSelectionChanged;
            list.PreviewKeyDown += OnListPreviewKeyDown;
            list.GotKeyboardFocus += OnListGotKeyboardFocus;

            diagnosticText = new TextBlock
            {
                Text = "ANIKI GAMELIST POC V0.2 PERF",
                FontSize = 13,
                Opacity = 0.42,
                Margin = new Thickness(18, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };
            diagnosticText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            root.Children.Add(list);
            root.Children.Add(diagnosticText);
            Content = root;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnIsVisibleChanged;
            SizeChanged += OnControlSizeChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (isLoaded)
            {
                return;
            }

            isLoaded = true;
            plugin.ExperimentalGameListSelectionChanged += OnPlayniteSelectionChanged;
            plugin.ExperimentalGameListRefreshRequested += OnRefreshRequested;

            ResolveHostControl();
            UpdateItemMetrics();
            RefreshItems(force: true);
            AttachNativeListFocusRedirect();

            Dispatcher.BeginInvoke(new Action(delegate
            {
                SyncSelectionFromPlaynite(GetCurrentSelectedGame(), scrollIntoView: true);
                RedirectNativeListFocusIfNeeded();
            }), DispatcherPriority.Loaded);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!isLoaded)
            {
                return;
            }

            isLoaded = false;
            plugin.ExperimentalGameListSelectionChanged -= OnPlayniteSelectionChanged;
            plugin.ExperimentalGameListRefreshRequested -= OnRefreshRequested;
            DetachNativeListFocusRedirect();
            hostControl = null;
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (Visibility != Visibility.Visible || !IsVisible)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(delegate
            {
                ResolveHostControl();
                UpdateItemMetrics();
                RefreshItems(force: false);
                SyncSelectionFromPlaynite(GetCurrentSelectedGame(), scrollIntoView: true);
                RedirectNativeListFocusIfNeeded();
            }), DispatcherPriority.Background);
        }

        private void OnListGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // GotKeyboardFocus is a routed event. In V0.1 it also fired while focus moved between
            // child containers, which meant rebuilding the filtered-list fingerprint on navigation.
            // Only do the refresh work when focus actually enters the list from outside.
            var oldFocus = e.OldFocus as DependencyObject;
            if (oldFocus != null && IsDescendantOrSelf(oldFocus, list))
            {
                return;
            }

            ResolveHostControl();
            UpdateItemMetrics();
            RefreshItems(force: false);
            SyncSelectionFromPlaynite(GetCurrentSelectedGame(), scrollIntoView: false);
        }

        private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!isLoaded || !IsVisible)
            {
                return;
            }

            UpdateItemMetrics();
        }

        private void OnRefreshRequested()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnRefreshRequested), DispatcherPriority.Background);
                return;
            }

            RefreshItems(force: true);
        }

        private void OnPlayniteSelectionChanged(Game game)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    OnPlayniteSelectionChanged(game);
                }), DispatcherPriority.DataBind);
                return;
            }

            if (game != null && !ContainsGame(game.Id))
            {
                RefreshItems(force: false);
            }

            SyncSelectionFromPlaynite(game, scrollIntoView: true);
        }

        private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var entry = list.SelectedItem as AnikiGameListEntry;
            if (entry == null)
            {
                return;
            }

            // Let ListBox's native keyboard navigation/virtualizing panel perform the normal
            // bring-into-view operation. V0.1 forced an additional ScrollIntoView here on every
            // selection, causing an avoidable synchronous layout pass.
            if (syncingFromPlaynite)
            {
                return;
            }

            try
            {
                var selected = GetCurrentSelectedGame();
                if (selected == null || selected.Id != entry.GameId)
                {
                    plugin.PlayniteApi.MainView.SelectGame(entry.GameId);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "[AnikiGameListPOC] Failed to synchronize selection to Playnite.");
            }
        }

        private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return && e.Key != Key.Space)
            {
                return;
            }

            var entry = list.SelectedItem as AnikiGameListEntry;
            if (entry == null)
            {
                return;
            }

            try
            {
                plugin.PlayniteApi.StartGame(entry.GameId);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "[AnikiGameListPOC] Failed to start selected game.");
            }
        }

        private void RefreshItems(bool force)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    RefreshItems(force);
                }), DispatcherPriority.DataBind);
                return;
            }

            try
            {
                var games = plugin.PlayniteApi.MainView.FilteredGames ?? new List<Game>();
                var fingerprint = BuildFingerprint(games);

                if (!force && string.Equals(fingerprint, currentFingerprint, StringComparison.Ordinal))
                {
                    return;
                }

                var sw = Stopwatch.StartNew();
                var selectedId = (list.SelectedItem as AnikiGameListEntry)?.GameId ?? Guid.Empty;
                var entries = new List<AnikiGameListEntry>(games.Count);
                var newEntriesById = new Dictionary<Guid, AnikiGameListEntry>(games.Count);
                var newGameIds = new HashSet<Guid>();

                foreach (var game in games)
                {
                    if (game == null)
                    {
                        continue;
                    }

                    var entry = new AnikiGameListEntry
                    {
                        GameId = game.Id,
                        Name = game.Name ?? string.Empty,
                        CoverPath = ResolveCoverPath(game.CoverImage)
                    };

                    entries.Add(entry);
                    newEntriesById[entry.GameId] = entry;
                    newGameIds.Add(entry.GameId);
                }

                syncingFromPlaynite = true;
                try
                {
                    list.ItemsSource = entries;
                }
                finally
                {
                    syncingFromPlaynite = false;
                }

                entriesById.Clear();
                foreach (var pair in newEntriesById)
                {
                    entriesById[pair.Key] = pair.Value;
                }

                currentGameIds.Clear();
                foreach (var gameId in newGameIds)
                {
                    currentGameIds.Add(gameId);
                }

                currentFingerprint = fingerprint;
                UpdateDiagnosticText(entries.Count);

                var selectedGame = GetCurrentSelectedGame();
                if (selectedGame != null)
                {
                    SyncSelectionFromPlaynite(selectedGame, scrollIntoView: true);
                }
                else if (selectedId != Guid.Empty)
                {
                    SelectEntryById(selectedId, scrollIntoView: false);
                }

                sw.Stop();
                global::AnikiHelper.AnikiLog.Debug(Logger, "[AnikiGameListPOC] Refreshed " + entries.Count + " games in " + sw.ElapsedMilliseconds + " ms.");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "[AnikiGameListPOC] Failed to refresh filtered game list.");
            }
        }

        private string ResolveCoverPath(string coverImage)
        {
            if (string.IsNullOrWhiteSpace(coverImage))
            {
                return null;
            }

            try
            {
                Uri uri;
                if (Uri.TryCreate(coverImage, UriKind.Absolute, out uri))
                {
                    if (uri.IsFile || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    {
                        return coverImage;
                    }
                }

                var fullPath = plugin.PlayniteApi.Database.GetFullFilePath(coverImage);
                return string.IsNullOrWhiteSpace(fullPath) ? null : fullPath;
            }
            catch
            {
                return null;
            }
        }

        private Game GetCurrentSelectedGame()
        {
            try
            {
                return plugin.PlayniteApi.MainView.SelectedGames?.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private bool ContainsGame(Guid gameId)
        {
            return currentGameIds.Contains(gameId);
        }

        private void SyncSelectionFromPlaynite(Game game, bool scrollIntoView)
        {
            if (game == null)
            {
                return;
            }

            SelectEntryById(game.Id, scrollIntoView);
        }

        private void SelectEntryById(Guid gameId, bool scrollIntoView)
        {
            AnikiGameListEntry entry;
            if (!entriesById.TryGetValue(gameId, out entry) || entry == null || ReferenceEquals(list.SelectedItem, entry))
            {
                if (entry != null && scrollIntoView)
                {
                    list.ScrollIntoView(entry);
                }

                return;
            }

            syncingFromPlaynite = true;
            try
            {
                list.SelectedItem = entry;
                if (scrollIntoView)
                {
                    list.ScrollIntoView(entry);
                }
            }
            finally
            {
                syncingFromPlaynite = false;
            }
        }

        private static string BuildFingerprint(IList<Game> games)
        {
            if (games == null || games.Count == 0)
            {
                return "0";
            }

            // Exact-enough ordered hash of the current filtered list. It only runs when focus
            // comes back to the POC (no polling timer), so even a large library stays cheap.
            unchecked
            {
                var hash = 17;
                for (var i = 0; i < games.Count; i++)
                {
                    hash = (hash * 31) + (games[i]?.Id.GetHashCode() ?? 0);
                }

                return games.Count + "|" + hash;
            }
        }

        private void ResolveHostControl()
        {
            if (hostControl != null)
            {
                return;
            }

            var current = VisualTreeHelper.GetParent(this);
            while (current != null)
            {
                var contentControl = current as ContentControl;
                if (contentControl != null &&
                    string.Equals(contentControl.Name, "AnikiHelper_ExperimentalGameList", StringComparison.Ordinal))
                {
                    hostControl = contentControl;
                    break;
                }

                current = VisualTreeHelper.GetParent(current);
            }
        }

        private void UpdateItemMetrics()
        {
            try
            {
                var availableWidth = ActualWidth;
                if ((double.IsNaN(availableWidth) || availableWidth < 100) && hostControl != null)
                {
                    availableWidth = hostControl.ActualWidth;
                }

                if (double.IsNaN(availableWidth) || availableWidth < 100)
                {
                    return;
                }

                var columns = ReadFullscreenColumns();
                var widthRatio = Math.Max(1, plugin.PlayniteApi.ApplicationSettings.GridItemWidthRatio);
                var heightRatio = Math.Max(1, plugin.PlayniteApi.ApplicationSettings.GridItemHeightRatio);

                var slotWidth = availableWidth / Math.Max(1, columns);
                var itemWidth = Math.Max(48.0, slotWidth - (ItemOuterMargin * 2));
                var itemHeight = Math.Max(48.0, itemWidth * heightRatio / (double)widthRatio);
                var decodeWidth = Math.Max(96, (int)Math.Ceiling(itemWidth * 1.15));
                var asyncImages = plugin.PlayniteApi.ApplicationSettings.AsyncImageLoading;

                list.UpdateMetrics(itemWidth, itemHeight, ItemOuterMargin, decodeWidth, asyncImages);
                UpdateDiagnosticText(list.Items.Count);
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(Logger, ex, "[AnikiGameListPOC] Failed to update item metrics.");
            }
        }

        private int ReadFullscreenColumns()
        {
            ResolveHostControl();
            if (hostControl != null && hostControl.Tag != null)
            {
                try
                {
                    var value = Convert.ToInt32(hostControl.Tag);
                    if (value > 0 && value <= 20)
                    {
                        return value;
                    }
                }
                catch
                {
                }
            }

            return FallbackColumns;
        }

        private void UpdateDiagnosticText(int gameCount)
        {
            var widthRatio = 0;
            var heightRatio = 0;
            var asyncImages = false;
            try
            {
                widthRatio = plugin.PlayniteApi.ApplicationSettings.GridItemWidthRatio;
                heightRatio = plugin.PlayniteApi.ApplicationSettings.GridItemHeightRatio;
                asyncImages = plugin.PlayniteApi.ApplicationSettings.AsyncImageLoading;
            }
            catch
            {
            }

            diagnosticText.Text = "ANIKI GAMELIST POC V0.2 PERF  •  " + gameCount +
                                  " games  •  cols " + ReadFullscreenColumns() +
                                  "  •  ratio " + widthRatio + ":" + heightRatio +
                                  "  •  async " + (asyncImages ? "ON" : "OFF");
        }

        private void AttachNativeListFocusRedirect()
        {
            if (nativeGameList != null)
            {
                return;
            }

            try
            {
                var window = Window.GetWindow(this);
                if (window == null)
                {
                    return;
                }

                nativeGameList = FindDescendantByName(window, "PART_ListGameItems");
                if (nativeGameList != null)
                {
                    nativeGameList.GotKeyboardFocus += OnNativeGameListGotKeyboardFocus;
                }
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(Logger, ex, "[AnikiGameListPOC] Native list focus redirect was not attached.");
            }
        }

        private void DetachNativeListFocusRedirect()
        {
            if (nativeGameList == null)
            {
                return;
            }

            nativeGameList.GotKeyboardFocus -= OnNativeGameListGotKeyboardFocus;
            nativeGameList = null;
        }

        private void OnNativeGameListGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!IsVisible)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(delegate
            {
                FocusSelectedItemOrList();
            }), DispatcherPriority.Input);
        }

        private void RedirectNativeListFocusIfNeeded()
        {
            AttachNativeListFocusRedirect();

            if (!IsVisible)
            {
                return;
            }

            var focused = Keyboard.FocusedElement as FrameworkElement;
            if (focused != null && nativeGameList != null && IsDescendantOrSelf(focused, nativeGameList))
            {
                FocusSelectedItemOrList();
            }
        }

        private void FocusSelectedItemOrList()
        {
            if (!IsVisible)
            {
                return;
            }

            if (!list.Focus())
            {
                Keyboard.Focus(list);
            }
        }

        private static FrameworkElement FindDescendantByName(DependencyObject parent, string name)
        {
            if (parent == null)
            {
                return null;
            }

            var frameworkElement = parent as FrameworkElement;
            if (frameworkElement != null && string.Equals(frameworkElement.Name, name, StringComparison.Ordinal))
            {
                return frameworkElement;
            }

            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(parent);
            }
            catch
            {
                return null;
            }

            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var found = FindDescendantByName(child, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static bool IsDescendantOrSelf(DependencyObject child, DependencyObject ancestor)
        {
            var current = child;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }

                try
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private sealed class AnikiGameListEntry
        {
            public Guid GameId { get; set; }
            public string Name { get; set; }
            public string CoverPath { get; set; }
        }

        private sealed class AnikiVirtualizingGameList : ListBox
        {
            private static readonly ItemsPanelTemplate VirtualizedHorizontalPanel = CreateItemsPanelTemplate();

            private double itemWidth = FallbackItemWidth;
            private double itemHeight = FallbackItemHeight;
            private double itemMargin = ItemOuterMargin;
            private int decodeWidth = 260;
            private bool asyncImages = true;

            public AnikiVirtualizingGameList()
            {
                ItemsPanel = VirtualizedHorizontalPanel;

                ScrollViewer.SetCanContentScroll(this, true);
                ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Hidden);
                ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
                VirtualizingPanel.SetIsVirtualizing(this, true);
                VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
                KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Continue);
                KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
            }

            public void UpdateMetrics(double width, double height, double margin, int targetDecodeWidth, bool useAsyncImages)
            {
                if (Math.Abs(itemWidth - width) < 0.25 &&
                    Math.Abs(itemHeight - height) < 0.25 &&
                    Math.Abs(itemMargin - margin) < 0.25 &&
                    decodeWidth == targetDecodeWidth &&
                    asyncImages == useAsyncImages)
                {
                    return;
                }

                itemWidth = width;
                itemHeight = height;
                itemMargin = margin;
                decodeWidth = targetDecodeWidth;
                asyncImages = useAsyncImages;
                Height = itemHeight + (itemMargin * 2);

                // Only realized containers exist when virtualization is active. Updating metrics
                // therefore touches the small visible/recycled set, not the full library.
                for (var i = 0; i < Items.Count; i++)
                {
                    var container = ItemContainerGenerator.ContainerFromIndex(i) as AnikiGameListItem;
                    if (container != null)
                    {
                        container.Prepare(Items[i] as AnikiGameListEntry, itemWidth, itemHeight, itemMargin, decodeWidth, asyncImages);
                    }
                }
            }

            protected override DependencyObject GetContainerForItemOverride()
            {
                return new AnikiGameListItem();
            }

            protected override bool IsItemItsOwnContainerOverride(object item)
            {
                return item is AnikiGameListItem;
            }

            protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
            {
                base.PrepareContainerForItemOverride(element, item);
                var container = element as AnikiGameListItem;
                if (container != null)
                {
                    container.Prepare(item as AnikiGameListEntry, itemWidth, itemHeight, itemMargin, decodeWidth, asyncImages);
                }
            }

            protected override void ClearContainerForItemOverride(DependencyObject element, object item)
            {
                var container = element as AnikiGameListItem;
                if (container != null)
                {
                    container.ClearCover();
                }

                base.ClearContainerForItemOverride(element, item);
            }

            private static ItemsPanelTemplate CreateItemsPanelTemplate()
            {
                var panelFactory = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
                panelFactory.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Horizontal);

                return new ItemsPanelTemplate
                {
                    VisualTree = panelFactory
                };
            }
        }

        private sealed class AnikiGameListItem : ListBoxItem
        {
            private static readonly ControlTemplate ItemTemplate = CreateTemplate();

            private Image coverImage;
            private AnikiGameListEntry currentEntry;
            private int coverGeneration;
            private int currentDecodeWidth = 260;
            private bool currentAsyncImages = true;

            public AnikiGameListItem()
            {
                Padding = new Thickness(0);
                Background = Brushes.Black;
                BorderBrush = Brushes.Transparent;
                BorderThickness = new Thickness(2);
                HorizontalContentAlignment = HorizontalAlignment.Stretch;
                VerticalContentAlignment = VerticalAlignment.Stretch;
                FocusVisualStyle = null;
                Template = ItemTemplate;
                ClipToBounds = false;
                SnapsToDevicePixels = true;

                Selected += OnSelected;
                Unselected += OnUnselected;
            }

            public override void OnApplyTemplate()
            {
                base.OnApplyTemplate();
                coverImage = GetTemplateChild("PART_Cover") as Image;
                if (coverImage != null)
                {
                    RenderOptions.SetBitmapScalingMode(coverImage, BitmapScalingMode.LowQuality);
                    LoadCurrentCover();
                }
            }

            public void Prepare(AnikiGameListEntry entry, double width, double height, double margin, int decodeWidth, bool asyncImages)
            {
                Width = width;
                Height = height;
                Margin = new Thickness(margin);
                currentEntry = entry;
                currentDecodeWidth = Math.Max(1, decodeWidth);
                currentAsyncImages = asyncImages;

                if (coverImage == null)
                {
                    // OnApplyTemplate performs the initial load. Avoid starting a second decode
                    // immediately after the template is created.
                    ApplyTemplate();
                }
                else
                {
                    LoadCurrentCover();
                }
            }

            public void ClearCover()
            {
                Interlocked.Increment(ref coverGeneration);
                currentEntry = null;
                if (coverImage != null)
                {
                    coverImage.Source = null;
                }
            }

            private void LoadCurrentCover()
            {
                if (coverImage == null)
                {
                    return;
                }

                var entry = currentEntry;
                var path = entry == null ? null : entry.CoverPath;
                var generation = Interlocked.Increment(ref coverGeneration);
                coverImage.Source = null;

                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                BitmapSource cached;
                if (AnikiCoverBitmapCache.TryGet(path, currentDecodeWidth, out cached))
                {
                    coverImage.Source = cached;
                    return;
                }

                if (!currentAsyncImages)
                {
                    var syncBitmap = AnikiCoverBitmapCache.LoadAndCache(path, currentDecodeWidth);
                    if (generation == coverGeneration && syncBitmap != null)
                    {
                        coverImage.Source = syncBitmap;
                    }
                    else if (generation == coverGeneration)
                    {
                        SetFallbackSource(path);
                    }

                    return;
                }

                AnikiCoverBitmapCache.LoadAsync(path, currentDecodeWidth).ContinueWith(task =>
                {
                    var bitmap = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (generation != coverGeneration || currentEntry == null ||
                            !string.Equals(currentEntry.CoverPath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        if (bitmap != null)
                        {
                            coverImage.Source = bitmap;
                        }
                        else
                        {
                            // Safety fallback: if a decoder/provider rejects background loading,
                            // keep the POC usable and let WPF load that one image normally.
                            SetFallbackSource(path);
                        }
                    }), DispatcherPriority.Render);
                });
            }

            private void SetFallbackSource(string path)
            {
                try
                {
                    Uri uri;
                    if (!Uri.TryCreate(path, UriKind.Absolute, out uri))
                    {
                        return;
                    }

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = Math.Max(1, currentDecodeWidth);
                    bitmap.UriSource = uri;
                    bitmap.EndInit();
                    if (bitmap.CanFreeze)
                    {
                        bitmap.Freeze();
                    }

                    coverImage.Source = bitmap;
                }
                catch
                {
                    coverImage.Source = null;
                }
            }

            private void OnSelected(object sender, RoutedEventArgs e)
            {
                BorderThickness = new Thickness(4);
                SetResourceReference(BorderBrushProperty, "FocusGameBorderBrush");
                Panel.SetZIndex(this, 10);
            }

            private void OnUnselected(object sender, RoutedEventArgs e)
            {
                BorderThickness = new Thickness(2);
                BorderBrush = Brushes.Transparent;
                Panel.SetZIndex(this, 0);
            }

            private static ControlTemplate CreateTemplate()
            {
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                border.SetBinding(Border.BackgroundProperty, new Binding("Background")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                });
                border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                });
                border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                });

                var image = new FrameworkElementFactory(typeof(Image), "PART_Cover");
                image.SetValue(Image.StretchProperty, Stretch.UniformToFill);
                image.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
                image.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
                image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.LowQuality);
                border.AppendChild(image);

                return new ControlTemplate(typeof(ListBoxItem))
                {
                    VisualTree = border
                };
            }
        }

        private static class AnikiCoverBitmapCache
        {
            private const int MaxEntries = 48;
            private static readonly object SyncRoot = new object();
            private static readonly Dictionary<string, CacheEntry> Entries =
                new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            private static readonly SemaphoreSlim DecodeGate = new SemaphoreSlim(2, 2);

            public static bool TryGet(string source, int width, out BitmapSource bitmap)
            {
                bitmap = null;
                var key = BuildKey(source, width);
                lock (SyncRoot)
                {
                    CacheEntry entry;
                    if (!Entries.TryGetValue(key, out entry))
                    {
                        return false;
                    }

                    entry.LastAccessUtc = DateTime.UtcNow;
                    bitmap = entry.Bitmap;
                    return bitmap != null;
                }
            }

            public static Task<BitmapSource> LoadAsync(string source, int width)
            {
                BitmapSource cached;
                if (TryGet(source, width, out cached))
                {
                    return Task.FromResult(cached);
                }

                return Task.Run(delegate
                {
                    DecodeGate.Wait();
                    try
                    {
                        return LoadAndCache(source, width);
                    }
                    finally
                    {
                        DecodeGate.Release();
                    }
                });
            }

            public static BitmapSource LoadAndCache(string source, int width)
            {
                BitmapSource cached;
                if (TryGet(source, width, out cached))
                {
                    return cached;
                }

                var bitmap = LoadBitmap(source, width);
                if (bitmap != null)
                {
                    Add(source, width, bitmap);
                }

                return bitmap;
            }

            private static BitmapSource LoadBitmap(string source, int decodePixelWidth)
            {
                try
                {
                    Uri uri;
                    if (!Uri.TryCreate(source, UriKind.Absolute, out uri))
                    {
                        return null;
                    }

                    // Remote URLs are left to the safe UI fallback. The database covers used in
                    // Playnite are normally resolved to local files before reaching this point.
                    if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    {
                        return null;
                    }

                    var path = uri.IsFile ? uri.LocalPath : source;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        return null;
                    }

                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                        bitmap.DecodePixelWidth = Math.Max(1, decodePixelWidth);
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
                catch
                {
                    return null;
                }
            }

            private static void Add(string source, int width, BitmapSource bitmap)
            {
                var key = BuildKey(source, width);
                lock (SyncRoot)
                {
                    Entries[key] = new CacheEntry
                    {
                        Bitmap = bitmap,
                        LastAccessUtc = DateTime.UtcNow
                    };

                    if (Entries.Count <= MaxEntries)
                    {
                        return;
                    }

                    var removeCount = Entries.Count - MaxEntries;
                    var oldKeys = Entries.OrderBy(x => x.Value.LastAccessUtc)
                        .Take(removeCount)
                        .Select(x => x.Key)
                        .ToList();

                    foreach (var oldKey in oldKeys)
                    {
                        Entries.Remove(oldKey);
                    }
                }
            }

            private static string BuildKey(string source, int width)
            {
                return width + "|" + source;
            }

            private sealed class CacheEntry
            {
                public BitmapSource Bitmap { get; set; }
                public DateTime LastAccessUtc { get; set; }
            }
        }

    }
}
