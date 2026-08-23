using Playnite.SDK.Models;
using System.Windows.Media;

namespace AnikiHelper.Services.ScreenSaver
{
    internal sealed class ScreenSaverSlide
    {
        public Game Game { get; set; }
        public ImageSource BackgroundImage { get; set; }
        public ImageSource LogoImage { get; set; }
        public string GameName { get; set; } = string.Empty;
        public string PlaytimeLabel { get; set; } = string.Empty;
        public string PlaytimeValue { get; set; } = string.Empty;
        public string AchievementsLabel { get; set; } = string.Empty;
        public string AchievementsValue { get; set; } = string.Empty;
        public string LastPlayedLabel { get; set; } = string.Empty;
        public string LastPlayedValue { get; set; } = string.Empty;
        public string StatusValue { get; set; } = string.Empty;
    }
}
