using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AnikiHelper.Services.VideoPlayer
{
    /// <summary>
    /// Persists the lightweight watched/unwatched state for files opened by Aniki Video Center.
    /// Resume positions deliberately remain in AnikiVideoResumeStore.
    /// </summary>
    internal sealed class AnikiVideoWatchStore
    {
        private sealed class WatchEntry
        {
            public string Path { get; set; }
            public DateTime WatchedUtc { get; set; }
        }

        private readonly object sync = new object();
        private readonly ILogger logger;
        private readonly string filePath;
        private Dictionary<string, WatchEntry> entries =
            new Dictionary<string, WatchEntry>(StringComparer.OrdinalIgnoreCase);
        private Task lastSaveTask = Task.CompletedTask;

        public AnikiVideoWatchStore(string pluginUserDataPath, ILogger logger)
        {
            this.logger = logger ?? LogManager.GetLogger();
            var root = AnikiVideoStoragePaths.GetPlayerStateRoot(pluginUserDataPath, this.logger);
            filePath = Path.Combine(root, "WatchState.json");
            Load();
        }

        public bool IsWatched(string path)
        {
            var key = Normalize(path);
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (sync)
            {
                return entries.ContainsKey(key);
            }
        }

        public void SetWatched(string path, bool watched)
        {
            var normalizedPath = NormalizePath(path);
            var key = Normalize(normalizedPath);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (sync)
            {
                if (!watched)
                {
                    entries.Remove(key);
                    return;
                }

                entries[key] = new WatchEntry
                {
                    Path = normalizedPath,
                    WatchedUtc = DateTime.UtcNow
                };

                if (entries.Count > 1500)
                {
                    foreach (var oldKey in entries
                        .OrderByDescending(x => x.Value?.WatchedUtc ?? DateTime.MinValue)
                        .Skip(1200)
                        .Select(x => x.Key)
                        .ToList())
                    {
                        entries.Remove(oldKey);
                    }
                }
            }
        }

        public bool Toggle(string path)
        {
            var next = !IsWatched(path);
            SetWatched(path, next);
            return next;
        }

        public Task SaveAsync()
        {
            List<WatchEntry> snapshot;
            lock (sync)
            {
                snapshot = entries.Values
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                    .Select(x => new WatchEntry { Path = x.Path, WatchedUtc = x.WatchedUtc })
                    .OrderByDescending(x => x.WatchedUtc)
                    .ToList();
            }

            var previous = lastSaveTask ?? Task.CompletedTask;
            lastSaveTask = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { }

                try
                {
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                    var temp = filePath + ".tmp";
                    File.WriteAllText(temp, json);

                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    File.Move(temp, filePath);
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to save watched state.");
                }
            });

            return lastSaveTask;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return;
                }

                var json = File.ReadAllText(filePath);
                var list = JsonConvert.DeserializeObject<List<WatchEntry>>(json) ?? new List<WatchEntry>();
                entries = list
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                    .GroupBy(x => Normalize(x.Path), StringComparer.OrdinalIgnoreCase)
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                    .ToDictionary(
                        x => x.Key,
                        x => x.OrderByDescending(y => y.WatchedUtc).First(),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to load watched state.");
                entries = new Dictionary<string, WatchEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path).Trim();
            }
            catch
            {
                return (path ?? string.Empty).Trim();
            }
        }

        private static string Normalize(string path)
        {
            return NormalizePath(path).ToUpperInvariant();
        }
    }
}
