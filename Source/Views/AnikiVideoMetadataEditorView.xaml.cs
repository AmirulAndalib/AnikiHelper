using AnikiHelper.Services.VideoPlayer;
using Playnite.SDK;
using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AnikiHelper
{
    public partial class AnikiVideoMetadataEditorView : UserControl
    {
        private readonly AnikiVideoPlayerService playerService;
        private readonly AnikiVideoLibraryManagerItem item;
        private readonly ILogger logger;

        public bool WasSaved { get; private set; }

        public AnikiVideoMetadataEditorView(AnikiVideoPlayerService playerService, AnikiVideoLibraryManagerItem item, ILogger logger)
        {
            this.playerService = playerService;
            this.item = item;
            this.logger = logger ?? LogManager.GetLogger();
            InitializeComponent();

            var metadata = playerService?.GetDesktopMetadata(item) ?? new AnikiVideoMetadataRecord();
            TargetPathText.Text = item?.FullPath ?? string.Empty;
            TitleBox.Text = string.IsNullOrWhiteSpace(metadata.Title) ? item?.Name ?? string.Empty : metadata.Title;
            YearBox.Text = metadata.Year > 0 ? metadata.Year.ToString(CultureInfo.InvariantCulture) : string.Empty;
            TypeCombo.SelectedValue = string.IsNullOrWhiteSpace(metadata.MediaType) ? item?.Kind ?? "movies" : metadata.MediaType;
            GenresBox.Text = metadata.Genres ?? string.Empty;
            RatingBox.Text = metadata.Rating > 0.0 ? metadata.Rating.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;
            OverviewBox.Text = metadata.Overview ?? string.Empty;
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (playerService == null || item == null) return;
            int year = 0;
            int.TryParse((YearBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out year);
            double rating = 0.0;
            double.TryParse((RatingBox.Text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out rating);
            var metadata = new AnikiVideoMetadataRecord
            {
                Title = (TitleBox.Text ?? string.Empty).Trim(),
                Year = year,
                MediaType = TypeCombo.SelectedValue?.ToString() ?? item.Kind,
                Genres = (GenresBox.Text ?? string.Empty).Trim(),
                Rating = Math.Max(0.0, Math.Min(10.0, rating)),
                Overview = (OverviewBox.Text ?? string.Empty).Trim(),
                Provider = "MANUAL",
                IsManual = true
            };
            try
            {
                WasSaved = await playerService.SaveDesktopMetadataAsync(item, metadata).ConfigureAwait(true);
                if (WasSaved)
                {
                    var window = Window.GetWindow(this);
                    if (window != null)
                    {
                        window.DialogResult = true;
                        window.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[AnikiHelper][VideoCenter] Failed to save manual metadata.");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }
    }
}
