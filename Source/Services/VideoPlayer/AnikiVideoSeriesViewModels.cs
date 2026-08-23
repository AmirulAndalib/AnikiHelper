using Playnite.SDK;
using System;
using System.Collections.Generic;

namespace AnikiHelper.Services.VideoPlayer
{
    public sealed class AnikiVideoSeasonItem : ObservableObject
    {
        private int seasonNumber;
        private string name = string.Empty;
        private IReadOnlyList<AnikiVideoBrowserItem> episodes = Array.Empty<AnikiVideoBrowserItem>();
        private int watchedCount;
        private bool isSelected;

        public int SeasonNumber { get => seasonNumber; set => SetValue(ref seasonNumber, value); }
        public string Name { get => name; set => SetValue(ref name, value ?? string.Empty); }
        public IReadOnlyList<AnikiVideoBrowserItem> Episodes { get => episodes; set => SetValue(ref episodes, value ?? Array.Empty<AnikiVideoBrowserItem>()); }
        public int WatchedCount { get => watchedCount; set => SetValue(ref watchedCount, value); }
        public bool IsSelected { get => isSelected; set => SetValue(ref isSelected, value); }
        public int EpisodeCount => Episodes?.Count ?? 0;
        public string ProgressText => EpisodeCount <= 0 ? string.Empty : WatchedCount + " / " + EpisodeCount;

        public void NotifyProgressChanged()
        {
            OnPropertyChanged(nameof(EpisodeCount));
            OnPropertyChanged(nameof(ProgressText));
        }
    }
}
