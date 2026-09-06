using AnikiHelper.Services.CommunityPacks;
using AnikiHelper.Services.VisualPacks;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AnikiHelper
{
    public sealed class CommunityVisualPackViewItem : ObservableObject
    {
        public CommunityPackCatalogItem Source { get; set; }

        public string Id => Source?.Id ?? string.Empty;
        public string Name => string.IsNullOrWhiteSpace(Source?.Name) ? "Community Pack" : Source.Name;
        public string Description => Source?.Description ?? string.Empty;
        public bool Featured => Source?.Featured == true;
        public string Version => Source?.Version ?? string.Empty;
        public string AuthorDisplay => string.IsNullOrWhiteSpace(Source?.Author) ? string.Empty : Source.Author;
        public string VersionDisplay => string.IsNullOrWhiteSpace(Version) ? string.Empty : "v" + Version;
        public string PackType => CommunityPackService.NormalizePackType(Source?.Type);
        public bool ShowPreview => true;

        private ImageSource previewImage;
        public ImageSource PreviewImage
        {
            get => previewImage;
            set
            {
                SetValue(ref previewImage, value);
                OnPropertyChanged(nameof(HasPreview));
            }
        }

        public bool HasPreview => PreviewImage != null;

        private string installedVersion = string.Empty;
        public string InstalledVersion
        {
            get => installedVersion;
            set => SetValue(ref installedVersion, value ?? string.Empty);
        }

        private bool isInstalled;
        public bool IsInstalled
        {
            get => isInstalled;
            set
            {
                if (isInstalled == value) return;
                SetValue(ref isInstalled, value);
                OnPropertyChanged(nameof(CanAction));
                OnPropertyChanged(nameof(CanUninstall));
                OnPropertyChanged(nameof(HasInstallOrUpdateAction));
            }
        }

        private bool updateAvailable;
        public bool UpdateAvailable
        {
            get => updateAvailable;
            set
            {
                if (updateAvailable == value) return;
                SetValue(ref updateAvailable, value);
                OnPropertyChanged(nameof(CanAction));
                OnPropertyChanged(nameof(HasInstallOrUpdateAction));
            }
        }

        private bool isBusy;
        public bool IsBusy
        {
            get => isBusy;
            set
            {
                if (isBusy == value) return;
                SetValue(ref isBusy, value);
                OnPropertyChanged(nameof(CanAction));
                OnPropertyChanged(nameof(CanUninstall));
            }
        }

        private string actionText = string.Empty;
        public string ActionText
        {
            get => actionText;
            set => SetValue(ref actionText, value ?? string.Empty);
        }

        private string statusLabel = string.Empty;
        public string StatusLabel
        {
            get => statusLabel;
            set => SetValue(ref statusLabel, value ?? string.Empty);
        }

        private Brush statusBackground = new SolidColorBrush(Color.FromRgb(70, 84, 94));
        public Brush StatusBackground
        {
            get => statusBackground;
            set => SetValue(ref statusBackground, value);
        }

        public bool CanAction => !IsBusy && (!IsInstalled || UpdateAvailable);
        public bool CanUninstall => !IsBusy && IsInstalled;
        public bool HasInstallOrUpdateAction => !IsInstalled || UpdateAvailable;
    }

    public sealed class CommunityVisualPacksViewModel : ObservableObject
    {
        public ObservableCollection<CommunityVisualPackViewItem> Packs { get; } =
            new ObservableCollection<CommunityVisualPackViewItem>();

        private string statusText = string.Empty;
        public string StatusText
        {
            get => statusText;
            set => SetValue(ref statusText, value ?? string.Empty);
        }

        private string countText = string.Empty;
        public string CountText
        {
            get => countText;
            set => SetValue(ref countText, value ?? string.Empty);
        }

        private bool isEmpty;
        public bool IsEmpty
        {
            get => isEmpty;
            set => SetValue(ref isEmpty, value);
        }

        private string windowTitle = string.Empty;
        public string WindowTitle
        {
            get => windowTitle;
            set => SetValue(ref windowTitle, value ?? string.Empty);
        }

        private string windowDescription = string.Empty;
        public string WindowDescription
        {
            get => windowDescription;
            set => SetValue(ref windowDescription, value ?? string.Empty);
        }

        private string emptyText = string.Empty;
        public string EmptyText
        {
            get => emptyText;
            set => SetValue(ref emptyText, value ?? string.Empty);
        }
    }

    public partial class AnikiCommunityVisualPacksView : UserControl, IDisposable
    {
        private readonly global::AnikiHelper.AnikiHelper plugin;
        private readonly IPlayniteAPI api;
        private readonly ILogger logger;
        private readonly CommunityPackService service;
        private readonly CommunityVisualPacksViewModel viewModel = new CommunityVisualPacksViewModel();
        private readonly string packType;
        private CancellationTokenSource refreshCts;
        private bool disposed;

        public AnikiCommunityVisualPacksView(
            global::AnikiHelper.AnikiHelper plugin,
            IPlayniteAPI api,
            string pluginUserDataPath,
            ILogger logger)
            : this(plugin, api, pluginUserDataPath, logger, "visual")
        {
        }

        public AnikiCommunityVisualPacksView(
            global::AnikiHelper.AnikiHelper plugin,
            IPlayniteAPI api,
            string pluginUserDataPath,
            ILogger logger,
            string packType)
        {
            this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.logger = logger;
            this.packType = CommunityPackService.NormalizePackType(packType);
            service = new CommunityPackService(plugin, api, pluginUserDataPath, logger, this.packType);

            InitializeComponent();
            UpdateHeaderTexts();
            DataContext = viewModel;
            Loaded += AnikiCommunityVisualPacksView_Loaded;
        }

        public string WindowTitle => GetWindowTitle(packType);

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

        public static string GetWindowTitle(string packType)
        {
            var typeName = GetLocalizedPackTypeName(packType);
            return string.Format(Loc("CommunityPack_WindowTitleFormat", "Community {0}"), typeName);
        }

        private static string GetLocalizedPackTypeName(string packType)
        {
            switch (CommunityPackService.NormalizePackType(packType))
            {
                case "visual": return Loc("CommunityPack_TypeVisual", "Visual Packs");
                case "color": return Loc("CommunityPack_TypeColor", "Color Packs");
                case "login": return Loc("CommunityPack_TypeLogin", "Login Packs");
                case "sound": return Loc("CommunityPack_TypeSound", "Sound Packs");
                case "complete": return Loc("CommunityPack_TypeComplete", "Complete Packs");
                default: return Loc("CommunityPack_TypeAll", "Packs");
            }
        }

        private async void AnikiCommunityVisualPacksView_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= AnikiCommunityVisualPacksView_Loaded;
            await RefreshAsync();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (disposed) return;

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
                    var item = new CommunityVisualPackViewItem { Source = source };
                    ApplyInstalledState(item, installed);
                    viewModel.Packs.Add(item);
                }

                viewModel.IsEmpty = viewModel.Packs.Count == 0;
                viewModel.CountText = string.Format(Loc("CommunityPack_CountFormat", "{0} pack(s)"), viewModel.Packs.Count);
                viewModel.StatusText = catalog.UsedCachedCatalog
                    ? Loc("CommunityPack_Cached", "GitHub is unavailable. Showing the last cached catalog.")
                    : Loc("CommunityPack_Ready", "Community catalog loaded from GitHub.");

                var previewTasks = viewModel.Packs.Select(x => LoadPreviewAsync(x, token)).ToArray();
                await Task.WhenAll(previewTasks);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][CommunityPacks] Browser refresh failed for " + packType + ".");
                viewModel.Packs.Clear();
                viewModel.IsEmpty = true;
                viewModel.CountText = string.Empty;
                viewModel.StatusText = Loc("CommunityPack_LoadError", "The Community Packs catalog could not be loaded.") + " " + ex.Message;
            }
        }

        private async Task LoadPreviewAsync(CommunityVisualPackViewItem item, CancellationToken token)
        {
            if (item == null || !item.ShowPreview)
            {
                if (item != null)
                {
                    item.PreviewImage = null;
                }
                return;
            }

            try
            {
                var path = await service.GetPreviewPathAsync(item.Source, token);
                token.ThrowIfCancellationRequested();
                item.PreviewImage = CommunityPackPreviewHelper.LoadImageOrFallback(path, 600);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                item.PreviewImage = CommunityPackPreviewHelper.LoadFallback(600);
                Debug.WriteLine("[AnikiHelper][CommunityPacks] Preview load failed: " + ex.Message);
            }
        }

        private async void PackAction_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as CommunityVisualPackViewItem;
            if (item == null || item.IsBusy || (!item.UpdateAvailable && item.IsInstalled)) return;

            var wasUpdate = item.UpdateAvailable;
            foreach (var pack in viewModel.Packs) pack.IsBusy = true;
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
                var installed = service.GetInstalledPacks();
                foreach (var pack in viewModel.Packs) ApplyInstalledState(pack, installed);

                viewModel.StatusText = string.Format(
                    wasUpdate
                        ? Loc("CommunityPack_UpdateSuccess", "'{0}' was updated to version {1}.")
                        : Loc("CommunityPack_InstallSuccess", "'{0}' was installed. You can now select it in Aniki ReMake settings."),
                    result.PackName,
                    result.Version);

                api.Dialogs.ShowMessage(viewModel.StatusText, viewModel.WindowTitle);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][CommunityPacks] Install/update failed for " + item.Id + ".");
                viewModel.StatusText = Loc("CommunityPack_InstallError", "The Community Pack could not be installed:") + " " + ex.Message;
                api.Dialogs.ShowErrorMessage(
                    Loc("CommunityPack_InstallError", "The Community Pack could not be installed:") + Environment.NewLine + ex.Message,
                    viewModel.WindowTitle);
            }
            finally
            {
                var installed = service.GetInstalledPacks();
                foreach (var pack in viewModel.Packs)
                {
                    pack.IsBusy = false;
                    ApplyInstalledState(pack, installed);
                }
            }
        }

        internal static void ApplyInstalledState(
            CommunityVisualPackViewItem item,
            Dictionary<string, CommunityPackInstallation> installed)
        {
            if (item?.Source == null) return;

            if (installed != null && installed.TryGetValue(item.Id, out var record) && record != null)
            {
                item.IsInstalled = true;
                item.InstalledVersion = record.Version ?? string.Empty;
                var update = false;
                try { update = CommunityVisualPackService.CompareVersions(item.Version, record.Version) > 0; } catch { }
                item.UpdateAvailable = update;
                if (update)
                {
                    item.StatusLabel = Loc("CommunityPack_UpdateAvailable", "UPDATE");
                    item.ActionText = Loc("CommunityPack_Update", "Update");
                    item.StatusBackground = new SolidColorBrush(Color.FromRgb(151, 102, 37));
                }
                else
                {
                    item.StatusLabel = Loc("CommunityPack_Installed", "INSTALLED");
                    item.ActionText = Loc("CommunityPack_Installed", "Installed");
                    item.StatusBackground = new SolidColorBrush(Color.FromRgb(52, 122, 81));
                }
            }
            else
            {
                item.IsInstalled = false;
                item.InstalledVersion = string.Empty;
                item.UpdateAvailable = false;
                item.StatusLabel = Loc("CommunityPack_Available", "AVAILABLE");
                item.ActionText = Loc("CommunityPack_Install", "Install");
                item.StatusBackground = new SolidColorBrush(Color.FromRgb(55, 88, 105));
            }
        }

        internal static string Loc(string key, string fallback)
        {
            try { return Application.Current?.TryFindResource(key) as string ?? fallback; }
            catch { return fallback; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { refreshCts?.Cancel(); } catch { }
            try { refreshCts?.Dispose(); } catch { }
            refreshCts = null;
            service.Dispose();
        }
    }
}
