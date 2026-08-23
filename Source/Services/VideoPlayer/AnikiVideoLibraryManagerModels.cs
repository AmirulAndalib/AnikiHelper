using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Windows;

namespace AnikiHelper.Services.VideoPlayer
{
    public sealed class AnikiVideoLibraryManagerItem : ObservableObject
    {
        private string name = string.Empty;
        private string typeLabel = string.Empty;
        private string kind = string.Empty;
        private string fullPath = string.Empty;
        private string artworkPath = string.Empty;
        private string landscapePath = string.Empty;
        private string heroPath = string.Empty;
        private string logoPath = string.Empty;
        private string statusText = string.Empty;
        private int year;
        private string overview = string.Empty;
        private string genres = string.Empty;
        private string ratingText = string.Empty;
        private string metadataProvider = string.Empty;
        private bool isWatched;
        private bool isAvailable = true;
        private bool hasArtwork;
        private bool hasLandscape;
        private bool hasHero;
        private bool hasLogo;
        private bool isDirectory;
        private bool isVideo;
        private bool isLibraryRoot;

        public string Name { get => name; set => SetValue(ref name, value ?? string.Empty); }
        public string TypeLabel { get => typeLabel; set => SetValue(ref typeLabel, value ?? string.Empty); }
        public string Kind { get => kind; set => SetValue(ref kind, value ?? string.Empty); }
        public string FullPath { get => fullPath; set => SetValue(ref fullPath, value ?? string.Empty); }
        public string ArtworkPath { get => artworkPath; set => SetValue(ref artworkPath, value ?? string.Empty); }
        public string LandscapePath { get => landscapePath; set => SetValue(ref landscapePath, value ?? string.Empty); }
        public string HeroPath { get => heroPath; set => SetValue(ref heroPath, value ?? string.Empty); }
        public string LogoPath { get => logoPath; set => SetValue(ref logoPath, value ?? string.Empty); }
        public string StatusText { get => statusText; set => SetValue(ref statusText, value ?? string.Empty); }
        public int Year { get => year; set { SetValue(ref year, Math.Max(0, value)); OnPropertyChanged(nameof(MetadataSummary)); } }
        public string Overview { get => overview; set => SetValue(ref overview, value ?? string.Empty); }
        public string Genres { get => genres; set => SetValue(ref genres, value ?? string.Empty); }
        public string RatingText { get => ratingText; set { SetValue(ref ratingText, value ?? string.Empty); OnPropertyChanged(nameof(MetadataSummary)); } }
        public string MetadataProvider { get => metadataProvider; set { SetValue(ref metadataProvider, value ?? string.Empty); OnPropertyChanged(nameof(MetadataSummary)); } }
        public string MetadataSummary
        {
            get
            {
                var parts = new List<string>();
                if (Year > 0) parts.Add(Year.ToString());
                if (!string.IsNullOrWhiteSpace(RatingText)) parts.Add(RatingText);
                if (!string.IsNullOrWhiteSpace(MetadataProvider)) parts.Add(MetadataProvider);
                return string.Join("  •  ", parts);
            }
        }
        public bool IsWatched { get => isWatched; set => SetValue(ref isWatched, value); }
        public bool IsAvailable { get => isAvailable; set => SetValue(ref isAvailable, value); }
        public bool HasArtwork { get => hasArtwork; set => SetValue(ref hasArtwork, value); }
        public bool HasLandscape { get => hasLandscape; set => SetValue(ref hasLandscape, value); }
        public bool HasHero { get => hasHero; set => SetValue(ref hasHero, value); }
        public bool HasLogo { get => hasLogo; set => SetValue(ref hasLogo, value); }
        public bool IsDirectory { get => isDirectory; set => SetValue(ref isDirectory, value); }
        public bool IsVideo { get => isVideo; set => SetValue(ref isVideo, value); }
        public bool IsLibraryRoot { get => isLibraryRoot; set => SetValue(ref isLibraryRoot, value); }

        public bool IsMissingArtwork => IsAvailable && !IsLibraryRoot && !HasArtwork;
        public bool IsMissingBackdrop => IsAvailable && !IsLibraryRoot && !HasLandscape;
        public bool IsMissingHero => IsAvailable && !IsLibraryRoot && !HasHero;
        public bool IsUnavailable => !IsAvailable;
        public bool IsProblem => IsUnavailable || IsMissingArtwork || IsMissingBackdrop || IsMissingHero;
        public string ProblemText
        {
            get
            {
                if (IsUnavailable) return Loc("VideoLibraryManager_Unavailable", "Unavailable");

                var missing = new List<string>();
                if (IsMissingArtwork) missing.Add(Loc("VideoLibraryManager_Cover", "Cover").ToLowerInvariant());
                if (IsMissingBackdrop) missing.Add(Loc("VideoLibraryManager_Landscape", "Landscape").ToLowerInvariant());
                if (IsMissingHero) missing.Add(Loc("VideoLibraryManager_Hero", "Hero").ToLowerInvariant());
                if (missing.Count == 0) return string.Empty;

                var format = Loc("VideoLibraryManager_MissingAssets", "Missing: {0}");
                try { return string.Format(format, string.Join(", ", missing)); }
                catch { return "Missing: " + string.Join(", ", missing); }
            }
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

        public void NotifyArtworkStateChanged()
        {
            OnPropertyChanged(nameof(IsMissingArtwork));
            OnPropertyChanged(nameof(IsMissingBackdrop));
            OnPropertyChanged(nameof(IsMissingHero));
            OnPropertyChanged(nameof(IsUnavailable));
            OnPropertyChanged(nameof(IsProblem));
            OnPropertyChanged(nameof(ProblemText));
        }
    }

    public sealed class AnikiVideoLibraryArtworkChoice : ObservableObject
    {
        private bool isCurrent;
        public string PreviewPath { get; set; } = string.Empty;
        public string ProviderText { get; set; } = string.Empty;
        public string MatchText { get; set; } = string.Empty;
        public string LanguageText { get; set; } = string.Empty;
        public string SizeText { get; set; } = string.Empty;
        public string MetadataTitle { get; set; } = string.Empty;
        public int MetadataYear { get; set; }
        public string MetadataOverview { get; set; } = string.Empty;
        public string MetadataGenres { get; set; } = string.Empty;
        public double MetadataRating { get; set; }
        public int MetadataRuntimeMinutes { get; set; }
        public int MetadataVoteCount { get; set; }
        public string MetadataTagline { get; set; } = string.Empty;
        public string MetadataCredits { get; set; } = string.Empty;
        public string MetadataOriginalTitle { get; set; } = string.Empty;
        public string MetadataProviderId { get; set; } = string.Empty;
        public string ArtworkTarget { get; set; } = "cover";
        public bool IsCurrent
        {
            get => isCurrent;
            set => SetValue(ref isCurrent, value);
        }

        internal string RemoteImageUrl { get; set; } = string.Empty;
        internal object NativeChoice { get; set; }
    }
}
