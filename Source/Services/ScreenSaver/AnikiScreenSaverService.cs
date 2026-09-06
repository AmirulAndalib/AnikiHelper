using AnikiHelper.Services.Achievements;
using AnikiHelper.Services.SplashScreen;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AnikiHelper.Services.ScreenSaver
{
    internal sealed class AnikiScreenSaverService : IDisposable
    {
        private readonly IPlayniteAPI api;
        private readonly AnikiHelperSettings settings;
        private readonly ILogger logger;
        private readonly Func<bool> isThemeActive;
        private readonly Func<bool> isPlayniteForeground;
        private readonly Func<bool> isGameRunningOrLaunching;
        private readonly Func<bool> isBlockingUiOpen;
        private readonly Func<Game, string> resolveSplashImage;
        private readonly PlayniteAchievementsReader achievementsReader;
        private readonly Random random = new Random();
        private readonly List<Guid> recentlyShownGameIds = new List<Guid>();
        private readonly Dictionary<Guid, int> displayCounts = new Dictionary<Guid, int>();
        private DispatcherTimer idleTimer;
        private DispatcherTimer slideTimer;
        private AnikiScreenSaverWindow window;
        private DateTime lastActivityUtc = DateTime.UtcNow;
        private uint lastInputTick;
        private int slideLoadToken;
        private int slideLoadInProgress;
        private bool previewMode;
        private bool started;
        private bool disposed;

        public bool IsVisible => window != null && window.IsVisible;

        public AnikiScreenSaverService(
            IPlayniteAPI api,
            AnikiHelperSettings settings,
            ILogger logger,
            Func<bool> isThemeActive,
            Func<bool> isPlayniteForeground,
            Func<bool> isGameRunningOrLaunching,
            Func<bool> isBlockingUiOpen,
            Func<Game, string> resolveSplashImage)
        {
            this.api = api;
            this.settings = settings;
            this.logger = logger;
            this.isThemeActive = isThemeActive;
            this.isPlayniteForeground = isPlayniteForeground;
            this.isGameRunningOrLaunching = isGameRunningOrLaunching;
            this.isBlockingUiOpen = isBlockingUiOpen;
            this.resolveSplashImage = resolveSplashImage;
            achievementsReader = new PlayniteAchievementsReader(api, logger);
        }

        public void Start()
        {
            RunOnUi(() =>
            {
                if (disposed || started)
                {
                    return;
                }

                started = true;
                lastActivityUtc = DateTime.UtcNow;
                lastInputTick = GetLastInputTick();

                idleTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                idleTimer.Tick += IdleTimer_Tick;
                idleTimer.Start();
            });
        }

        public void Stop()
        {
            RunOnUi(() =>
            {
                started = false;

                if (idleTimer != null)
                {
                    idleTimer.Stop();
                    idleTimer.Tick -= IdleTimer_Tick;
                    idleTimer = null;
                }

                StopSlideTimer();
                CloseWindow(resetActivity: false);
            });
        }

        public void ShowPreview()
        {
            RunOnUi(() =>
            {
                MarkActivity();
                StartShowcase(true);
            });
        }

        public void StopCurrentScreenSaver()
        {
            RunOnUi(() => CloseWindow(resetActivity: true));
        }

        public bool HandleControllerInput(OnControllerButtonStateChangedArgs args)
        {
            if (args == null || args.State != ControllerInputState.Pressed)
            {
                return false;
            }

            MarkActivity();

            if (!IsVisible)
            {
                return false;
            }

            RunOnUi(() => CloseWindow(resetActivity: true));
            return true;
        }

        private void IdleTimer_Tick(object sender, EventArgs e)
        {
            if (disposed)
            {
                return;
            }

            var currentInputTick = GetLastInputTick();
            if (currentInputTick != 0 && currentInputTick != lastInputTick)
            {
                lastInputTick = currentInputTick;
                MarkActivity();

                if (IsVisible)
                {
                    CloseWindow(resetActivity: false);
                    return;
                }
            }

            if (IsVisible)
            {
                if (!CanRemainVisible())
                {
                    CloseWindow(resetActivity: true);
                }

                return;
            }

            if (!CanStartAutomatically())
            {
                return;
            }

            var delaySetting = settings?.ScreenSaverIdleDelayMinutes ?? 1;
            var idleDelay = delaySetting < 0
                ? TimeSpan.FromSeconds(Math.Abs(delaySetting))
                : TimeSpan.FromMinutes(Math.Max(1, Math.Min(120, delaySetting)));

            if (DateTime.UtcNow - lastActivityUtc >= idleDelay)
            {
                StartShowcase(false);
            }
        }

        private bool CanStartAutomatically()
        {
            try
            {
                return settings?.ScreenSaverEnabled == true &&
                       isThemeActive?.Invoke() == true &&
                       isPlayniteForeground?.Invoke() == true &&
                       isGameRunningOrLaunching?.Invoke() != true &&
                       isBlockingUiOpen?.Invoke() != true;
            }
            catch
            {
                return false;
            }
        }

        private bool CanRemainVisible()
        {
            if (previewMode)
            {
                return isGameRunningOrLaunching?.Invoke() != true;
            }

            return CanStartAutomatically();
        }

        private void StartShowcase(bool isPreview)
        {
            if (disposed || IsVisible)
            {
                return;
            }

            if (!isPreview && !CanStartAutomatically())
            {
                return;
            }

            previewMode = isPreview;
            window = new AnikiScreenSaverWindow();
            window.DismissRequested += Window_DismissRequested;
            window.Closed += Window_Closed;

            try
            {
                var owner = api?.Dialogs?.GetCurrentAppWindow();
                if (owner != null && owner != window)
                {
                    window.Owner = owner;
                }
            }
            catch
            {
            }

            try
            {
                SetScreenSaverActive(true);
                window.Show();
                window.Activate();
                window.Focus();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][ScreenSaver] Failed to show the ScreenSaver window.");
                CloseWindow(resetActivity: true);
                return;
            }

            ConfigureSlideTimer();
            _ = ShowNextSlideAsync();
        }

        private void ConfigureSlideTimer()
        {
            StopSlideTimer();

            var intervalSeconds = Math.Max(5, Math.Min(300, settings?.ScreenSaverChangeIntervalSeconds ?? 15));
            slideTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(intervalSeconds)
            };
            slideTimer.Tick += SlideTimer_Tick;
            slideTimer.Start();
        }

        private void StopSlideTimer()
        {
            if (slideTimer == null)
            {
                return;
            }

            slideTimer.Stop();
            slideTimer.Tick -= SlideTimer_Tick;
            slideTimer = null;
        }

        private void SlideTimer_Tick(object sender, EventArgs e)
        {
            _ = ShowNextSlideAsync();
        }

        private async Task ShowNextSlideAsync()
        {
            if (Interlocked.Exchange(ref slideLoadInProgress, 1) != 0)
            {
                return;
            }

            var token = Interlocked.Increment(ref slideLoadToken);

            try
            {
                var game = SelectNextGame(out var backgroundPath);
                if (game == null)
                {
                    logger?.Warn("[AnikiHelper][ScreenSaver] No eligible game with a usable background was found.");
                    CloseWindow(resetActivity: true);
                    return;
                }

                var slide = await Task.Run(() => BuildSlide(game, backgroundPath));
                if (slide == null || token != slideLoadToken)
                {
                    return;
                }

                RunOnUi(() =>
                {
                    if (token != slideLoadToken || window == null || !window.IsVisible)
                    {
                        return;
                    }

                    var intervalSeconds = Math.Max(5, Math.Min(300, settings?.ScreenSaverChangeIntervalSeconds ?? 15));
                    window.ShowSlide(
                        slide,
                        settings?.ScreenSaverShowLogo ?? true,
                        settings?.ScreenSaverShowInfoCard ?? true,
                        settings?.ScreenSaverAnimateBackground ?? true,
                        settings?.ScreenSaverUseFadeTransitions ?? true,
                        TimeSpan.FromSeconds(intervalSeconds));
                });
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][ScreenSaver] Failed to prepare the next ScreenSaver game.");
            }
            finally
            {
                Interlocked.Exchange(ref slideLoadInProgress, 0);
            }
        }

        private Game SelectNextGame(out string backgroundPath)
        {
            backgroundPath = string.Empty;
            var candidates = GetCandidateGames();
            if (candidates.Count == 0)
            {
                return null;
            }

            var source = settings?.ScreenSaverSource ?? ScreenSaverSource.InstalledGames;
            var attempts = Math.Min(Math.Max(12, candidates.Count), 80);

            for (var attempt = 0; attempt < attempts; attempt++)
            {
                Game selected;

                if (source == ScreenSaverSource.WeightedRandom)
                {
                    selected = SelectWeighted(candidates);
                }
                else
                {
                    var pool = candidates
                        .Where(game => !recentlyShownGameIds.Contains(game.Id))
                        .ToList();

                    if (pool.Count == 0)
                    {
                        pool = candidates;
                    }

                    selected = pool[random.Next(pool.Count)];
                }

                if (selected == null)
                {
                    continue;
                }

                var imagePath = ResolveBackgroundPath(selected);
                if (string.IsNullOrWhiteSpace(imagePath))
                {
                    candidates.Remove(selected);
                    if (candidates.Count == 0)
                    {
                        return null;
                    }

                    continue;
                }

                backgroundPath = imagePath;
                RegisterDisplayedGame(selected.Id);
                return selected;
            }

            return null;
        }

        private List<Game> GetCandidateGames()
        {
            IEnumerable<Game> games;
            var source = settings?.ScreenSaverSource ?? ScreenSaverSource.InstalledGames;

            if (source == ScreenSaverSource.CurrentFilter)
            {
                games = GetCurrentFilteredGames();
            }
            else
            {
                games = api?.Database?.Games?.Where(game => game != null) ?? Enumerable.Empty<Game>();
            }

            games = games.Where(game => game != null && !game.Hidden);

            switch (source)
            {
                case ScreenSaverSource.InstalledGames:
                    games = games.Where(game => game.IsInstalled);
                    break;

                case ScreenSaverSource.Favorites:
                    games = games.Where(game => game.Favorite);
                    break;

                case ScreenSaverSource.NeverPlayed:
                    games = games.Where(game => game.Playtime == 0);
                    break;

                case ScreenSaverSource.RecentlyAdded:
                    var recentlyAdded = games
                        .Where(game => game.Added.HasValue)
                        .OrderByDescending(game => game.Added.Value)
                        .Take(100)
                        .ToList();
                    games = recentlyAdded;
                    break;

                case ScreenSaverSource.AllGames:
                case ScreenSaverSource.CurrentFilter:
                case ScreenSaverSource.WeightedRandom:
                default:
                    break;
            }

            return games.ToList();
        }

        private IEnumerable<Game> GetCurrentFilteredGames()
        {
            try
            {
                var mainView = api?.MainView;
                if (mainView == null)
                {
                    return Enumerable.Empty<Game>();
                }

                var property = mainView.GetType().GetProperty("FilteredGames");
                var rawValue = property?.GetValue(mainView, null);

                if (rawValue == null)
                {
                    var interfaceProperty = mainView.GetType()
                        .GetInterfaces()
                        .Select(type => type.GetProperty("FilteredGames"))
                        .FirstOrDefault(value => value != null);
                    rawValue = interfaceProperty?.GetValue(mainView, null);
                }

                if (rawValue == null)
                {
                    var method = mainView.GetType().GetMethod("GetFilteredGames", Type.EmptyTypes);
                    rawValue = method?.Invoke(mainView, null);
                }

                if (rawValue is IEnumerable<Game> typedGames)
                {
                    return typedGames.ToList();
                }

                if (rawValue is IEnumerable enumerable)
                {
                    return enumerable.Cast<object>().OfType<Game>().ToList();
                }
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][ScreenSaver] Failed to read the current filtered game list.");
            }

            return Enumerable.Empty<Game>();
        }

        private Game SelectWeighted(IList<Game> games)
        {
            if (games == null || games.Count == 0)
            {
                return null;
            }

            var pool = games
                .Where(game => game != null && (!recentlyShownGameIds.Contains(game.Id) || games.Count <= recentlyShownGameIds.Count + 1))
                .ToList();

            if (pool.Count == 0)
            {
                pool = games.Where(game => game != null).ToList();
            }

            var weighted = new List<Tuple<Game, double>>();
            double totalWeight = 0;
            var now = DateTime.UtcNow;

            foreach (var game in pool)
            {
                var weight = 1.0;

                if (game.IsInstalled)
                {
                    weight += 3.0;
                }

                if (game.Favorite)
                {
                    weight += 2.5;
                }

                if (game.Playtime == 0)
                {
                    weight += 2.0;
                }

                if (game.Added.HasValue && now - game.Added.Value.ToUniversalTime() <= TimeSpan.FromDays(90))
                {
                    weight += 1.5;
                }

                if (displayCounts.TryGetValue(game.Id, out var count))
                {
                    weight /= 1.0 + (count * 0.55);
                }

                totalWeight += Math.Max(0.05, weight);
                weighted.Add(Tuple.Create(game, Math.Max(0.05, weight)));
            }

            var roll = random.NextDouble() * totalWeight;
            foreach (var entry in weighted)
            {
                roll -= entry.Item2;
                if (roll <= 0)
                {
                    return entry.Item1;
                }
            }

            return weighted.LastOrDefault()?.Item1;
        }

        private void RegisterDisplayedGame(Guid gameId)
        {
            recentlyShownGameIds.Remove(gameId);
            recentlyShownGameIds.Add(gameId);

            while (recentlyShownGameIds.Count > 10)
            {
                recentlyShownGameIds.RemoveAt(0);
            }

            if (!displayCounts.ContainsKey(gameId))
            {
                displayCounts[gameId] = 0;
            }

            displayCounts[gameId]++;
        }

        private ScreenSaverSlide BuildSlide(Game game, string backgroundPath)
        {
            if (game == null)
            {
                return null;
            }

            var background = LoadImage(backgroundPath, 3840);
            if (background == null)
            {
                return null;
            }

            var logo = settings?.ScreenSaverShowLogo == true
                ? LoadImage(GetLogoPath(game), 1200)
                : null;

            PlayniteAchievementsSummary achievements = null;
            if (settings?.ScreenSaverShowInfoCard == true)
            {
                achievements = achievementsReader.LoadSummary(game);
            }

            return new ScreenSaverSlide
            {
                Game = game,
                BackgroundImage = background,
                LogoImage = logo,
                GameName = game.Name ?? string.Empty,
                PlaytimeLabel = Loc("ScreenSaver_Info_Playtime", "Play time"),
                PlaytimeValue = FormatPlaytime(game.Playtime),
                AchievementsLabel = Loc("ScreenSaver_Info_Achievements", "Achievements"),
                AchievementsValue = achievements != null && achievements.Total > 0
                    ? string.Format(CultureInfo.CurrentCulture, "{0} / {1}", achievements.Unlocked, achievements.Total)
                    : Loc("ScreenSaver_Info_NoAchievements", "No data"),
                LastPlayedLabel = Loc("ScreenSaver_Info_LastPlayed", "Last played"),
                LastPlayedValue = game.LastActivity.HasValue
                    ? game.LastActivity.Value.ToString("d", CultureInfo.CurrentCulture)
                    : Loc("ScreenSaver_Info_NeverPlayed", "Never"),
                StatusValue = game.IsInstalled
                    ? Loc("ScreenSaver_Info_Installed", "Installed")
                    : Loc("ScreenSaver_Info_NotInstalled", "Not installed")
            };
        }

        private string ResolveBackgroundPath(Game game)
        {
            if (game == null)
            {
                return string.Empty;
            }

            if (settings?.ScreenSaverUseSplashImages == true)
            {
                try
                {
                    var splashPath = resolveSplashImage?.Invoke(game);
                    if (IsSupportedStillImage(splashPath))
                    {
                        return splashPath;
                    }
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][ScreenSaver] Splash image resolution failed.");
                }
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(game.BackgroundImage))
                {
                    var path = api?.Database?.GetFullFilePath(game.BackgroundImage);
                    if (IsSupportedStillImage(path))
                    {
                        return path;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static bool IsSupportedStillImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jfif", StringComparison.OrdinalIgnoreCase);
        }

        private string GetLogoPath(Game game)
        {
            if (game == null)
            {
                return string.Empty;
            }

            try
            {
                var gameId = game.Id.ToString();
                var roots = new[]
                {
                    api?.Paths?.ConfigurationPath,
                    api?.Paths?.ApplicationPath,
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Playnite")
                };

                foreach (var root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root))
                    {
                        continue;
                    }

                    var gameFolder = Path.Combine(
                        root,
                        "ExtraMetadata",
                        "games",
                        gameId);

                    if (!Directory.Exists(gameFolder))
                    {
                        continue;
                    }

                    var candidates = new[]
                    {
                        Path.Combine(gameFolder, "logo.png"),
                        Path.Combine(gameFolder, "logo.jpg"),
                        Path.Combine(gameFolder, "logo.jpeg"),
                        Path.Combine(gameFolder, "logo.webp")
                    };

                    foreach (var candidate in candidates)
                    {
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][ScreenSaver] Failed to resolve ExtraMetadata logo.");
            }

            return string.Empty;
        }

        private static ImageSource LoadImage(string path, int decodePixelWidth)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);

                if (decodePixelWidth > 0)
                {
                    bitmap.DecodePixelWidth = decodePixelWidth;
                }

                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private string FormatPlaytime(ulong totalSeconds)
        {
            var totalMinutes = totalSeconds / 60UL;
            if (totalMinutes < 60)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    Loc("ScreenSaver_Time_Minutes", "{0} min"),
                    totalMinutes);
            }

            var hours = totalMinutes / 60UL;
            var minutes = totalMinutes % 60UL;
            return string.Format(
                CultureInfo.CurrentCulture,
                Loc("ScreenSaver_Time_HoursMinutes", "{0} h {1} min"),
                hours,
                minutes);
        }

        private static string Loc(string key, string fallback)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    return dispatcher.Invoke(new Func<string>(() => Loc(key, fallback)));
                }

                var value = ResourceProvider.GetString(key);
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        private void Window_DismissRequested()
        {
            CloseWindow(resetActivity: true);
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (window != null)
            {
                window.DismissRequested -= Window_DismissRequested;
                window.Closed -= Window_Closed;
            }

            window = null;
            previewMode = false;
            SetScreenSaverActive(false);
            StopSlideTimer();
            Interlocked.Increment(ref slideLoadToken);
        }

        private void CloseWindow(bool resetActivity)
        {
            if (resetActivity)
            {
                MarkActivity();
            }

            StopSlideTimer();
            Interlocked.Increment(ref slideLoadToken);

            var currentWindow = window;
            window = null;
            previewMode = false;
            SetScreenSaverActive(false);

            if (currentWindow != null)
            {
                currentWindow.DismissRequested -= Window_DismissRequested;
                currentWindow.Closed -= Window_Closed;
                currentWindow.CloseImmediately();
            }
        }

        private void SetScreenSaverActive(bool active)
        {
            try
            {
                if (settings != null)
                {
                    settings.IsScreenSaverActive = active;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][ScreenSaver] Failed to update the ScreenSaver audio state.");
            }
        }

        private void MarkActivity()
        {
            lastActivityUtc = DateTime.UtcNow;
        }

        private void RunOnUi(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Stop();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private static uint GetLastInputTick()
        {
            try
            {
                var info = new LASTINPUTINFO
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO))
                };

                return GetLastInputInfo(ref info) ? info.dwTime : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
