using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.IO;

namespace AnikiHelper.Services.VideoPlayer
{
    public sealed class AnikiVideoBrowserItem : System.Collections.Generic.ObservableObject
    {
        private string name = string.Empty;
        public string Name
        {
            get => name;
            set => SetValue(ref name, value ?? string.Empty);
        }

        private string fullPath = string.Empty;
        public string FullPath
        {
            get => fullPath;
            set => SetValue(ref fullPath, value ?? string.Empty);
        }

        private string secondaryText = string.Empty;
        public string SecondaryText
        {
            get => secondaryText;
            set => SetValue(ref secondaryText, value ?? string.Empty);
        }

        private string typeLabel = string.Empty;
        public string TypeLabel
        {
            get => typeLabel;
            set => SetValue(ref typeLabel, value ?? string.Empty);
        }

        public bool IsDirectory { get; set; }
        public bool IsDrive { get; set; }
        public bool IsVideo { get; set; }
        public bool IsHomeShortcut { get; set; }
        public bool IsNetworkLocation { get; set; }
        public bool IsVirtualSeriesGroup { get; set; }
        public bool IsCollection { get; set; }
        public int CollectionId { get; set; }
        public int CollectionMemberCount { get; set; }
        public string CollectionPosterRemotePath { get; set; } = string.Empty;
        public string CollectionBackdropRemotePath { get; set; } = string.Empty;

        private bool isAvailable = true;
        public bool IsAvailable
        {
            get => isAvailable;
            set
            {
                SetValue(ref isAvailable, value);
                OnPropertyChanged(nameof(IsActionable));
            }
        }

        public bool IsActionable => IsAvailable || IsNetworkLocation;

        private int seasonNumber;
        public int SeasonNumber
        {
            get => seasonNumber;
            set
            {
                SetValue(ref seasonNumber, Math.Max(0, value));
                OnPropertyChanged(nameof(EpisodeCode));
            }
        }

        private int episodeNumber;
        public int EpisodeNumber
        {
            get => episodeNumber;
            set
            {
                SetValue(ref episodeNumber, Math.Max(0, value));
                OnPropertyChanged(nameof(EpisodeCode));
            }
        }

        public string EpisodeCode
        {
            get
            {
                if (SeasonNumber > 0 && EpisodeNumber > 0)
                {
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "S{0:00}E{1:00}", SeasonNumber, EpisodeNumber);
                }
                if (EpisodeNumber > 0)
                {
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "E{0:00}", EpisodeNumber);
                }
                return string.Empty;
            }
        }

        private bool isFavorite;
        public bool IsFavorite
        {
            get => isFavorite;
            set => SetValue(ref isFavorite, value);
        }

        private double progressPercent;
        public double ProgressPercent
        {
            get => progressPercent;
            set
            {
                SetValue(ref progressPercent, Math.Max(0.0, Math.Min(100.0, value)));
                OnPropertyChanged(nameof(HasProgress));
            }
        }

        private string progressText = string.Empty;
        public string ProgressText
        {
            get => progressText;
            set
            {
                SetValue(ref progressText, value ?? string.Empty);
                OnPropertyChanged(nameof(HasProgress));
            }
        }

        public bool HasProgress => ProgressPercent > 0.0 || !string.IsNullOrWhiteSpace(ProgressText);

        private string thumbnailPath = string.Empty;
        public string ThumbnailPath
        {
            get => thumbnailPath;
            set
            {
                SetValue(ref thumbnailPath, value ?? string.Empty);
                OnPropertyChanged(nameof(HasThumbnail));
                OnPropertyChanged(nameof(HasPortraitArtwork));
                OnPropertyChanged(nameof(HasLandscapeArtwork));
            }
        }

        public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailPath);

        private bool isPortraitArtwork;
        public bool IsPortraitArtwork
        {
            get => isPortraitArtwork;
            set
            {
                SetValue(ref isPortraitArtwork, value);
                OnPropertyChanged(nameof(HasPortraitArtwork));
                OnPropertyChanged(nameof(HasLandscapeArtwork));
            }
        }

        public bool HasPortraitArtwork => HasThumbnail && IsPortraitArtwork;
        public bool HasLandscapeArtwork => HasThumbnail && !IsPortraitArtwork;

        private string durationText = string.Empty;
        public string DurationText
        {
            get => durationText;
            set => SetValue(ref durationText, value ?? string.Empty);
        }

        private string qualityText = string.Empty;
        public string QualityText
        {
            get => qualityText;
            set
            {
                SetValue(ref qualityText, value ?? string.Empty);
                OnPropertyChanged(nameof(HasQuality));
            }
        }

        public bool HasQuality => !string.IsNullOrWhiteSpace(QualityText);

        private bool isWatched;
        public bool IsWatched
        {
            get => isWatched;
            set => SetValue(ref isWatched, value);
        }

        // Persistent library-index timestamps. These are metadata only: reading them never touches
        // the media file/NAS and allows All views to sort instantly without rescanning anything.
        public DateTime AddedUtc { get; set; }
        public DateTime LastWriteUtc { get; set; }

        public string Extension
        {
            get
            {
                try
                {
                    return IsVideo ? Path.GetExtension(FullPath)?.TrimStart('.').ToUpperInvariant() ?? string.Empty : string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }
    }
}
