using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AnikiHelper.Services.VideoPlayer
{
    public sealed class AnikiVideoIntroEndingEpisodeItem : ObservableObject
    {
        private bool isAnalyzed;
        private bool hasIntro;
        private bool hasEnding;
        private long introStartMs = -1L;
        private long introEndMs = -1L;
        private long endingStartMs = -1L;
        private double introConfidence;
        private double endingConfidence;
        private string statusText = string.Empty;
        private string sourceText = string.Empty;

        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int SeasonNumber { get; set; } = 1;
        public int EpisodeNumber { get; set; }
        public bool IsMovie { get; set; }
        public bool IsAnalyzed { get => isAnalyzed; set => SetValue(ref isAnalyzed, value); }
        public bool HasIntro
        {
            get => hasIntro;
            set
            {
                if (hasIntro == value) return;
                SetValue(ref hasIntro, value);
                RefreshDetectionText();
            }
        }
        public bool HasEnding
        {
            get => hasEnding;
            set
            {
                if (hasEnding == value) return;
                SetValue(ref hasEnding, value);
                RefreshDetectionText();
            }
        }
        public long IntroStartMs
        {
            get => introStartMs;
            set
            {
                if (introStartMs == value) return;
                SetValue(ref introStartMs, value);
                RefreshDetectionText();
            }
        }
        public long IntroEndMs
        {
            get => introEndMs;
            set
            {
                if (introEndMs == value) return;
                SetValue(ref introEndMs, value);
                RefreshDetectionText();
            }
        }
        public long EndingStartMs
        {
            get => endingStartMs;
            set
            {
                if (endingStartMs == value) return;
                SetValue(ref endingStartMs, value);
                RefreshDetectionText();
            }
        }
        public double IntroConfidence { get => introConfidence; set => SetValue(ref introConfidence, value); }
        public double EndingConfidence { get => endingConfidence; set => SetValue(ref endingConfidence, value); }
        public string StatusText { get => statusText; set => SetValue(ref statusText, value ?? string.Empty); }
        public string SourceText { get => sourceText; set => SetValue(ref sourceText, value ?? string.Empty); }

        public string EpisodeCode => IsMovie
            ? Loc("VideoIntroEnding_Movie", "Movie")
            : EpisodeNumber > 0
                ? string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}", Math.Max(1, SeasonNumber), EpisodeNumber)
                : string.Format(CultureInfo.InvariantCulture, "S{0:00}", Math.Max(1, SeasonNumber));

        public string IntroTimeText => HasIntro
            ? FormatTime(IntroStartMs) + " → " + FormatTime(IntroEndMs)
            : "—";

        public string EndingTimeText => HasEnding
            ? FormatTime(EndingStartMs)
            : "—";

        private void RefreshDetectionText()
        {
            OnPropertyChanged(nameof(IntroTimeText));
            OnPropertyChanged(nameof(EndingTimeText));
        }

        private static string FormatTime(long milliseconds)
        {
            if (milliseconds < 0L) return "—";
            var value = TimeSpan.FromMilliseconds(milliseconds);
            if (value.TotalHours >= 1.0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)value.TotalHours, value.Minutes, value.Seconds);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", (int)value.TotalMinutes, value.Seconds);
        }

        private static string Loc(string key, string fallback)
        {
            try
            {
                var value = System.Windows.Application.Current?.TryFindResource(key) as string;
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch { return fallback; }
        }
    }

    public sealed class AnikiVideoIntroEndingSeasonItem : ObservableObject
    {
        private string statusText = string.Empty;
        private string detailText = string.Empty;

        public int SeasonNumber { get; set; } = 1;
        public bool IsMovie { get; set; }
        public IReadOnlyList<AnikiVideoIntroEndingEpisodeItem> Episodes { get; set; } = Array.Empty<AnikiVideoIntroEndingEpisodeItem>();
        public string Title => IsMovie
            ? Loc("VideoIntroEnding_Movie", "Movie")
            : Loc("VideoIntroEnding_Season", "Season") + " " + Math.Max(1, SeasonNumber).ToString(CultureInfo.InvariantCulture);
        public string StatusText { get => statusText; set => SetValue(ref statusText, value ?? string.Empty); }
        public string DetailText { get => detailText; set => SetValue(ref detailText, value ?? string.Empty); }

        internal void RefreshSummary()
        {
            var episodeCount = Episodes?.Count ?? 0;
            var analyzed = Episodes?.Count(x => x?.IsAnalyzed == true) ?? 0;
            var intros = Episodes?.Count(x => x?.HasIntro == true) ?? 0;
            var endings = Episodes?.Count(x => x?.HasEnding == true) ?? 0;

            if (episodeCount == 0 || analyzed == 0)
                StatusText = Loc("VideoIntroEnding_StatusNotAnalyzed", "Not checked");
            else if (analyzed < episodeCount)
                StatusText = Loc("VideoIntroEnding_StatusPartialAnalysis", "Partially checked");
            else if (intros > 0 && endings > 0)
                StatusText = Loc("VideoIntroEnding_StatusDetected", "Markers available");
            else if (intros > 0 || endings > 0)
                StatusText = Loc("VideoIntroEnding_StatusPartial", "Partial markers");
            else
                StatusText = Loc("VideoIntroEnding_StatusNoMatch", "No markers available");

            DetailText = IsMovie
                ? string.Format(CultureInfo.InvariantCulture,
                    Loc("VideoIntroEnding_MovieSummary", "Checked {0}/{1}  •  Intro {2}  •  Ending {3}"),
                    analyzed, episodeCount, intros, endings)
                : string.Format(CultureInfo.InvariantCulture,
                    Loc("VideoIntroEnding_SeasonSummary", "{0} episodes  •  {1} checked  •  Intro {2}/{0}  •  Ending {3}/{0}"),
                    episodeCount, analyzed, intros, endings);

            OnPropertyChanged(nameof(Title));
        }

        private static string Loc(string key, string fallback)
        {
            try
            {
                var value = System.Windows.Application.Current?.TryFindResource(key) as string;
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch { return fallback; }
        }
    }

    public sealed class AnikiVideoIntroEndingSeriesItem : ObservableObject
    {
        private string statusText = string.Empty;
        private string detailText = string.Empty;
        private bool isAnalyzing;

        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string ArtworkPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public IReadOnlyList<AnikiVideoIntroEndingSeasonItem> Seasons { get; set; } = Array.Empty<AnikiVideoIntroEndingSeasonItem>();
        public string TypeLabel
        {
            get
            {
                if (string.Equals(Kind, "anime", StringComparison.OrdinalIgnoreCase)) return Loc("VideoPlayer_Anime", "Anime");
                if (string.Equals(Kind, "movies", StringComparison.OrdinalIgnoreCase)) return Loc("VideoPlayer_Movies", "Movies");
                return Loc("VideoPlayer_Series", "TV series");
            }
        }
        public string StatusText { get => statusText; set => SetValue(ref statusText, value ?? string.Empty); }
        public string DetailText { get => detailText; set => SetValue(ref detailText, value ?? string.Empty); }
        public bool IsAnalyzing { get => isAnalyzing; set => SetValue(ref isAnalyzing, value); }

        public int EpisodeCount => Seasons?.Sum(x => x?.Episodes?.Count ?? 0) ?? 0;
        public int AnalyzedCount => Seasons?.Sum(x => x?.Episodes?.Count(e => e?.IsAnalyzed == true) ?? 0) ?? 0;
        public int IntroCount => Seasons?.Sum(x => x?.Episodes?.Count(e => e?.HasIntro == true) ?? 0) ?? 0;
        public int EndingCount => Seasons?.Sum(x => x?.Episodes?.Count(e => e?.HasEnding == true) ?? 0) ?? 0;

        internal void RefreshSummary()
        {
            foreach (var season in Seasons ?? Array.Empty<AnikiVideoIntroEndingSeasonItem>()) season?.RefreshSummary();

            if (EpisodeCount == 0 || AnalyzedCount == 0)
                StatusText = Loc("VideoIntroEnding_StatusNotAnalyzed", "Not checked");
            else if (AnalyzedCount < EpisodeCount)
                StatusText = Loc("VideoIntroEnding_StatusPartialAnalysis", "Partially checked");
            else if (IntroCount > 0 && EndingCount > 0)
                StatusText = Loc("VideoIntroEnding_StatusDetected", "Markers available");
            else if (IntroCount > 0 || EndingCount > 0)
                StatusText = Loc("VideoIntroEnding_StatusPartial", "Partial markers");
            else
                StatusText = Loc("VideoIntroEnding_StatusNoMatch", "No markers available");

            DetailText = string.Format(
                CultureInfo.InvariantCulture,
                string.Equals(Kind, "movies", StringComparison.OrdinalIgnoreCase)
                    ? Loc("VideoIntroEnding_MovieItemSummary", "{0} file  •  {1} checked  •  Intro {2}  •  Ending {3}")
                    : Loc("VideoIntroEnding_SeriesSummary", "{0} episodes  •  {1} checked  •  Intro {2}  •  Ending {3}"),
                EpisodeCount, AnalyzedCount, IntroCount, EndingCount);

            OnPropertyChanged(nameof(TypeLabel));
            OnPropertyChanged(nameof(EpisodeCount));
            OnPropertyChanged(nameof(AnalyzedCount));
            OnPropertyChanged(nameof(IntroCount));
            OnPropertyChanged(nameof(EndingCount));
        }

        private static string Loc(string key, string fallback)
        {
            try
            {
                var value = System.Windows.Application.Current?.TryFindResource(key) as string;
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch { return fallback; }
        }
    }

    public sealed class AnikiVideoIntroEndingProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
