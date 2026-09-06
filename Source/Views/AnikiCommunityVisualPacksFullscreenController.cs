using AnikiHelper.Services.CommunityPacks;
using AnikiHelper.Services.VisualPacks;
using AnikiHelperFullscreen.Views;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

namespace AnikiHelper
{
    public sealed class AnikiCommunityVisualPacksFullscreenController : IDisposable
    {
        private readonly global::AnikiHelper.AnikiHelper plugin;
        private readonly IPlayniteAPI api;
        private readonly ILogger logger;
        private CommunityPackService service;
        private readonly string pluginUserDataPath;
        private string packType = "visual";
        private readonly CommunityVisualPacksViewModel viewModel = new CommunityVisualPacksViewModel();
        private CancellationTokenSource refreshCts;
        private bool loadedOnce;
        private bool disposed;

        public UserControl Control { get; }

        public AnikiCommunityVisualPacksFullscreenController(
            global::AnikiHelper.AnikiHelper plugin,
            IPlayniteAPI api,
            string pluginUserDataPath,
            ILogger logger)
        {
            this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.logger = logger;
            this.pluginUserDataPath = pluginUserDataPath ?? string.Empty;
            service = new CommunityPackService(plugin, api, this.pluginUserDataPath, logger, packType);

            Control = LoadView();
            UpdateHeaderTexts();
            Control.DataContext = viewModel;
            Control.Loaded += OnLoaded;
            Control.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClick), true);
            Control.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        }

        private static UserControl LoadView()
        {
            var pluginAssemblyName = Assembly.GetExecutingAssembly().GetName().Name;
            var resourceUri = new Uri(
                $"pack://application:,,,/{pluginAssemblyName};component/Views/AnikiCommunityVisualPacksFullscreenView.xaml",
                UriKind.Absolute);

            var resource = Application.GetResourceStream(resourceUri);
            if (resource == null || resource.Stream == null)
            {
                throw new InvalidOperationException("AnikiCommunityVisualPacksFullscreenView.xaml resource not found.");
            }

            using (var stream = resource.Stream)
            {
                return (UserControl)XamlReader.Load(stream);
            }
        }

        public void PrepareForOpen(string requestedPackType)
        {
            if (disposed)
            {
                return;
            }

            var normalized = CommunityPackService.NormalizePackType(requestedPackType);
            if (!string.Equals(packType, normalized, StringComparison.OrdinalIgnoreCase))
            {
                try { service?.Dispose(); } catch { }
                packType = normalized;
                service = new CommunityPackService(plugin, api, pluginUserDataPath, logger, packType);
            }

            UpdateHeaderTexts();
            if (loadedOnce)
            {
                _ = RefreshAsync();
            }
        }

        public void PrepareForOpen()
        {
            PrepareForOpen(packType);
        }

        private void UpdateHeaderTexts()
        {
            var typeName = GetLocalizedPackTypeName(packType);
            viewModel.WindowTitle = string.Format(Loc("CommunityPack_WindowTitleFormat", "Community {0}"), typeName);
            viewModel.WindowDescription = string.Format(
                Loc("CommunityPack_WindowDescriptionFormat", "Browse, install and update Community {0} shared by Aniki ReMake users."),
                typeName);
            viewModel.EmptyText = string.Format(
                Loc("CommunityPack_EmptyFormat", "No Community {0} are currently available."),
                typeName);
        }

        private static string GetLocalizedPackTypeName(string type)
        {
            switch (CommunityPackService.NormalizePackType(type))
            {
                case "visual": return Loc("CommunityPack_TypeVisual", "Visual Packs");
                case "color": return Loc("CommunityPack_TypeColor", "Color Packs");
                case "login": return Loc("CommunityPack_TypeLogin", "Login Packs");
                case "sound": return Loc("CommunityPack_TypeSound", "Sound Packs");
                case "complete": return Loc("CommunityPack_TypeComplete", "Complete Packs");
                default: return Loc("CommunityPack_TypeAll", "Packs");
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (disposed || loadedOnce)
            {
                return;
            }

            loadedOnce = true;
            await RefreshAsync();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (disposed || e == null || e.IsRepeat)
            {
                return;
            }

            var currentButton = FindParentButton(e.OriginalSource as DependencyObject);
            if (currentButton == null)
            {
                return;
            }

            var action = (currentButton as FrameworkElement)?.Tag as string;

            // Header -> first card row.
            if (e.Key == Key.Down &&
                (string.Equals(action, "Back", StringComparison.Ordinal) ||
                 string.Equals(action, "Refresh", StringComparison.Ordinal)))
            {
                var target = FindNearestTopRowCardButton(currentButton);
                if (target == null)
                {
                    return;
                }

                FocusButton(target);
                e.Handled = true;
                return;
            }

            if (!IsCardAction(action))
            {
                return;
            }

            // First card row -> header.
            if (e.Key == Key.Up && IsTopRowCardButton(currentButton))
            {
                var headerTarget = FindNearestHeaderButton(currentButton);
                if (headerTarget == null)
                {
                    return;
                }

                FocusButton(headerTarget);
                e.Handled = true;
                return;
            }

            // Handle vertical card navigation ourselves so the ScrollViewer always
            // reveals the whole destination card instead of only its focused button.
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                var target = FindAdjacentCardButton(currentButton, e.Key == Key.Up ? -1 : 1);
                if (target == null)
                {
                    return;
                }

                FocusButton(target);
                e.Handled = true;
            }
        }

        private static bool IsCardAction(string action)
        {
            return string.Equals(action, "InstallOrUpdate", StringComparison.Ordinal) ||
                   string.Equals(action, "Uninstall", StringComparison.Ordinal);
        }

        private void FocusButton(ButtonBase button)
        {
            if (button == null)
            {
                return;
            }

            button.Focus();
            Keyboard.Focus(button);

            var card = FindCardRoot(button);
            if (card == null)
            {
                button.BringIntoView();
                return;
            }

            EnsureCardFullyVisible(card);

            // Focus/layout changes can slightly alter the final geometry. Run one
            // second visibility pass after WPF has processed the focus change.
            try
            {
                button.Dispatcher.BeginInvoke(new Action(() => EnsureCardFullyVisible(card)));
            }
            catch
            {
            }
        }

        private void QueueRestorePackFocus(string packId, string preferredAction = null)
        {
            if (disposed || string.IsNullOrWhiteSpace(packId) || Control == null)
            {
                return;
            }

            try
            {
                // The action buttons can be recreated/hidden by bindings after an
                // install/update/uninstall. Wait for the visual tree to settle before
                // resolving the new button for the same pack.
                Control.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        Control.Dispatcher.BeginInvoke(new Action(() => RestorePackFocus(packId, preferredAction)));
                    }
                    catch
                    {
                        RestorePackFocus(packId, preferredAction);
                    }
                }));
            }
            catch
            {
                RestorePackFocus(packId, preferredAction);
            }
        }

        private void RestorePackFocus(string packId, string preferredAction)
        {
            if (disposed || string.IsNullOrWhiteSpace(packId))
            {
                return;
            }

            var candidates = FindVisualChildren<ButtonBase>(Control)
                .Where(button => button != null && button.IsVisible && button.IsEnabled)
                .Where(button =>
                {
                    var item = button.DataContext as CommunityVisualPackViewItem;
                    var action = (button as FrameworkElement)?.Tag as string;
                    return item != null &&
                           string.Equals(item.Id, packId, StringComparison.OrdinalIgnoreCase) &&
                           IsCardAction(action);
                })
                .ToList();

            if (candidates.Count == 0)
            {
                return;
            }

            ButtonBase target = null;
            if (!string.IsNullOrWhiteSpace(preferredAction))
            {
                target = candidates.FirstOrDefault(button =>
                    string.Equals((button as FrameworkElement)?.Tag as string, preferredAction, StringComparison.Ordinal));
            }

            target = target ?? candidates[0];
            FocusButton(target);
        }

        private bool IsTopRowCardButton(ButtonBase button)
        {
            var card = FindCardRoot(button);
            var cardPoint = GetPosition(card);
            if (card == null || !cardPoint.HasValue)
            {
                return false;
            }

            var cards = GetCardRootsWithPositions();
            if (cards.Count == 0)
            {
                return false;
            }

            var topY = cards.Min(x => x.Point.Y);
            return Math.Abs(cardPoint.Value.Y - topY) <= 24.0;
        }

        private ButtonBase FindNearestHeaderButton(ButtonBase sourceButton)
        {
            var candidates = FindVisualChildren<ButtonBase>(Control)
                .Where(button => button != null && button.IsVisible && button.IsEnabled)
                .Where(button =>
                {
                    var tag = (button as FrameworkElement)?.Tag as string;
                    return string.Equals(tag, "Back", StringComparison.Ordinal) ||
                           string.Equals(tag, "Refresh", StringComparison.Ordinal);
                })
                .Select(button => new
                {
                    Button = button,
                    Point = GetPosition(button)
                })
                .Where(x => x.Point.HasValue)
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            var sourcePoint = GetPosition(sourceButton);
            if (!sourcePoint.HasValue)
            {
                return candidates[0].Button;
            }

            var sourceCenterX = sourcePoint.Value.X + (sourceButton.ActualWidth / 2.0);
            return candidates
                .OrderBy(x => Math.Abs((x.Point.Value.X + (x.Button.ActualWidth / 2.0)) - sourceCenterX))
                .First()
                .Button;
        }

        private ButtonBase FindNearestTopRowCardButton(ButtonBase sourceButton)
        {
            var cards = GetCardRootsWithPositions();
            if (cards.Count == 0)
            {
                return null;
            }

            var topY = cards.Min(x => x.Point.Y);
            var topRow = cards
                .Where(x => Math.Abs(x.Point.Y - topY) <= 24.0)
                .ToList();

            var sourcePoint = GetPosition(sourceButton);
            var sourceCenterX = sourcePoint.HasValue
                ? sourcePoint.Value.X + (sourceButton.ActualWidth / 2.0)
                : topRow[0].Point.X + (topRow[0].Card.ActualWidth / 2.0);

            var targetCard = topRow
                .OrderBy(x => Math.Abs((x.Point.X + (x.Card.ActualWidth / 2.0)) - sourceCenterX))
                .First()
                .Card;

            return FindNearestActionButtonInCard(targetCard, sourceCenterX);
        }

        private ButtonBase FindAdjacentCardButton(ButtonBase sourceButton, int direction)
        {
            var sourceCard = FindCardRoot(sourceButton);
            var sourceCardPoint = GetPosition(sourceCard);
            if (sourceCard == null || !sourceCardPoint.HasValue)
            {
                return null;
            }

            var cards = GetCardRootsWithPositions()
                .Where(x => !ReferenceEquals(x.Card, sourceCard))
                .ToList();

            if (cards.Count == 0)
            {
                return null;
            }

            const double rowTolerance = 24.0;
            List<CardPosition> row;

            if (direction < 0)
            {
                var above = cards.Where(x => x.Point.Y < sourceCardPoint.Value.Y - rowTolerance).ToList();
                if (above.Count == 0)
                {
                    return null;
                }

                var rowY = above.Max(x => x.Point.Y);
                row = above.Where(x => Math.Abs(x.Point.Y - rowY) <= rowTolerance).ToList();
            }
            else
            {
                var below = cards.Where(x => x.Point.Y > sourceCardPoint.Value.Y + rowTolerance).ToList();
                if (below.Count == 0)
                {
                    return null;
                }

                var rowY = below.Min(x => x.Point.Y);
                row = below.Where(x => Math.Abs(x.Point.Y - rowY) <= rowTolerance).ToList();
            }

            if (row.Count == 0)
            {
                return null;
            }

            var sourceCardCenterX = sourceCardPoint.Value.X + (sourceCard.ActualWidth / 2.0);
            var targetCard = row
                .OrderBy(x => Math.Abs((x.Point.X + (x.Card.ActualWidth / 2.0)) - sourceCardCenterX))
                .First()
                .Card;

            var sourceButtonPoint = GetPosition(sourceButton);
            var preferredButtonX = sourceButtonPoint.HasValue
                ? sourceButtonPoint.Value.X + (sourceButton.ActualWidth / 2.0)
                : sourceCardCenterX;

            return FindNearestActionButtonInCard(targetCard, preferredButtonX);
        }

        private ButtonBase FindNearestActionButtonInCard(FrameworkElement card, double preferredCenterX)
        {
            if (card == null)
            {
                return null;
            }

            var candidates = FindVisualChildren<ButtonBase>(card)
                .Where(button => button != null && button.IsVisible && button.IsEnabled)
                .Where(button => IsCardAction((button as FrameworkElement)?.Tag as string))
                .Select(button => new
                {
                    Button = button,
                    Point = GetPosition(button)
                })
                .Where(x => x.Point.HasValue)
                .OrderBy(x => Math.Abs((x.Point.Value.X + (x.Button.ActualWidth / 2.0)) - preferredCenterX))
                .ToList();

            return candidates.Count > 0 ? candidates[0].Button : null;
        }

        private List<CardPosition> GetCardRootsWithPositions()
        {
            return FindVisualChildren<FrameworkElement>(Control)
                .Where(element => element != null && element.IsVisible)
                .Where(element => string.Equals(element.Tag as string, "CommunityCard", StringComparison.Ordinal))
                .Select(element => new
                {
                    Card = element,
                    Point = GetPosition(element)
                })
                .Where(x => x.Point.HasValue)
                .Select(x => new CardPosition
                {
                    Card = x.Card,
                    Point = x.Point.Value
                })
                .ToList();
        }

        private sealed class CardPosition
        {
            public FrameworkElement Card { get; set; }
            public Point Point { get; set; }
        }

        private void EnsureCardFullyVisible(FrameworkElement card)
        {
            if (card == null)
            {
                return;
            }

            var scrollViewer = Control?.FindName("CatalogScrollViewer") as ScrollViewer;
            if (scrollViewer == null || scrollViewer.ViewportHeight <= 0)
            {
                card.BringIntoView();
                return;
            }

            try
            {
                var point = card.TransformToAncestor(scrollViewer).Transform(new Point(0, 0));
                var top = point.Y;
                var bottom = top + card.ActualHeight;
                const double padding = 10.0;

                if (top < padding)
                {
                    scrollViewer.ScrollToVerticalOffset(
                        Math.Max(0, scrollViewer.VerticalOffset + top - padding));
                }
                else if (bottom > scrollViewer.ViewportHeight - padding)
                {
                    scrollViewer.ScrollToVerticalOffset(
                        scrollViewer.VerticalOffset + (bottom - (scrollViewer.ViewportHeight - padding)));
                }
            }
            catch
            {
                card.BringIntoView();
            }
        }

        private FrameworkElement FindCardRoot(DependencyObject source)
        {
            while (source != null)
            {
                if (source is FrameworkElement element &&
                    string.Equals(element.Tag as string, "CommunityCard", StringComparison.Ordinal))
                {
                    return element;
                }

                try
                {
                    source = System.Windows.Media.VisualTreeHelper.GetParent(source);
                }
                catch
                {
                    source = LogicalTreeHelper.GetParent(source);
                }
            }

            return null;
        }

        private Point? GetPosition(FrameworkElement element)
        {
            try
            {
                if (element == null || Control == null || !element.IsVisible)
                {
                    return null;
                }

                return element.TransformToAncestor(Control).Transform(new Point(0, 0));
            }
            catch
            {
                return null;
            }
        }

        private static ButtonBase FindParentButton(DependencyObject source)
        {
            while (source != null)
            {
                if (source is ButtonBase button)
                {
                    return button;
                }

                try
                {
                    source = System.Windows.Media.VisualTreeHelper.GetParent(source);
                }
                catch
                {
                    source = LogicalTreeHelper.GetParent(source);
                }
            }

            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                yield break;
            }

            var count = 0;
            try
            {
                count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            }
            catch
            {
                yield break;
            }

            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T typed)
                {
                    yield return typed;
                }

                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private async void OnButtonClick(object sender, RoutedEventArgs e)
        {
            if (disposed)
            {
                return;
            }

            var element = e.OriginalSource as DependencyObject;
            FrameworkElement buttonElement = null;
            while (element != null)
            {
                if (element is ButtonBase button)
                {
                    buttonElement = button as FrameworkElement;
                    break;
                }

                DependencyObject parent = null;
                try
                {
                    parent = System.Windows.Media.VisualTreeHelper.GetParent(element);
                }
                catch
                {
                    parent = LogicalTreeHelper.GetParent(element);
                }

                element = parent;
            }

            if (buttonElement == null)
            {
                return;
            }

            var action = buttonElement.Tag as string;
            if (string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            e.Handled = true;

            switch (action)
            {
                case "Back":
                    FullscreenSettingsView.ReturnFromCommunityVisualPacks();
                    break;

                case "Refresh":
                    await RefreshAsync();
                    break;

                case "InstallOrUpdate":
                    await InstallOrUpdateAsync(buttonElement.DataContext as CommunityVisualPackViewItem);
                    break;

                case "Uninstall":
                    Uninstall(buttonElement.DataContext as CommunityVisualPackViewItem);
                    break;
            }
        }

        private async Task RefreshAsync()
        {
            if (disposed)
            {
                return;
            }

            refreshCts?.Cancel();
            refreshCts?.Dispose();
            refreshCts = new CancellationTokenSource();
            var token = refreshCts.Token;

            viewModel.StatusText = Loc("CommunityPack_Loading", "Loading Community Packs...");
            viewModel.CountText = string.Empty;
            viewModel.IsEmpty = false;
            viewModel.Packs.Clear();

            try
            {
                var catalog = await service.GetCatalogAsync(token);
                token.ThrowIfCancellationRequested();

                var installed = service.GetInstalledPacks();
                foreach (var source in catalog.Packs ?? new List<CommunityPackCatalogItem>())
                {
                    var item = new CommunityVisualPackViewItem
                    {
                        Source = source
                    };

                    ApplyInstalledState(item, installed);
                    viewModel.Packs.Add(item);
                }

                viewModel.IsEmpty = viewModel.Packs.Count == 0;
                viewModel.CountText = string.Format(
                    Loc("CommunityPack_CountFormat", "{0} pack(s)"),
                    viewModel.Packs.Count);

                viewModel.StatusText = catalog.UsedCachedCatalog
                    ? Loc("CommunityPack_Cached", "Showing the last cached community catalog.")
                    : string.Empty;

                var previewTasks = viewModel.Packs.Select(x => LoadPreviewAsync(x, token)).ToArray();
                await Task.WhenAll(previewTasks);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][CommunityPacks][Fullscreen] Browser refresh failed for " + packType + ".");
                viewModel.Packs.Clear();
                viewModel.IsEmpty = true;
                viewModel.CountText = string.Empty;
                viewModel.StatusText = Loc("CommunityPack_LoadError", "The Community Packs catalog could not be loaded.") + " " + ex.Message;
            }
        }

        private async Task LoadPreviewAsync(CommunityVisualPackViewItem item, CancellationToken token)
        {
            try
            {
                var path = await service.GetPreviewPathAsync(item.Source, token);
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return;
                }

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 760;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                if (image.CanFreeze)
                {
                    image.Freeze();
                }

                item.PreviewImage = image;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AnikiHelper][CommunityPacks][Fullscreen] Preview load failed: " + ex.Message);
            }
        }

        private async Task InstallOrUpdateAsync(CommunityVisualPackViewItem item)
        {
            if (item == null || item.IsBusy || (!item.UpdateAvailable && item.IsInstalled))
            {
                return;
            }

            var wasUpdate = item.UpdateAvailable;
            SetAllBusy(true);
            item.ActionText = wasUpdate
                ? Loc("CommunityPack_Updating", "Updating...")
                : Loc("CommunityPack_Installing", "Installing...");
            viewModel.StatusText = string.Format(
                wasUpdate
                    ? Loc("CommunityPack_UpdatingStatus", "Updating {0}...")
                    : Loc("CommunityPack_InstallingStatus", "Installing {0}..."),
                item.Name);

            try
            {
                var result = await service.InstallOrUpdateAsync(item.Source, CancellationToken.None);
                RefreshInstalledStates();

                viewModel.StatusText = string.Format(
                    wasUpdate
                        ? Loc("CommunityPack_UpdateSuccess", "'{0}' was updated to version {1}.")
                        : Loc("CommunityPack_InstallSuccess", "'{0}' was installed. You can now select it in Aniki ReMake settings."),
                    result.PackName,
                    result.Version);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][CommunityPacks][Fullscreen] Install/update failed for " + item.Id + ".");
                viewModel.StatusText = Loc("CommunityPack_InstallError", "The Community Pack could not be installed:") + " " + ex.Message;
                api.Dialogs.ShowErrorMessage(
                    Loc("CommunityPack_InstallError", "The Community Pack could not be installed:") + Environment.NewLine + ex.Message,
                    viewModel.WindowTitle);
            }
            finally
            {
                SetAllBusy(false);
                RefreshInstalledStates();
                QueueRestorePackFocus(item.Id, "Uninstall");
            }
        }

        private void Uninstall(CommunityVisualPackViewItem item)
        {
            if (item == null || item.IsBusy || !item.IsInstalled)
            {
                return;
            }

            var confirmText = string.Format(
                Loc("CommunityPack_UninstallConfirm", "Uninstall '{0}' from the local pack library?"),
                item.Name);

            var confirmation = api.Dialogs.ShowMessage(
                confirmText,
                viewModel.WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
            {
                QueueRestorePackFocus(item.Id, "Uninstall");
                return;
            }

            SetAllBusy(true);
            viewModel.StatusText = string.Format(
                Loc("CommunityPack_UninstallingStatus", "Uninstalling {0}..."),
                item.Name);

            try
            {
                service.Uninstall(item.Id);
                RefreshInstalledStates();
                viewModel.StatusText = string.Format(
                    Loc("CommunityPack_UninstallSuccess", "'{0}' was uninstalled."),
                    item.Name);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][CommunityPacks][Fullscreen] Uninstall failed for " + item.Id + ".");
                viewModel.StatusText = Loc("CommunityPack_UninstallError", "The Community Pack could not be uninstalled:") + " " + ex.Message;
                api.Dialogs.ShowErrorMessage(
                    Loc("CommunityPack_UninstallError", "The Community Pack could not be uninstalled:") + Environment.NewLine + ex.Message,
                    viewModel.WindowTitle);
            }
            finally
            {
                SetAllBusy(false);
                RefreshInstalledStates();
                QueueRestorePackFocus(item.Id, "InstallOrUpdate");
            }
        }

        private void SetAllBusy(bool busy)
        {
            foreach (var pack in viewModel.Packs)
            {
                pack.IsBusy = busy;
            }
        }

        private void RefreshInstalledStates()
        {
            var installed = service.GetInstalledPacks();
            foreach (var pack in viewModel.Packs)
            {
                ApplyInstalledState(pack, installed);
            }
        }

        private static void ApplyInstalledState(
            CommunityVisualPackViewItem item,
            Dictionary<string, CommunityPackInstallation> installed)
        {
            if (item?.Source == null)
            {
                return;
            }

            CommunityPackInstallation record;
            if (installed != null && installed.TryGetValue(item.Id, out record) && record != null)
            {
                item.IsInstalled = true;
                item.InstalledVersion = record.Version ?? string.Empty;

                var update = false;
                try
                {
                    update = CommunityVisualPackService.CompareVersions(item.Version, record.Version) > 0;
                }
                catch
                {
                }

                item.UpdateAvailable = update;
                if (update)
                {
                    item.StatusLabel = Loc("CommunityPack_UpdateAvailable", "UPDATE");
                    item.ActionText = Loc("CommunityPack_Update", "Update");
                    item.StatusBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(151, 102, 37));
                }
                else
                {
                    item.StatusLabel = Loc("CommunityPack_Installed", "INSTALLED");
                    item.ActionText = Loc("CommunityPack_Installed", "Installed");
                    item.StatusBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 122, 81));
                }
            }
            else
            {
                item.IsInstalled = false;
                item.InstalledVersion = string.Empty;
                item.UpdateAvailable = false;
                item.StatusLabel = Loc("CommunityPack_Available", "AVAILABLE");
                item.ActionText = Loc("CommunityPack_Install", "Install");
                item.StatusBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 88, 105));
            }
        }

        private static string Loc(string key, string fallback)
        {
            try
            {
                return Application.Current?.TryFindResource(key) as string ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try { refreshCts?.Cancel(); } catch { }
            try { refreshCts?.Dispose(); } catch { }
            refreshCts = null;

            try
            {
                Control.RemoveHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClick));
                Control.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
            }
            catch
            {
            }

            try { service?.Dispose(); } catch { }
        }
    }
}
