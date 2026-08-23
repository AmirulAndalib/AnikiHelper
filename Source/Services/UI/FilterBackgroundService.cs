using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace AnikiHelper.Services.UI
{
    /// <summary>Resolves the background for the active named filter preset.</summary>
    public sealed class FilterBackgroundService : IDisposable
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(
            new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" },
            StringComparer.OrdinalIgnoreCase);

        private readonly IPlayniteAPI api;
        private readonly AnikiHelperSettings settings;
        private readonly ILogger logger;
        private readonly Func<string> themePathProvider;

        private DispatcherTimer refreshTimer;
        private string lastPresetId = string.Empty;
        private string lastPresetName = string.Empty;
        private string lastCustomFolder = string.Empty;
        private string lastThemePath = string.Empty;
        private string lastResolvedPath = string.Empty;
        private DateTime nextMissingPathProbeUtc = DateTime.MinValue;
        private bool invalidated = true;

        public FilterBackgroundService(
            IPlayniteAPI api,
            AnikiHelperSettings settings,
            ILogger logger,
            Func<string> themePathProvider)
        {
            this.api = api;
            this.settings = settings;
            this.logger = logger;
            this.themePathProvider = themePathProvider;
        }

        public void Start()
        {
            try
            {
                var dispatcher = api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return;
                }

                if (!dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(new Action(Start), DispatcherPriority.Loaded);
                    return;
                }

                if (refreshTimer == null)
                {
                    refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(750)
                    };
                    refreshTimer.Tick += RefreshTimer_Tick;
                }

                invalidated = true;
                RefreshCore();
                refreshTimer.Start();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][FilterBackground] Failed to start filter background service.");
            }
        }

        public void Stop()
        {
            try
            {
                if (refreshTimer != null)
                {
                    refreshTimer.Stop();
                    refreshTimer.Tick -= RefreshTimer_Tick;
                    refreshTimer = null;
                }
            }
            catch
            {
            }
        }

        public void Invalidate()
        {
            invalidated = true;
            RefreshNow();
        }

        public void RefreshNow()
        {
            try
            {
                var dispatcher = api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return;
                }

                if (!dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(new Action(RefreshCore), DispatcherPriority.Background);
                    return;
                }

                RefreshCore();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][FilterBackground] Failed to refresh filter background.");
            }
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshCore();
        }

        private void RefreshCore()
        {
            try
            {
                var activePreset = GetActiveFilterPreset();
                var presetId = activePreset?.Id.ToString() ?? string.Empty;
                var presetName = activePreset?.Name ?? string.Empty;
                var customFolder = settings?.CustomFilterBackgroundsFolder ?? string.Empty;
                var themePath = themePathProvider?.Invoke() ?? string.Empty;

                var currentPathMissing = !string.IsNullOrWhiteSpace(lastResolvedPath) && !File.Exists(lastResolvedPath);
                var missingPathProbeDue = string.IsNullOrWhiteSpace(lastResolvedPath) &&
                                          DateTime.UtcNow >= nextMissingPathProbeUtc;
                var shouldResolve = invalidated ||
                                    currentPathMissing ||
                                    missingPathProbeDue ||
                                    !string.Equals(lastPresetId, presetId, StringComparison.OrdinalIgnoreCase) ||
                                    !string.Equals(lastPresetName, presetName, StringComparison.Ordinal) ||
                                    !string.Equals(lastCustomFolder, customFolder, StringComparison.OrdinalIgnoreCase) ||
                                    !string.Equals(lastThemePath, themePath, StringComparison.OrdinalIgnoreCase);

                if (!shouldResolve)
                {
                    return;
                }

                var resolvedPath = ResolveBackgroundPath(presetName, customFolder, themePath);

                lastPresetId = presetId;
                lastPresetName = presetName;
                lastCustomFolder = customFolder;
                lastThemePath = themePath;
                lastResolvedPath = resolvedPath;
                nextMissingPathProbeUtc = string.IsNullOrWhiteSpace(resolvedPath)
                    ? DateTime.UtcNow.AddSeconds(5)
                    : DateTime.MaxValue;
                invalidated = false;

                if (settings != null)
                {
                    settings.ActiveFilterPresetName = presetName;
                    settings.ActiveFilterBackgroundPath = resolvedPath;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][FilterBackground] Failed to resolve active filter background.");
            }
        }

        private FilterPreset GetActiveFilterPreset()
        {
            try
            {
                if (api?.MainView == null || api.Database?.FilterPresets == null)
                {
                    return null;
                }

                // GetActiveFilterPreset returns the ID of a named preset. Boxing the
                // result keeps this code compatible whether the SDK exposes Guid or Guid?.
                var activePresetIdObject = (object)api.MainView.GetActiveFilterPreset();
                if (activePresetIdObject == null)
                {
                    return null;
                }

                if (!Guid.TryParse(activePresetIdObject.ToString(), out var activePresetId) ||
                    activePresetId == Guid.Empty)
                {
                    return null;
                }

                return api.Database.FilterPresets.FirstOrDefault(x => x != null && x.Id == activePresetId);
            }
            catch
            {
                return null;
            }
        }

        private string ResolveBackgroundPath(string presetName, string customFolder, string themePath)
        {
            var themeFolder = string.IsNullOrWhiteSpace(themePath)
                ? string.Empty
                : Path.Combine(themePath, "Icons", "FilterBackground");

            var legacyThemeFolder = string.IsNullOrWhiteSpace(themePath)
                ? string.Empty
                : Path.Combine(themePath, "Images", "FilterBackgrounds");

            // Priority: exact user image -> exact theme image -> legacy theme image -> user Default -> theme Default -> legacy Default.
            var path = FindImage(customFolder, presetName);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            path = FindImage(themeFolder, presetName);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            path = FindImage(legacyThemeFolder, presetName);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            path = FindImage(customFolder, "Default");
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            path = FindImage(themeFolder, "Default");
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            path = FindImage(legacyThemeFolder, "Default");
            return path ?? string.Empty;
        }

        private static string FindImage(string folder, string fileNameWithoutExtension)
        {
            if (string.IsNullOrWhiteSpace(folder) ||
                string.IsNullOrWhiteSpace(fileNameWithoutExtension) ||
                !Directory.Exists(folder))
            {
                return string.Empty;
            }

            try
            {
                var match = Directory
                    .EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                    .FirstOrDefault(path => string.Equals(
                        Path.GetFileNameWithoutExtension(path),
                        fileNameWithoutExtension,
                        StringComparison.OrdinalIgnoreCase));

                return string.IsNullOrWhiteSpace(match)
                    ? string.Empty
                    : Path.GetFullPath(match).Replace("\\", "/");
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
