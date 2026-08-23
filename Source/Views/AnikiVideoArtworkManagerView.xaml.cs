using AnikiHelper.Services.VideoPlayer;
using Microsoft.Win32;
using Playnite.SDK;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AnikiHelper
{
    public partial class AnikiVideoArtworkManagerView : UserControl, INotifyPropertyChanged, IDisposable
    {
        private readonly IPlayniteAPI playniteApi;
        private readonly AnikiVideoPlayerService playerService;
        private readonly AnikiVideoLibraryManagerItem item;
        private readonly ILogger logger;
        private readonly string artworkTarget;
        private CancellationTokenSource searchCts;
        private AnikiVideoLibraryArtworkChoice selectedChoice;
        private bool disposed;

        public event PropertyChangedEventHandler PropertyChanged;
        public ObservableCollection<AnikiVideoLibraryArtworkChoice> Choices { get; } = new ObservableCollection<AnikiVideoLibraryArtworkChoice>();
        public bool ArtworkWasApplied { get; private set; }
        public bool IsWideTarget => !string.Equals(artworkTarget, "cover", StringComparison.OrdinalIgnoreCase);
        public double PreviewCardWidth => IsWideTarget ? 230.0 : 150.0;
        public double PreviewImageHeight => IsWideTarget ? 130.0 : 215.0;

        public AnikiVideoLibraryArtworkChoice SelectedChoice
        {
            get => selectedChoice;
            set
            {
                if (ReferenceEquals(selectedChoice, value))
                {
                    return;
                }
                selectedChoice = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChoice)));
                if (ApplyButton != null)
                {
                    ApplyButton.IsEnabled = selectedChoice != null;
                }
            }
        }

        public AnikiVideoArtworkManagerView(
            IPlayniteAPI playniteApi,
            AnikiVideoPlayerService playerService,
            AnikiVideoLibraryManagerItem item,
            ILogger logger,
            string artworkTarget = "cover")
        {
            this.playniteApi = playniteApi;
            this.playerService = playerService;
            this.item = item;
            this.logger = logger ?? LogManager.GetLogger();
            this.artworkTarget = NormalizeTarget(artworkTarget);

            InitializeComponent();
            DataContext = this;
            TargetTitleText.Text = item?.Name ?? string.Empty;
            ArtworkTargetText.Text = GetTargetDisplayName();
            ArtworkSearchBox.Text = playerService?.GetDesktopArtworkDefaultSearchText(item) ?? item?.Name ?? string.Empty;
            if (LocalFileButton != null)
            {
                LocalFileButton.Content = Loc("VideoLibraryManager_ChooseLocalArtwork", "Choose image...");
            }

            Loaded += async (_, __) => await SearchAsync().ConfigureAwait(true);
            ArtworkChoicesList.SelectionChanged += (_, __) => SelectedChoice = ArtworkChoicesList.SelectedItem as AnikiVideoLibraryArtworkChoice;
        }

        private static string NormalizeTarget(string target)
        {
            var value = (target ?? string.Empty).Trim().ToLowerInvariant();
            return value == "landscape" || value == "hero" || value == "logo" ? value : "cover";
        }

        private string GetTargetDisplayName()
        {
            switch (artworkTarget)
            {
                case "landscape": return Loc("VideoLibraryManager_Landscape", "Landscape");
                case "hero": return Loc("VideoLibraryManager_Hero", "Hero wallpaper");
                case "logo": return Loc("VideoLibraryManager_Logo", "Logo");
                default: return Loc("VideoLibraryManager_Cover", "Cover");
            }
        }

        private string Loc(string key, string fallback)
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

        private async Task SearchAsync()
        {
            if (disposed || playerService == null || item == null)
            {
                return;
            }

            var old = searchCts;
            searchCts = new CancellationTokenSource();
            try { old?.Cancel(); old?.Dispose(); } catch { }
            var owner = searchCts;

            SearchButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            Choices.Clear();
            SelectedChoice = null;
            ArtworkStatusText.Visibility = Visibility.Visible;
            ArtworkStatusText.Text = Loc("VideoLibraryManager_SearchingArtwork", "Searching artwork providers...");

            try
            {
                var choices = await playerService.SearchDesktopArtworkAsync(
                    item,
                    ArtworkSearchBox.Text,
                    artworkTarget,
                    owner.Token).ConfigureAwait(true);

                if (!ReferenceEquals(searchCts, owner) || owner.IsCancellationRequested)
                {
                    return;
                }

                foreach (var choice in choices ?? Array.Empty<AnikiVideoLibraryArtworkChoice>())
                {
                    Choices.Add(choice);
                }

                ArtworkStatusText.Visibility = Choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ArtworkStatusText.Text = Choices.Count == 0
                    ? Loc("VideoLibraryManager_NoArtworkResults", "No artwork result matched this search.")
                    : string.Empty;

                if (Choices.Count > 0)
                {
                    SelectedChoice = Choices[0];
                    ArtworkChoicesList.SelectedIndex = 0;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[AnikiHelper][VideoCenter][LibraryManager] Artwork search failed.");
                ArtworkStatusText.Visibility = Visibility.Visible;
                ArtworkStatusText.Text = Loc("VideoLibraryManager_ArtworkSearchFailed", "Artwork search failed.");
            }
            finally
            {
                if (ReferenceEquals(searchCts, owner))
                {
                    searchCts = null;
                }
                owner.Dispose();
                SearchButton.IsEnabled = true;
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await SearchAsync().ConfigureAwait(true);
        }
        private async void LocalFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (playerService == null || item == null)
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                Filter = "Image files|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All files|*.*",
                Title = Loc("VideoLibraryManager_ChooseLocalArtwork", "Choose image...")
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            {
                return;
            }

            ApplyButton.IsEnabled = false;
            SearchButton.IsEnabled = false;
            if (LocalFileButton != null)
            {
                LocalFileButton.IsEnabled = false;
            }
            ArtworkStatusText.Visibility = Visibility.Visible;
            ArtworkStatusText.Text = Loc("VideoLibraryManager_ApplyingArtwork", "Applying artwork...");

            try
            {
                var ok = await playerService.ApplyDesktopLocalArtworkAsync(
                    item,
                    dialog.FileName,
                    artworkTarget,
                    CancellationToken.None).ConfigureAwait(true);

                if (!ok)
                {
                    ArtworkStatusText.Text = Loc("VideoLibraryManager_ArtworkApplyFailed", "Artwork could not be applied.");
                    SearchButton.IsEnabled = true;
                    if (LocalFileButton != null)
                    {
                        LocalFileButton.IsEnabled = true;
                    }
                    return;
                }

                ArtworkWasApplied = true;
                var window = Window.GetWindow(this);
                if (window != null)
                {
                    window.DialogResult = true;
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[AnikiHelper][VideoCenter][LibraryManager] Failed to import local artwork.");
                ArtworkStatusText.Text = Loc("VideoLibraryManager_ArtworkApplyFailed", "Artwork could not be applied.");
                SearchButton.IsEnabled = true;
                if (LocalFileButton != null)
                {
                    LocalFileButton.IsEnabled = true;
                }
            }
        }


        private async void ArtworkSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SearchAsync().ConfigureAwait(true);
            }
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedChoice == null || playerService == null || item == null)
            {
                return;
            }

            ApplyButton.IsEnabled = false;
            SearchButton.IsEnabled = false;
            ArtworkStatusText.Visibility = Visibility.Visible;
            ArtworkStatusText.Text = Loc("VideoLibraryManager_ApplyingArtwork", "Applying artwork...");

            try
            {
                var ok = await playerService.ApplyDesktopArtworkChoiceAsync(
                    item,
                    SelectedChoice,
                    artworkTarget,
                    CancellationToken.None).ConfigureAwait(true);

                if (!ok)
                {
                    ArtworkStatusText.Text = Loc("VideoLibraryManager_ArtworkApplyFailed", "Artwork could not be applied.");
                    ApplyButton.IsEnabled = true;
                    SearchButton.IsEnabled = true;
                    if (LocalFileButton != null)
                    {
                        LocalFileButton.IsEnabled = true;
                    }
                    return;
                }

                ArtworkWasApplied = true;
                var window = Window.GetWindow(this);
                if (window != null)
                {
                    window.DialogResult = true;
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[AnikiHelper][VideoCenter][LibraryManager] Failed to apply artwork.");
                ArtworkStatusText.Text = Loc("VideoLibraryManager_ArtworkApplyFailed", "Artwork could not be applied.");
                ApplyButton.IsEnabled = true;
                SearchButton.IsEnabled = true;
                if (LocalFileButton != null)
                {
                    LocalFileButton.IsEnabled = true;
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            try { searchCts?.Cancel(); } catch { }
            try { searchCts?.Dispose(); } catch { }
            searchCts = null;
        }
    }
}
